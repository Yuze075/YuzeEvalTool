#nullable enable
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

// The local listener's workspace tools reach configured remote hosts only; a network export serves
// only this host's registered folders and never forwards. Contexts are leases, not MCP sessions.
internal sealed class WorkspaceService(BrokerConfiguration config, BrokerRegistry registry)
{
    private const string Incompatible = BrokerErrorCodes.RemoteIncompatible;
    private readonly ConcurrentDictionary<string, WorkspaceContext> _contexts = new(StringComparer.Ordinal);
    private readonly WorkspaceDesktop _desktop = new(config.Workspace, registry);
    private WorkspaceOptions Options => config.Workspace;
    private bool DesktopInstalled => Options.DesktopDriverPath is { } path && File.Exists(path);

    public async Task<JsonObject> CallAsync(bool networkExport, string name, JsonObject args, CancellationToken token)
    {
        if (name == "workspace_list") return WorkspaceTools.Result(networkExport ? ListLocal() : await ListRemoteAsync(token));
        if (name == "workspace_connect") return WorkspaceTools.Result(networkExport ? ConnectLocal(args) : await ConnectRemoteAsync(args, token));
        if (name is "desktop_observe" or "desktop_act")
        {
            if (args.ContainsKey("hostId") && (networkExport || args.ContainsKey("contextHandle")))
                throw WorkspaceFiles.Error("InvalidDesktopTarget", "Use either a remote hostId or a workspace context. Network exports cannot forward a hostId.");
            if (networkExport && !DesktopInstalled) throw WorkspaceFiles.Error("ToolUnavailable", "This host has no installed, configured Cua Driver.");
            if (!args.ContainsKey("contextHandle"))
            {
                if (networkExport)
                    return name == "desktop_observe" ? await _desktop.ObserveAsync(null, args, token) : await _desktop.ActAsync(null, args, token);
                var forwarded = (JsonObject)args.DeepClone(); forwarded.Remove("hostId");
                return await CallPeerAsync(Required(args, "hostId"), name, forwarded, token);
            }
        }
        var context = _contexts.TryGetValue(Required(args, "contextHandle"), out var found) && found.NetworkExport == networkExport ? found
            : throw WorkspaceFiles.Error("ContextHandleInvalid", "The context does not exist or expired. Connect again.");
        Interlocked.Increment(ref context.ActiveRequests);
        try { return context.HostId != null ? await ForwardAsync(context, name, args, token) : await LocalAsync(context, name, args, token); }
        finally { context.Touch(); Interlocked.Decrement(ref context.ActiveRequests); }
    }

