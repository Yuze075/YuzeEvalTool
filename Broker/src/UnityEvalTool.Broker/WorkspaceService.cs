#nullable enable
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace YuzeToolkit.Eval.Broker;

internal sealed class WorkspaceService(BrokerConfiguration config, BrokerRegistry registry)
{
    private const string Incompatible = BrokerErrorCodes.RemoteIncompatible;
    public BrokerConfiguration Configuration => config;
    private readonly WorkspaceDesktop _desktop = new(config.Workspace, registry);
    private bool DesktopInstalled => config.Workspace.DesktopDriverPath is { } path && File.Exists(path);

    // The gateway always lists desktop tools and lets each peer reject them; an export lists them only with an installed driver.
    public Task<JsonArray> ListToolsAsync(WorkspaceSession session, CancellationToken token) =>
        Task.FromResult(WorkspaceTools.List(!session.NetworkExport || DesktopInstalled));

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
                if (args.ContainsKey("hostId") && (session.NetworkExport || args.ContainsKey("contextHandle")))
                    throw WorkspaceFiles.Error("InvalidDesktopTarget", "Use either a remote hostId or a workspace context. Network exports cannot forward a hostId.");
                if (session.NetworkExport && !DesktopInstalled)
                    throw WorkspaceFiles.Error("ToolUnavailable", "This host has no installed, configured Cua Driver.");
                if (!args.ContainsKey("contextHandle"))
                {
                    if (session.NetworkExport)
                        return name == "desktop_observe"
                            ? await _desktop.ObserveAsync(session.Id, null, args, cancellationToken)
                            : await _desktop.ActAsync(session.Id, null, args, cancellationToken);
                    var peer = await PeerAsync(session, Required(args, "hostId"), cancellationToken);
                    if (peer.State != "online") throw WorkspaceFiles.Error("RemoteUnavailable", "The remote desktop host is offline. No request was sent.");
                    var forwarded = (JsonObject)args.DeepClone(); forwarded.Remove("hostId");
                    return await InvokeAsync(session, peer, name, forwarded, cancellationToken);
                }
            }
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
                if (name == "workspace_status" && !IsError(result))
                {
                    var snapshot = Payload(result); snapshot["contextHandle"] = context.Handle;
                    DescribeRemoteProject(snapshot, context.Peer.Id);
                    context.Snapshot = (JsonObject)snapshot.DeepClone();
                    result = WorkspaceTools.Result(snapshot);
                }
                if (name == "workspace_disconnect" && !IsError(result))
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
        finally { session.Gate.Release(); }
    }

    private JsonObject HostSnapshot(WorkspaceSession session)
    {
        var snapshot = new JsonObject
        {
            ["brokerId"] = config.BrokerId, ["brokerInstanceId"] = BrokerHost.InstanceId,
            ["view"] = session.NetworkExport ? "local" : "remote", ["expiresAtUtc"] = session.ExpiresAtUtc.ToString("O"),
            ["hosts"] = new JsonArray(session.Peers.Values.Select(p => (JsonNode)new JsonObject
            {
                ["hostId"] = p.Id, ["state"] = p.State, ["error"] = p.Error, ["lastSeenAtUtc"] = p.LastSeenAtUtc?.ToString("O"),
                ["desktopToolsAvailable"] = p.DesktopToolsAvailable
            }).ToArray())
        };
        if (session.NetworkExport) snapshot["desktopToolsAvailable"] = DesktopInstalled;
        return snapshot;
    }

    private async Task<JsonObject> ListAsync(WorkspaceSession session, CancellationToken token)
    {
        var projects = new JsonArray(); var errors = new JsonArray();
        if (session.NetworkExport)
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
                    var exported = payload["projects"] as JsonArray ?? throw WorkspaceFiles.Error(Incompatible, "The peer returned no workspace array.");
                    foreach (var entry in exported)
                    {
                        if (entry is not JsonObject project || Text(project, "source", "", Incompatible) != "local")
                            throw WorkspaceFiles.Error("RecursiveRouteRejected", "The peer returned a workspace that is not its own local export.");
                        var item = (JsonObject)project.DeepClone(); DescribeRemoteProject(item, remote.Id); projects.Add((JsonNode)item);
                    }
                }
                catch (Exception ex) when (RemoteBrokerConnection.IsFailure(ex))
                {
                    errors.Add((JsonNode)new JsonObject
                    {
                        ["hostId"] = remote.Id, ["error"] = RemoteBrokerConnection.FailureCode(ex),
                        ["message"] = ex is BrokerOperationException operation ? operation.Detail : ex.Message
                    });
                }
            }
        }
        var result = HostSnapshot(session);
        result["projects"] = projects; result["errors"] = errors;
        return WorkspaceTools.Result(result);
    }

    private async Task<JsonObject> ConnectAsync(WorkspaceSession session, JsonObject args, CancellationToken token)
    {
        if (session.Contexts.Count >= config.Workspace.MaxContextsPerSession) throw WorkspaceFiles.Error("ContextLimitReached", "Close an existing context first.");
        var id = Required(args, "workspaceId");
        WorkspaceContext context;
        JsonObject snapshot;
        if (session.NetworkExport)
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
            if (Text(snapshot, "source", "", Incompatible) != "local") throw WorkspaceFiles.Error("RecursiveRouteRejected", "The peer did not connect its own local workspace.");
            context = new WorkspaceContext(id, Required(snapshot, "cwd", Incompatible))
            { Peer = peer, RemoteHandle = Required(snapshot, "contextHandle", Incompatible), RemoteInstanceId = peer.InstanceId };
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
        if (snapshot.ContainsKey("name")) snapshot["displayName"] = "[Remote:" + hostId + "] " + Required(snapshot, "name", Incompatible);
        if (snapshot["unityInstances"] is JsonArray instances)
            foreach (var entry in instances)
            {
                var unity = entry as JsonObject ?? throw WorkspaceFiles.Error(Incompatible, "The peer returned an invalid Unity instance.");
                var localId = Required(unity, "instanceId", Incompatible);
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
        RemoteBrokerConnection? connection = null;
        try
        {
            connection = await RemoteBrokerConnection.ConnectAsync(config, remote, "/remote/mcp",
                TimeSpan.FromSeconds(config.Workspace.RequestTimeoutSeconds), token);
            var peer = new WorkspacePeer(hostId, connection);
            await InvokeAsync(session, peer, "workspace_status", new JsonObject(), token);
            session.Peers[hostId] = peer;
            return peer;
        }
        catch (Exception ex) when (RemoteBrokerConnection.IsFailure(ex))
        {
            if (connection != null) await connection.DisposeAsync();
            throw new BrokerOperationException(RemoteBrokerConnection.FailureCode(ex), "The remote workspace could not initialize; no workspace operation was sent. " +
                (ex is BrokerOperationException operation ? operation.Detail : ex.Message));
        }
    }

    private void ValidatePeer(WorkspacePeer peer, JsonObject payload)
    {
        var broker = Required(payload, "brokerId", Incompatible);
        var instance = Required(payload, "brokerInstanceId", Incompatible);
        RemoteBrokerConnection.RequireLocalExport(Text(payload, "view", "", Incompatible), broker, instance);
        registry.ValidateRemoteIdentity(peer.Id, broker);
        peer.InstanceId = instance;
        if (payload.ContainsKey("desktopToolsAvailable")) peer.DesktopToolsAvailable = RemoteFlag(payload, "desktopToolsAvailable");
    }

    private async Task<JsonObject> InvokeAsync(WorkspaceSession session, WorkspacePeer peer, string name, JsonObject args, CancellationToken token, bool heartbeat = false)
    {
        CallToolResult result;
        try
        {
            result = await peer.Connection.CallAsync(name, args, TimeSpan.FromSeconds(heartbeat
                ? config.ConnectionTimeoutSeconds + config.TransportGraceSeconds : config.Workspace.RequestTimeoutSeconds), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (WorkspaceTools.IsMutation(name))
                throw new BrokerOperationException(BrokerErrorCodes.ExecutionOutcomeUnknown, "The request was interrupted after dispatch. Verify actual state before any retry.", true);
            throw;
        }
        // The peer replied with a JSON-RPC error: it is online and did not execute the call.
        catch (McpProtocolException ex)
        { throw new BrokerOperationException(Incompatible, "The peer rejected the workspace request: " + ex.Message); }
        catch (Exception ex) when (RemoteBrokerConnection.IsFailure(ex))
        {
            peer.Failures = Math.Min(peer.Failures + 1, 16);
            var state = heartbeat && peer.Failures < config.Workspace.HeartbeatFailureThreshold ? "reconnecting" : "offline";
            if (peer.State != state) session.Publish(state == "offline" ? "remote.disconnected" : "remote.reconnecting", new JsonObject
            {
                ["hostId"] = peer.Id, ["reason"] = "Workspace transport did not return a response.",
                ["affectedWorkspaces"] = new JsonArray(session.Contexts.Values.Where(context => context.Peer == peer).Select(context => context.WorkspaceId).Distinct().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["recovery"] = "Read status/events; reconnect after a Broker restart. Never replay unknown writes."
            });
            peer.State = state;
            peer.Error = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound } ? "ContextHandleInvalid" : RemoteBrokerConnection.FailureCode(ex);
            peer.NextProbeAtUtc = DateTimeOffset.UtcNow + RemoteBrokerConnection.RetryDelay(config, peer.Failures);
            var unknown = WorkspaceTools.IsMutation(name) && !RemoteBrokerConnection.ProvesNotExecuted(ex);
            throw new BrokerOperationException(unknown ? BrokerErrorCodes.ExecutionOutcomeUnknown : peer.Error,
                unknown ? "The request was dispatched but no result arrived. Verify actual state before any retry." : "Remote workspace is unavailable; cached context data is stale.", unknown);
        }
        var json = JsonSerializer.SerializeToNode(result, BrokerJsonContext.Default.CallToolResult)!.AsObject();
        if (name is "workspace_status" or "workspace_list" or "workspace_connect" && !IsError(json))
            ValidatePeer(peer, Payload(json));
        if (!heartbeat) MarkOnline(session, peer);
        return json;
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
                    await InvokeAsync(session, peer, "workspace_status", new JsonObject(), token, heartbeat: true);
                    if (previous != peer.InstanceId) session.Publish("remote.restarted", new JsonObject { ["hostId"] = peer.Id, ["reason"] = "Broker process changed; reconnect all contexts." });
                    var events = Payload(await InvokeAsync(session, peer, "workspace_events", new JsonObject { ["after"] = peer.EventCursor }, token, heartbeat: true));
                    if (RemoteFlag(events, "gap"))
                        session.Publish("remote.events.gap", new JsonObject { ["hostId"] = peer.Id, ["reason"] = "Remote event retention expired; read fresh workspace/task status." });
                    var exportedEvents = events["events"] as JsonArray ?? throw WorkspaceFiles.Error(Incompatible, "The peer returned no events array.");
                    foreach (var entry in exportedEvents)
                    {
                        var item = entry as JsonObject ?? throw WorkspaceFiles.Error(Incompatible, "The peer returned an invalid event.");
                        var forwarded = (JsonObject)item.DeepClone();
                        forwarded["hostId"] = peer.Id; forwarded["remoteEventId"] = RemoteCursor(item, "eventId");
                        session.Publish(Required(item, "type", Incompatible), forwarded);
                    }
                    peer.EventCursor = RemoteCursor(events, "nextCursor");
                    MarkOnline(session, peer);
                }
                catch (Exception ex) when (RemoteBrokerConnection.IsFailure(ex))
                {
                    if (peer.State == "online")
                    {
                        peer.State = "offline"; peer.Error = RemoteBrokerConnection.FailureCode(ex);
                        session.Publish("remote.invalidated", new JsonObject { ["hostId"] = peer.Id, ["reason"] = peer.Error, ["recovery"] = "Validate the peer and reconnect the workspace." });
                    }
                }
            }
        }
        finally { session.Gate.Release(); }
    }

    private static WorkspaceTerminal RequireTask(WorkspaceContext context, JsonObject args) => context.Tasks.TryGetValue(Required(args, "taskId"), out var task)
        ? task : throw WorkspaceFiles.Error("TaskNotFound", "No task with this id belongs to this context; its outcome is not inferred.");

    private static bool IsError(JsonObject result) => result["isError"]?.GetValue<bool>() == true;

    // Peer failures keep the peer's own error code and message.
    private static JsonObject Payload(JsonObject result)
    {
        if (IsError(result))
        {
            var error = result["structuredContent"]?["error"] as JsonObject
                ?? throw WorkspaceFiles.Error(Incompatible, "The peer returned an unstructured error.");
            throw WorkspaceFiles.Error(Required(error, "code", Incompatible), Text(error, "message", "", Incompatible));
        }
        return (result["structuredContent"]?["result"] as JsonObject)?.DeepClone().AsObject()
            ?? throw WorkspaceFiles.Error(Incompatible, "Remote workspace returned no structured result.");
    }

    internal static string Text(JsonObject args, string name, string fallback = "", string code = BrokerErrorCodes.InvalidRequest) => args[name] switch
    {
        null => fallback,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw WorkspaceFiles.Error(code, name + " must be a string.")
    };
    internal static string Required(JsonObject args, string name, string code = BrokerErrorCodes.InvalidRequest) =>
        Text(args, name, "", code) is { Length: > 0 } value ? value : throw WorkspaceFiles.Error(code, name + " is required.");
    internal static int Number(JsonObject args, string name, int fallback = 0) => args[name]?.GetValue<int>() ?? fallback;
    private static long RemoteCursor(JsonObject payload, string name) => payload[name] is JsonValue value && value.TryGetValue<long>(out var cursor)
        ? cursor : throw WorkspaceFiles.Error(Incompatible, "The peer returned no integer " + name + ".");
    private static bool RemoteFlag(JsonObject payload, string name) => payload[name] is JsonValue value && value.TryGetValue<bool>(out var flag)
        ? flag : throw WorkspaceFiles.Error(Incompatible, "The peer returned no boolean " + name + ".");
}
