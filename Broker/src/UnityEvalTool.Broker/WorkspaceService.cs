#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace YuzeToolkit.Eval.Broker;

internal sealed class WorkspaceService(BrokerConfiguration config, BrokerRegistry registry)
{
    public BrokerConfiguration Configuration => config;
    private readonly WorkspaceDesktop _desktop = new(config.Workspace, registry);
    private bool DesktopInstalled => config.Workspace.DesktopDriverPath is { } path && File.Exists(path);

    private bool DesktopAvailable(WorkspaceSession session) => session.Exporting
        ? DesktopInstalled
        : session.Peers.Values.Any(peer => peer.State == "online" && peer.DesktopToolsAvailable);

    public async Task<JsonArray> ListToolsAsync(WorkspaceSession session, CancellationToken token)
    {
        await session.Gate.WaitAsync(token);
        try
        {
            var available = DesktopAvailable(session);
            session.UpdateDesktopTools(available);
            return WorkspaceTools.List(available);
        }
        finally { session.Gate.Release(); }
    }

    public async Task<JsonObject> CallAsync(WorkspaceSession session, string name, JsonObject args, CancellationToken cancellationToken)
    {
        if (name == "workspace_events")
        {
            var after = Number(args, "after");
            var wait = Number(args, "waitSeconds");
            if (wait is < 0 or > 30) throw WorkspaceFiles.Error("InvalidRequest", "waitSeconds must be 0–30.");
            var until = DateTimeOffset.UtcNow.AddSeconds(wait);
            JsonObject events;
            do
            {
                events = session.Events(after);
                if (events["events"]!.AsArray().Count != 0 || DateTimeOffset.UtcNow >= until) break;
                await Task.Delay(200, cancellationToken);
            } while (true);
            return WorkspaceTools.Result(events);
        }
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.Closed) throw WorkspaceFiles.Error("ContextHandleInvalid", "This MCP session was closed. Initialize again.");
            if (name == "workspace_list") return await ListAsync(session, cancellationToken);
            if (name == "workspace_connect") return await ConnectAsync(session, args, cancellationToken);
            if (name == "workspace_status" && !args.ContainsKey("contextHandle")) return WorkspaceTools.Result(HostSnapshot(session));
            if (name is "desktop_observe" or "desktop_act")
            {
                if (args.ContainsKey("hostId") && (session.Exporting || args.ContainsKey("contextHandle")))
                    throw WorkspaceFiles.Error("InvalidDesktopTarget", "Use either a remote hostId or a workspace context. Network exports cannot forward a hostId.");
                if (!args.ContainsKey("contextHandle"))
                {
                    if (!session.Exporting)
                    {
                        var peer = await PeerAsync(session, Required(args, "hostId"), cancellationToken);
                        if (peer.State != "online") throw WorkspaceFiles.Error("RemoteUnavailable", "The remote desktop host is offline. No request was sent.");
                        if (!peer.DesktopToolsAvailable) throw WorkspaceFiles.Error("ToolUnavailable", "The remote host has no installed, configured Cua Driver.");
                        var forwarded = (JsonObject)args.DeepClone(); forwarded.Remove("hostId");
                        return await InvokeAsync(session, peer, name, forwarded, cancellationToken);
                    }
                    if (!DesktopInstalled) throw WorkspaceFiles.Error("ToolUnavailable", "This host has no installed, configured Cua Driver.");
                    return name == "desktop_observe"
                        ? await _desktop.ObserveAsync(session.Id, null, args, cancellationToken)
                        : await _desktop.ActAsync(session.Id, null, args, cancellationToken);
                }
            }
            if (name is "desktop_observe" or "desktop_act" && !DesktopAvailable(session))
                throw WorkspaceFiles.Error("ToolUnavailable", "Desktop tools are not available. The remote host must have an installed, configured Cua Driver; discover or connect that host first.");
            var context = session.RequireContext(Required(args, "contextHandle"));
            if (context.Peer != null)
            {
                if (context.RemoteInstanceId != context.Peer.InstanceId)
                    throw WorkspaceFiles.Error("ContextHandleInvalid", "The remote Broker process was replaced. Connect again.");
                if (name == "workspace_status" && context.Peer.State != "online")
                {
                    var stale = (JsonObject)context.Snapshot!.DeepClone();
                    stale["isStale"] = true; stale["state"] = context.Peer.State; stale["error"] = context.Peer.Error;
                    stale["lastSeenAtUtc"] = context.Peer.LastSeenAtUtc?.ToString("O");
                    return WorkspaceTools.Result(stale);
                }
                if (context.Peer.State != "online")
                    throw WorkspaceFiles.Error("RemoteUnavailable", "The remote is known offline. No request was sent; wait for recovery or reconnect explicitly.");
                var forwarded = (JsonObject)args.DeepClone(); forwarded["contextHandle"] = context.RemoteHandle;
                var result = await InvokeAsync(session, context.Peer, name, forwarded, cancellationToken);
                if (name == "workspace_status" && result["isError"]?.GetValue<bool>() != true)
                {
                    var snapshot = Payload(result); snapshot["contextHandle"] = context.Handle;
                    DescribeRemoteProject(snapshot, context.Peer.Id);
                    context.Snapshot = (JsonObject)snapshot.DeepClone();
                    result = WorkspaceTools.Result(snapshot);
                }
                if (name == "workspace_disconnect" && result["isError"]?.GetValue<bool>() != true)
                {
                    session.Contexts.TryRemove(context.Handle, out _);
                    session.Publish("workspace.closed", new JsonObject { ["hostId"] = context.Peer.Id, ["workspaceId"] = context.WorkspaceId });
                }
                return result;
            }
            var files = context.Files!;
            JsonNode data;
            switch (name)
            {
                case "workspace_status": data = Describe(context); break;
                case "workspace_disconnect":
                    context.Dispose(); session.Contexts.TryRemove(context.Handle, out _);
                    session.Publish("workspace.closed", new JsonObject { ["workspaceId"] = context.WorkspaceId });
                    data = new JsonObject { ["closed"] = true }; break;
                case "workspace_get_guidance": data = files.Guidance(Text(args, "path", context.Cwd)); break;
                case "workspace_read_skill": data = files.ReadSkill(Required(args, "skillId"), Text(args, "reference"), Number(args, "offset"), Number(args, "length")); break;
                case "file_read": data = files.Read(Required(args, "path"), Number(args, "offset"), Number(args, "length")); break;
                case "file_search": data = files.Search(Text(args, "path", context.Cwd), Required(args, "query"), Number(args, "offset"), Number(args, "limit", 100)); break;
                case "file_apply_patch": data = files.ApplyPatch(Required(args, "path"), Required(args, "expectedVersion"), args["edits"]?.AsArray() ?? throw WorkspaceFiles.Error("InvalidRequest", "edits is required.")); break;
                case "file_view_image": return WorkspaceTools.Result(new JsonObject { ["path"] = files.Resolve(Required(args, "path")), ["workspaceId"] = context.WorkspaceId }, new JsonArray((JsonNode)files.Image(Required(args, "path"))));
                case "terminal_exec":
                    if (context.Tasks.Count >= config.Workspace.MaxTasksPerContext) throw WorkspaceFiles.Error("TaskLimitReached", "This context reached maxTasksPerContext; close it after collecting output.");
                    var task = new WorkspaceTerminal(files, Text(args, "cwd", context.Cwd), args, config.Workspace,
                        (type, detail) => { detail["workspaceId"] = context.WorkspaceId; session.Publish(type, detail); });
                    context.Tasks[task.Id] = task;
                    data = task.Snapshot(0, config.Workspace.MaxTextCharacters); break;
                case "terminal_poll": data = RequireTask(context, args).Snapshot(Number(args, "offset"), Number(args, "maxCharacters", config.Workspace.MaxTextCharacters)); break;
                case "terminal_input":
                    await RequireTask(context, args).WriteAsync(Text(args, "text"), args["close"]?.GetValue<bool>() == true, cancellationToken);
                    data = RequireTask(context, args).Snapshot(0, 0); break;
                case "terminal_cancel": data = await RequireTask(context, args).CancelAsync(); break;
                case "desktop_observe": return await _desktop.ObserveAsync(session.Id, context, args, cancellationToken);
                case "desktop_act": return await _desktop.ActAsync(session.Id, context, args, cancellationToken);
                default: throw WorkspaceFiles.Error("UnknownTool", "The requested remote workspace tool does not exist.");
            }
            return WorkspaceTools.Result(data);
        }
        finally { session.UpdateDesktopTools(DesktopAvailable(session)); session.Gate.Release(); }
    }

    private JsonObject HostSnapshot(WorkspaceSession session) => new()
    {
        ["brokerId"] = config.BrokerId, ["brokerInstanceId"] = BrokerHost.InstanceId,
        ["view"] = session.Exporting ? "local" : "remote", ["expiresAtUtc"] = session.ExpiresAtUtc.ToString("O"),
        ["desktopToolsAvailable"] = DesktopAvailable(session),
        ["hosts"] = new JsonArray(session.Peers.Values.Select(p => (JsonNode)new JsonObject
        { ["hostId"] = p.Id, ["state"] = p.State, ["error"] = p.Error, ["lastSeenAtUtc"] = p.LastSeenAtUtc?.ToString("O") }).ToArray())
    };

    private async Task<JsonObject> ListAsync(WorkspaceSession session, CancellationToken token)
    {
        var result = HostSnapshot(session);
        var projects = new JsonArray(); var errors = new JsonArray();
        if (session.Exporting)
        {
            foreach (var project in config.Projects)
                projects.Add((JsonNode)DescribeProject(project));
        }
        else
        {
            foreach (var remote in config.Remotes.Where(p => p.Enabled))
            {
                try
                {
                    var peer = await PeerAsync(session, remote.Id, token);
                    var payload = Payload(await InvokeAsync(session, peer, "workspace_list", new JsonObject(), token));
                    ValidatePeer(peer, payload);
                    var exported = payload["projects"] as JsonArray ?? throw WorkspaceFiles.Error("RemoteIncompatible", "The peer returned no workspace array.");
                    foreach (var entry in exported)
                    {
                        if (entry is not JsonObject project || Text(project, "source") != "local")
                            throw WorkspaceFiles.Error("RecursiveRouteRejected", "The peer returned a workspace that is not its own local export.");
                        var item = (JsonObject)project.DeepClone(); DescribeRemoteProject(item, remote.Id); projects.Add((JsonNode)item);
                    }
                }
                catch (Exception ex) when (RemoteFailure(ex))
                { errors.Add((JsonNode)new JsonObject { ["hostId"] = remote.Id, ["error"] = ex is BrokerOperationException operation ? operation.Code : "RemoteUnavailable", ["message"] = ex.Message }); }
            }
        }
        result = HostSnapshot(session);
        result["projects"] = projects; result["errors"] = errors;
        return WorkspaceTools.Result(result);
    }

    private async Task<JsonObject> ConnectAsync(WorkspaceSession session, JsonObject args, CancellationToken token)
    {
        if (session.Contexts.Count >= config.Workspace.MaxContextsPerSession) throw WorkspaceFiles.Error("ContextLimitReached", "Close an existing context first.");
        var id = Required(args, "workspaceId");
        WorkspaceContext context;
        JsonObject snapshot;
        if (session.Exporting)
        {
            if (args.ContainsKey("hostId")) throw WorkspaceFiles.Error("RecursiveRouteRejected", "Network workspaces export local projects only; hostId is not accepted.");
            var project = config.Projects.SingleOrDefault(p => p.Id == id)
                ?? throw WorkspaceFiles.Error("WorkspaceNotFound", "This workspace is not explicitly shared.");
            var files = new WorkspaceFiles(project, config.Workspace);
            context = new WorkspaceContext(id, files.DirectoryPath(Text(args, "cwd", "."))) { Files = files, Definition = project };
            snapshot = Describe(context);
            snapshot["guidance"] = files.Guidance(context.Cwd);
            snapshot["skills"] = files.Skills();
        }
        else
        {
            var peer = await PeerAsync(session, Required(args, "hostId"), token, reconnect: true);
            var forwarded = (JsonObject)args.DeepClone(); forwarded.Remove("hostId");
            snapshot = Payload(await InvokeAsync(session, peer, "workspace_connect", forwarded, token));
            ValidatePeer(peer, snapshot);
            if (Text(snapshot, "source") != "local") throw WorkspaceFiles.Error("RecursiveRouteRejected", "The peer did not connect its own local workspace.");
            context = new WorkspaceContext(id, Required(snapshot, "cwd"))
            { Peer = peer, RemoteHandle = Required(snapshot, "contextHandle"), RemoteInstanceId = peer.InstanceId };
            snapshot["contextHandle"] = context.Handle; DescribeRemoteProject(snapshot, peer.Id);
        }
        session.Contexts[context.Handle] = context;
        context.Snapshot = (JsonObject)snapshot.DeepClone();
        session.Publish("workspace.connected", new JsonObject { ["hostId"] = context.Peer?.Id, ["workspaceId"] = context.WorkspaceId });
        return WorkspaceTools.Result(snapshot);
    }

    private JsonObject Describe(WorkspaceContext context)
    {
        var snapshot = DescribeProject(context.Definition!);
        snapshot["contextHandle"] = context.Handle;
        snapshot["brokerId"] = config.BrokerId; snapshot["brokerInstanceId"] = BrokerHost.InstanceId;
        snapshot["view"] = "local"; snapshot["cwd"] = context.Cwd;
        snapshot["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        snapshot["isStale"] = false; snapshot["state"] = "online";
        snapshot["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
        snapshot["tasks"] = new JsonArray(context.Tasks.Values.Select(task => (JsonNode)task.Snapshot(0, 0)).ToArray());
        return snapshot;
    }

    private JsonObject DescribeProject(WorkspaceDefinition project)
    {
        var instances = registry.GetSnapshot().UnityInstances.Where(unity => unity.Source.Kind == "local" && unity.IsConnected &&
            WorkspaceFiles.SamePath(unity.ProjectPath, project.RootPath)).ToArray();
        var canEval = instances.Any(unity => BrokerStatePolicy.CanExecute(unity.Status));
        return new JsonObject
        {
            ["workspaceId"] = project.Id, ["name"] = project.Name, ["displayName"] = "[Local] " + project.Name,
            ["rootPath"] = project.RootPath, ["registeredBy"] = project.RegisteredBy, ["source"] = "local",
            ["directoryExists"] = Directory.Exists(project.RootPath),
            ["kind"] = instances.Length == 0 ? "workspace" : "unity",
            ["unityState"] = instances.Length == 0 ? "disconnected" : canEval ? "ready" : "busy",
            ["desktopAvailable"] = DesktopInstalled,
            ["capabilities"] = new JsonObject
            {
                ["filesRead"] = true, ["filesWrite"] = true,
                ["terminal"] = true, ["pty"] = true,
                ["unityConnected"] = instances.Length != 0, ["unityCanEval"] = canEval,
                ["desktopDriverConfigured"] = config.Workspace.DesktopDriverPath != null,
                ["desktopDriverInstalled"] = DesktopInstalled, ["desktopAvailable"] = DesktopInstalled,
                ["desktopExecutionMode"] = "background-default-foreground-explicit",
                ["shells"] = new JsonArray(OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh")
            },
            ["unityInstances"] = new JsonArray(instances.Select(unity => (JsonNode)new JsonObject
            {
                ["instanceId"] = unity.InstanceId, ["processId"] = unity.ProcessId, ["projectPath"] = unity.ProjectPath,
                ["phase"] = unity.Status.Phase, ["canEval"] = BrokerStatePolicy.CanExecute(unity.Status)
            }).ToArray())
        };
    }

    private static void DescribeRemoteProject(JsonObject snapshot, string hostId)
    {
        snapshot["hostId"] = hostId; snapshot["source"] = "remote";
        if (snapshot["name"] is { } name) snapshot["displayName"] = "[Remote:" + hostId + "] " + name.GetValue<string>();
        if (snapshot["unityInstances"] is JsonArray instances)
            foreach (var entry in instances)
            {
                var unity = entry as JsonObject ?? throw WorkspaceFiles.Error("RemoteIncompatible", "The peer returned an invalid Unity instance.");
                var localId = Required(unity, "instanceId");
                if (localId.StartsWith("remote:", StringComparison.Ordinal))
                    throw WorkspaceFiles.Error("RecursiveRouteRejected", "The peer returned an imported Unity instance.");
                unity["remoteInstanceId"] = localId;
                unity["instanceId"] = "remote:" + hostId + ":" + localId;
            }
    }

    private async Task<WorkspacePeer> PeerAsync(WorkspaceSession session, string hostId, CancellationToken token, bool reconnect = false)
    {
        if (session.Peers.TryGetValue(hostId, out var existing) && existing.State == "online") return existing;
        // Listing is observational: it must not dispose a peer session and kill its tasks.
        if (existing != null && !reconnect && session.Contexts.Values.Any(context => context.Peer == existing)) return existing;
        var remote = config.Remotes.SingleOrDefault(p => p.Id == hostId && p.Enabled)
            ?? throw WorkspaceFiles.Error("WorkspaceNotConfigured", "Select an enabled remote MCP host.");
        if (existing != null)
        {
            existing.InstanceId = null;
            existing.State = "invalidated";
            existing.Error = "ContextHandleInvalid";
            await existing.DisposeAsync(); session.Peers.Remove(hostId);
        }
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(config.Workspace.RequestTimeoutSeconds) };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new UriBuilder(remote.Scheme, remote.Host, remote.Port, "/remote/mcp").Uri,
            Name = hostId, TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds),
            AdditionalHeaders = remote.CredentialRef == null ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + config.GetCredential(remote.CredentialRef) }
        }, http, ownsHttpClient: false);
        McpClient? created = null;
        try
        {
            var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds), ClientInfo = new Implementation { Name = "YuzeEvalTool.Workspace", Version = BrokerConstants.PackageVersion } }, cancellationToken: token);
            created = client;
            var peer = new WorkspacePeer(hostId, http, client);
            var payload = Payload(await InvokeAsync(session, peer, "workspace_status", new JsonObject(), token));
            ValidatePeer(peer, payload);
            session.Peers[hostId] = peer;
            return peer;
        }
        catch (Exception ex) when (RemoteFailure(ex))
        {
            if (created != null) await created.DisposeAsync();
            http.Dispose();
            var code = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? "RemoteAuthenticationFailed" : ex is BrokerOperationException operation ? operation.Code : "RemoteUnavailable";
            throw new BrokerOperationException(code, "The remote workspace could not initialize. No workspace operation was sent.");
        }
    }

    private void ValidatePeer(WorkspacePeer peer, JsonObject payload)
    {
        var broker = Required(payload, "brokerId"); var instance = Required(payload, "brokerInstanceId");
        if (Text(payload, "view") != "local" || broker == config.BrokerId || !Guid.TryParseExact(broker, "N", out _) || !Guid.TryParseExact(instance, "N", out _))
            throw WorkspaceFiles.Error("RemoteIdentityConflict", "Peer must export its own local workspaces from a different Broker.");
        if (peer.BrokerId != null && peer.BrokerId != broker) throw WorkspaceFiles.Error("RemoteIdentityConflict", "The peer's persistent identity changed.");
        peer.BrokerId = broker; peer.InstanceId = instance;
        if (payload["desktopToolsAvailable"] is { } desktopAvailable) peer.DesktopToolsAvailable = desktopAvailable.GetValue<bool>();
    }

    private async Task<JsonObject> InvokeAsync(WorkspaceSession session, WorkspacePeer peer, string name, JsonObject args, CancellationToken token, bool heartbeat = false)
    {
        var parameters = args.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value, BrokerJsonContext.Default.JsonNode));
        try
        {
            var result = await peer.Client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = parameters }, token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(heartbeat ? config.ConnectionTimeoutSeconds + config.TransportGraceSeconds : config.Workspace.RequestTimeoutSeconds), token);
            var json = JsonSerializer.SerializeToNode(result, BrokerJsonContext.Default.CallToolResult)!.AsObject();
            if (json["isError"]?.GetValue<bool>() != true && name is "workspace_status" or "workspace_list" or "workspace_connect")
                ValidatePeer(peer, Payload(json));
            if (!heartbeat) MarkOnline(session, peer);
            return json;
        }
        catch (Exception ex) when (RemoteFailure(ex))
        {
            peer.Failures = Math.Min(peer.Failures + 1, 16);
            var state = heartbeat && peer.Failures < config.Workspace.HeartbeatFailureThreshold ? "reconnecting" : "offline";
            if (peer.State != state) session.Publish(state == "offline" ? "remote.disconnected" : "remote.reconnecting", new JsonObject
            { ["hostId"] = peer.Id, ["reason"] = ex is McpProtocolException ? "Peer rejected the workspace protocol or session." : "Workspace transport did not return a response.",
                ["affectedWorkspaces"] = new JsonArray(session.Contexts.Values.Where(context => context.Peer == peer).Select(context => (JsonNode?)JsonValue.Create(context.WorkspaceId)).DistinctBy(node => node!.ToJsonString()).ToArray()),
                ["recovery"] = "Read status/events; reconnect after a Broker restart. Never replay unknown writes." });
            var refused = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound };
            peer.State = state;
            peer.Error = ex is BrokerOperationException operation ? operation.Code : ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? "RemoteAuthenticationFailed" : ex is McpProtocolException or HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } ? "ContextHandleInvalid" : "RemoteUnavailable";
            peer.NextProbeAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(config.ReconnectMaxDelaySeconds,
                config.DiscoveryIntervalSeconds * Math.Pow(2, peer.Failures - 1)));
            var unknown = WorkspaceTools.IsMutation(name) && !refused;
            throw new BrokerOperationException(unknown ? "ExecutionOutcomeUnknown" : peer.Error,
                unknown ? "The request was dispatched but no result arrived. Verify actual state before any retry." : "Remote workspace is unavailable; cached context data is stale.", unknown);
        }
    }

    private void MarkOnline(WorkspaceSession session, WorkspacePeer peer)
    {
        if (peer.State != "online") session.Publish(peer.State == "connecting" ? "remote.connected" : "remote.recovered", new JsonObject
        { ["hostId"] = peer.Id, ["reason"] = "Authenticated workspace response received; validate context handles before continuing." });
        peer.State = "online"; peer.Error = null; peer.LastSeenAtUtc = DateTimeOffset.UtcNow;
        peer.Failures = 0;
        peer.NextProbeAtUtc = DateTimeOffset.UtcNow.AddSeconds(config.DiscoveryIntervalSeconds);
    }

    public async Task MaintainAsync(WorkspaceSession session, CancellationToken token)
    {
        if (session.Closed) return;
        foreach (var context in session.Contexts.Values)
            foreach (var task in context.Tasks.Values)
                if (!task.Finished && DateTimeOffset.UtcNow >= task.Deadline) task.Cancel("Configured task deadline elapsed.");
        if (!await session.Gate.WaitAsync(0, token)) return;
        try
        {
            foreach (var peer in session.Peers.Values)
            {
                if (DateTimeOffset.UtcNow < peer.NextProbeAtUtc) continue;
                try
                {
                    var previous = peer.InstanceId;
                    var status = Payload(await InvokeAsync(session, peer, "workspace_status", new JsonObject(), token, heartbeat: true));
                    ValidatePeer(peer, status);
                    if (previous != peer.InstanceId) session.Publish("remote.restarted", new JsonObject { ["hostId"] = peer.Id, ["reason"] = "Broker process changed; reconnect all contexts." });
                    var events = Payload(await InvokeAsync(session, peer, "workspace_events", new JsonObject { ["after"] = peer.EventCursor }, token, heartbeat: true));
                    if (events["gap"]?.GetValue<bool>() == true)
                        session.Publish("remote.events.gap", new JsonObject { ["hostId"] = peer.Id, ["reason"] = "Remote event retention expired; read fresh workspace/task status." });
                    var exportedEvents = events["events"] as JsonArray ?? throw WorkspaceFiles.Error("RemoteIncompatible", "The peer returned no events array.");
                    foreach (var entry in exportedEvents)
                    {
                        var item = entry as JsonObject ?? throw WorkspaceFiles.Error("RemoteIncompatible", "The peer returned an invalid event.");
                        var forwarded = (JsonObject)item.DeepClone();
                        forwarded["hostId"] = peer.Id; forwarded["remoteEventId"] = item["eventId"]?.GetValue<long>() ?? throw WorkspaceFiles.Error("RemoteIncompatible", "The peer returned an event without a cursor.");
                        var type = Required(item, "type");
                        session.Publish(type == "tools.changed" ? "remote.tools.changed" : type, forwarded);
                    }
                    peer.EventCursor = events["nextCursor"]?.GetValue<long>() ?? throw WorkspaceFiles.Error("RemoteIncompatible", "The peer returned no next event cursor.");
                    MarkOnline(session, peer);
                }
                catch (Exception ex) when (RemoteFailure(ex))
                {
                    if (peer.State == "online")
                    {
                        peer.State = "offline"; peer.Error = ex is BrokerOperationException operation ? operation.Code : "RemoteIncompatible";
                        session.Publish("remote.invalidated", new JsonObject { ["hostId"] = peer.Id, ["reason"] = peer.Error, ["recovery"] = "Validate the peer and reconnect the workspace." });
                    }
                }
            }
        }
        finally { session.UpdateDesktopTools(DesktopAvailable(session)); session.Gate.Release(); }
    }

    private static WorkspaceTerminal RequireTask(WorkspaceContext context, JsonObject args) => context.Tasks.TryGetValue(Required(args, "taskId"), out var task)
        ? task : throw WorkspaceFiles.Error("TaskNotFound", "No task with this id belongs to this context; its outcome is not inferred.");
    private static JsonObject Payload(JsonObject result)
    {
        if (result["isError"]?.GetValue<bool>() == true) throw WorkspaceFiles.Error("RemoteRequestRejected", result["content"]?.ToJsonString() ?? "Peer rejected request.");
        return result["structuredContent"]?["result"]?.DeepClone().AsObject() ?? throw WorkspaceFiles.Error("RemoteIncompatible", "Remote workspace returned no structured result.");
    }
    internal static string Text(JsonObject args, string name, string fallback = "") => args[name]?.GetValue<string>() ?? fallback;
    internal static string Required(JsonObject args, string name) => args[name]?.GetValue<string>() is { Length: > 0 } value ? value : throw WorkspaceFiles.Error("InvalidRequest", name + " is required.");
    internal static int Number(JsonObject args, string name, int fallback = 0) => args[name]?.GetValue<int>() ?? fallback;
    private static bool RemoteFailure(Exception ex) => ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or McpException or ClientTransportClosedException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException;
}