    private async Task<JsonObject> LocalAsync(WorkspaceContext context, string name, JsonObject args, CancellationToken token)
    {
        var files = context.Files!;
        JsonNode data;
        switch (name)
        {
            case "workspace_status": data = Describe(context); break;
            case "workspace_disconnect": Close(context); data = new JsonObject { ["closed"] = true }; break;
            case "workspace_get_guidance": data = new JsonObject { ["documents"] = files.Guidance(Text(args, "path", context.Cwd)) }; break;
            case "file_read": data = ReadFiles(files, args); break;
            case "file_glob": data = files.Glob(Text(args, "path", context.Cwd), Required(args, "pattern"), Number(args, "offset"), Number(args, "limit", 200)); break;
            case "file_grep":
                data = files.Grep(Text(args, "path", context.Cwd), Required(args, "pattern"), Text(args, "glob"), args["ignoreCase"]?.GetValue<bool>() == true,
                    Text(args, "outputMode", "files"), Number(args, "contextLines"), Number(args, "offset"), Number(args, "limit", 200)); break;
            case "file_write":
                data = files.Write(Required(args, "path"), args.ContainsKey("content") ? Text(args, "content") : throw WorkspaceFiles.Error("InvalidRequest", "content is required."),
                    Required(args, "expectedVersion")); break;
            case "file_edit":
                data = files.Edit(Required(args, "path"), args.ContainsKey("expectedVersion") ? Required(args, "expectedVersion") : null,
                    args["edits"] as JsonArray ?? throw WorkspaceFiles.Error("InvalidRequest", "edits is required.")); break;
            case "file_apply_diff": data = files.ApplyDiff(Required(args, "diff")); break;
            case "file_view_image":
                var image = Required(args, "path");
                return WorkspaceTools.Result(new JsonObject { ["path"] = image, ["workspaceId"] = context.WorkspaceId }, new JsonArray((JsonNode)files.Image(image)));
            case "terminal_exec":
                if (context.Tasks.Count >= Options.MaxTasksPerContext) throw WorkspaceFiles.Error("TaskLimitReached", "This context reached maxTasksPerContext; disconnect it after collecting output.");
                var task = new WorkspaceTerminal(files, Text(args, "cwd", context.Cwd), args, Options);
                context.Tasks[task.Id] = task;
                data = await task.WaitAsync(Number(args, "waitSeconds", 30), 0, Options.MaxTextCharacters, true, token); break;
            case "terminal_poll":
                data = await RequireTask(context, args).WaitAsync(Number(args, "waitSeconds"), Number(args, "offset"),
                    Number(args, "maxCharacters", Options.MaxTextCharacters), false, token); break;
            case "terminal_input":
                var input = RequireTask(context, args);
                await input.WriteAsync(Text(args, "text"), args["close"]?.GetValue<bool>() == true, token);
                data = input.Snapshot(0, 0); break;
            case "terminal_cancel": data = await RequireTask(context, args).CancelAsync(); break;
            case "desktop_observe": return await _desktop.ObserveAsync(context, args, token);
            case "desktop_act": return await _desktop.ActAsync(context, args, token);
            default: throw WorkspaceFiles.Error("UnknownTool", "The requested workspace tool does not exist.");
        }
        return WorkspaceTools.Result(data);
    }

    private JsonObject ReadFiles(WorkspaceFiles files, JsonObject args)
    {
        var requested = args["files"] as JsonArray ?? throw WorkspaceFiles.Error("InvalidRequest", "files is required.");
        if (requested.Count is < 1 or > 20) throw WorkspaceFiles.Error("InvalidRequest", "files must contain 1–20 entries.");
        var budget = Options.MaxTextCharacters;
        var results = new JsonArray();
        foreach (var node in requested)
        {
            var entry = node as JsonObject ?? throw WorkspaceFiles.Error("InvalidRequest", "Each files entry must be an object.");
            var path = Required(entry, "path");
            try { results.Add((JsonNode)files.ReadLines(path, Number(entry, "startLine", 1), Number(entry, "lineCount"), ref budget)); }
            catch (Exception ex) when (ex is BrokerOperationException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                results.Add((JsonNode)new JsonObject { ["path"] = path, ["error"] = ex switch
                {
                    BrokerOperationException operation => operation.Code + ": " + operation.Detail,
                    FileNotFoundException or DirectoryNotFoundException => "FileNotFound",
                    _ => ex.Message
                } });
            }
        }
        return new JsonObject { ["files"] = results };
    }

    private JsonObject ListLocal() => new()
    {
        ["brokerId"] = config.BrokerId, ["brokerInstanceId"] = BrokerHost.InstanceId, ["view"] = "local",
        ["desktopToolsAvailable"] = DesktopInstalled,
        ["projects"] = new JsonArray(config.Projects.Select(project => (JsonNode)DescribeProject(project)).ToArray())
    };

    private async Task<JsonObject> ListRemoteAsync(CancellationToken token)
    {
        var hosts = new JsonArray(); var projects = new JsonArray(); var errors = new JsonArray();
        foreach (var host in registry.GetProjectSnapshot().RemoteBrokers.Where(host => host.State != "disabled"))
        {
            var entry = new JsonObject { ["hostId"] = host.Id, ["state"] = host.State, ["error"] = host.ErrorCode, ["lastSeenAtUtc"] = host.LastSeenAtUtc?.ToString("O") };
            hosts.Add((JsonNode)entry);
            if (host.State != "online") continue;
            try
            {
                var payload = Payload(await CallPeerAsync(host.Id, "workspace_list", new JsonObject(), token));
                entry["desktopToolsAvailable"] = RemoteFlag(payload, "desktopToolsAvailable");
                foreach (var node in payload["projects"] as JsonArray ?? throw WorkspaceFiles.Error(Incompatible, "The peer returned no workspace array."))
                {
                    if (node is not JsonObject project || Text(project, "source", "", Incompatible) != "local")
                        throw WorkspaceFiles.Error("RecursiveRouteRejected", "The peer returned a workspace that is not its own local export.");
                    var item = (JsonObject)project.DeepClone(); DescribeRemoteProject(item, host.Id); projects.Add((JsonNode)item);
                }
            }
            catch (Exception ex) when (RemoteBrokerConnection.IsFailure(ex))
            {
                errors.Add((JsonNode)new JsonObject
                {
                    ["hostId"] = host.Id, ["error"] = RemoteBrokerConnection.FailureCode(ex),
                    ["message"] = ex is BrokerOperationException operation ? operation.Detail : ex.Message
                });
            }
        }
        return new JsonObject { ["view"] = "remote", ["hosts"] = hosts, ["projects"] = projects, ["errors"] = errors };
    }

    private JsonObject ConnectLocal(JsonObject args)
    {
        if (args.ContainsKey("hostId")) throw WorkspaceFiles.Error("RecursiveRouteRejected", "Network workspaces export local projects only; hostId is not accepted.");
        RequireCapacity();
        var id = Required(args, "workspaceId");
        var project = config.Projects.SingleOrDefault(p => p.Id == id) ?? throw WorkspaceFiles.Error("WorkspaceNotFound", "No registered workspace has this id.");
        var files = new WorkspaceFiles(project, Options);
        var context = new WorkspaceContext(true, id, files.DirectoryPath(Text(args, "cwd", "."))) { Files = files, Definition = project };
        var snapshot = Describe(context);
        snapshot["guidance"] = files.Guidance(context.Cwd);
        snapshot["skills"] = files.Skills();
        _contexts[context.Handle] = context;
        return snapshot;
    }

    private async Task<JsonObject> ConnectRemoteAsync(JsonObject args, CancellationToken token)
    {
        var hostId = Required(args, "hostId");
        var peer = registry.GetPeer(hostId);
        if (peer.State != "online") throw WorkspaceFiles.Error("RemoteUnavailable", $"Remote '{hostId}' is {peer.State} ({peer.ErrorCode}). No request was sent.");
        RequireCapacity();
        var forwarded = (JsonObject)args.DeepClone(); forwarded.Remove("hostId");
        var snapshot = Payload(await CallPeerAsync(hostId, "workspace_connect", forwarded, token));
        if (Text(snapshot, "source", "", Incompatible) != "local") throw WorkspaceFiles.Error("RecursiveRouteRejected", "The peer did not connect its own local workspace.");
        if (Required(snapshot, "brokerInstanceId", Incompatible) != peer.BrokerInstanceId)
            throw WorkspaceFiles.Error("ContextHandleInvalid", "The remote Broker changed while connecting. Connect again.");
        var context = new WorkspaceContext(false, Required(args, "workspaceId"), Required(snapshot, "cwd", Incompatible))
        { HostId = hostId, RemoteHandle = Required(snapshot, "contextHandle", Incompatible), RemoteBrokerInstanceId = peer.BrokerInstanceId };
        snapshot["contextHandle"] = context.Handle; DescribeRemoteProject(snapshot, hostId);
        context.Snapshot = (JsonObject)snapshot.DeepClone();
        _contexts[context.Handle] = context;
        return snapshot;
    }

    private async Task<JsonObject> ForwardAsync(WorkspaceContext context, string name, JsonObject args, CancellationToken token)
    {
        var peer = registry.GetPeer(context.HostId!);
        if (peer.State == "online" && peer.BrokerInstanceId != context.RemoteBrokerInstanceId)
        {
            Close(context);
            throw WorkspaceFiles.Error("ContextHandleInvalid", "The remote Broker restarted, so this context is gone. Connect again.");
        }
        if (peer.State != "online")
        {
            if (name != "workspace_status") throw WorkspaceFiles.Error("RemoteUnavailable", "The remote is known offline. No request was sent; wait for recovery.");
            var stale = (JsonObject)context.Snapshot!.DeepClone();
            stale["isStale"] = true; stale["state"] = peer.State; stale["error"] = peer.ErrorCode;
            return WorkspaceTools.Result(stale);
        }
        var forwarded = (JsonObject)args.DeepClone(); forwarded["contextHandle"] = context.RemoteHandle;
        var result = await CallPeerAsync(context.HostId!, name, forwarded, token);
        if (IsError(result)) return result;
        if (name == "workspace_status")
        {
            var snapshot = Payload(result); snapshot["contextHandle"] = context.Handle;
            DescribeRemoteProject(snapshot, context.HostId!);
            context.Snapshot = (JsonObject)snapshot.DeepClone();
            return WorkspaceTools.Result(snapshot);
        }
        if (name == "workspace_disconnect") Close(context);
        return result;
    }

    private async Task<JsonObject> CallPeerAsync(string hostId, string name, JsonObject args, CancellationToken token)
    {
        var wait = name == "terminal_exec" ? Number(args, "waitSeconds", 30) : name == "terminal_poll" ? Number(args, "waitSeconds") : 0;
        var result = await registry.CallPeerToolAsync(hostId, name, args, TimeSpan.FromSeconds(Options.RequestTimeoutSeconds + Math.Clamp(wait, 0, 300)),
            WorkspaceTools.IsMutation(name), token);
        return JsonSerializer.SerializeToNode(result, BrokerJsonContext.Default.CallToolResult)!.AsObject();
    }

    private JsonObject Describe(WorkspaceContext context)
    {
        var snapshot = DescribeProject(context.Definition!);
        snapshot["contextHandle"] = context.Handle;
        snapshot["brokerId"] = config.BrokerId; snapshot["brokerInstanceId"] = BrokerHost.InstanceId;
        snapshot["cwd"] = context.Cwd;
        snapshot["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        snapshot["shells"] = new JsonArray(OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh");
        snapshot["isStale"] = false; snapshot["state"] = "online";
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
            ["unityInstances"] = new JsonArray(instances.Select(unity => (JsonNode)new JsonObject
            {
                ["instanceId"] = unity.InstanceId, ["processId"] = unity.ProcessId,
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
                unity["instanceId"] = "remote:" + hostId + ":" + localId;
            }
    }

    public void Maintain()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var context in _contexts.Values)
        {
            foreach (var task in context.Tasks.Values)
                if (!task.Finished && now >= task.Deadline) task.Cancel("Configured task deadline elapsed.");
            if (Volatile.Read(ref context.ActiveRequests) == 0 && now - context.LastUsedUtc > TimeSpan.FromSeconds(Options.LeaseSeconds)) Close(context);
        }
    }

    public void Stop() { foreach (var context in _contexts.Values) Close(context); }

    private void Close(WorkspaceContext context)
    {
        if (_contexts.TryRemove(context.Handle, out _)) context.Dispose();
    }

    private void RequireCapacity()
    {
        if (_contexts.Count >= Options.MaxContexts) throw WorkspaceFiles.Error("ContextLimitReached", "Too many open contexts; disconnect unused ones.");
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
    private static bool RemoteFlag(JsonObject payload, string name) => payload[name] is JsonValue value && value.TryGetValue<bool>(out var flag)
        ? flag : throw WorkspaceFiles.Error(Incompatible, "The peer returned no boolean " + name + ".");
}
