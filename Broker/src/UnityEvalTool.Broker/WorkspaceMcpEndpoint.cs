#nullable enable
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

// This separate, session-affine 2025-11-25 endpoint deliberately leaves /mcp's
// stateless Unity catalog intact. Both endpoints share the host's connection authorization.
internal sealed class WorkspaceMcpEndpoint(WorkspaceService service)
{
    private readonly ConcurrentDictionary<string, WorkspaceSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _streams = new(StringComparer.Ordinal);
    private readonly object _creationLock = new();
    private WorkspaceOptions Options => service.Configuration.Workspace;

    public async Task HandleAsync(HttpContext http)
    {
        var networkExport = http.Items.ContainsKey(BrokerHost.NetworkExportRequest);
        var authorization = http.Request.Headers.Authorization.ToString();
        var authHash = WorkspaceFiles.Version(Encoding.UTF8.GetBytes(authorization));
        var sessionId = http.Request.Headers["Mcp-Session-Id"].ToString();
        WorkspaceSession? session = null;
        if (sessionId.Length != 0 && (!_sessions.TryGetValue(sessionId, out session) || session.Closed || session.NetworkExport != networkExport || session.AuthorizationHash != authHash ||
            Volatile.Read(ref session.ActiveRequests) == 0 && session.ExpiresAtUtc <= DateTimeOffset.UtcNow))
        { http.Response.StatusCode = 404; return; }
        if (session != null) session.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Options.LeaseSeconds);
        if (http.Request.Method == "DELETE")
        {
            if (session == null) { http.Response.StatusCode = 400; return; }
            if (_sessions.TryRemove(sessionId, out var removed)) await removed.DisposeAsync();
            http.Response.StatusCode = 204; return;
        }
        if (http.Request.Method == "GET")
        {
            if (session == null) { http.Response.StatusCode = 400; return; }
            await StreamAsync(http, session); return;
        }
        if (http.Request.ContentLength > Options.MaxFileBytes) { http.Response.StatusCode = 413; return; }
        JsonObject request;
        try
        {
            using var buffer = new MemoryStream();
            var block = new byte[16384];
            int count;
            while ((count = await http.Request.Body.ReadAsync(block, http.RequestAborted)) != 0)
            {
                if (buffer.Length + count > Options.MaxFileBytes) { http.Response.StatusCode = 413; return; }
                buffer.Write(block, 0, count);
            }
            request = JsonNode.Parse(buffer.ToArray())?.AsObject() ?? throw new JsonException("Expected JSON-RPC object.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { await ReplyAsync(http, RpcError(null, -32700, "Invalid JSON-RPC request.")); return; }
        var id = request["id"]?.DeepClone();
        var method = request["method"] is JsonValue methodValue && methodValue.TryGetValue<string>(out var methodText) ? methodText : null;
        if (request["jsonrpc"] is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var version) || version != "2.0" ||
            string.IsNullOrEmpty(method) || request["params"] is not (null or JsonObject))
        { await ReplyAsync(http, RpcError(id, -32600, "Expected a JSON-RPC 2.0 method.")); return; }
        if (method == "server/discover" || http.Request.Headers["MCP-Protocol-Version"] == "2026-07-28")
        { await ReplyAsync(http, RpcError(id, -32022, "This session-affine endpoint supports initialize with 2025-11-25.")); return; }
        if (method == "initialize")
        {
            if (session != null) { await ReplyAsync(http, RpcError(id, -32600, "Already initialized.")); return; }
            lock (_creationLock)
            {
                if (_sessions.Count < Options.MaxSessions)
                {
                    session = new WorkspaceSession(networkExport, authHash, Options);
                    _sessions[session.Id] = session;
                }
            }
            if (session == null) { http.Response.StatusCode = 429; return; }
            http.Response.Headers["Mcp-Session-Id"] = session.Id;
            await ReplyAsync(http, Success(id, new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject(), ["logging"] = new JsonObject(), ["resources"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "YuzeEvalTool.RemoteWorkspace", ["version"] = BrokerConstants.PackageVersion },
                ["instructions"] = "Files, terminal, project AGENTS.md/Skills and optional desktop on remote hosts only; Unity eval stays on the Unity MCP. Flow: workspace_list, workspace_connect, read the returned guidance and relevant Skills, then work through contextHandle with remote paths. Never auto-retry a write that returned ExecutionOutcomeUnknown; check actual state first. Desktop tools work only on hosts with desktopToolsAvailable; foreground input needs the user's explicit permission."
            })); return;
        }
        if (session == null) { http.Response.StatusCode = 400; return; }
        if (id == null) { http.Response.StatusCode = 202; return; }
        try
        {
            var parameters = request["params"] as JsonObject ?? new JsonObject();
            JsonObject result;
            switch (method)
            {
                case "ping": case "logging/setLevel": result = new JsonObject(); break;
                case "tools/list": result = new JsonObject { ["tools"] = await service.ListToolsAsync(session, http.RequestAborted) }; break;
                case "resources/list": result = new JsonObject { ["resources"] = new JsonArray((JsonNode)new JsonObject { ["uri"] = "workspace://events", ["name"] = "Remote workspace session events", ["mimeType"] = "application/json" }) }; break;
                case "resources/templates/list": result = new JsonObject { ["resourceTemplates"] = new JsonArray() }; break;
                case "resources/read":
                    if (WorkspaceService.Required(parameters, "uri") != "workspace://events") throw WorkspaceFiles.Error("ResourceNotFound", "Unknown workspace resource.");
                    result = new JsonObject { ["contents"] = new JsonArray((JsonNode)new JsonObject { ["uri"] = "workspace://events", ["mimeType"] = "application/json", ["text"] = session.Events(0).ToJsonString() }) }; break;
                case "tools/call":
                    var tool = WorkspaceService.Required(parameters, "name");
                    var args = parameters["arguments"] as JsonObject ?? new JsonObject();
                    Interlocked.Increment(ref session.ActiveRequests);
                    try { result = await service.CallAsync(session, tool, args, http.RequestAborted); }
                    finally
                    {
                        session.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Options.LeaseSeconds);
                        Interlocked.Decrement(ref session.ActiveRequests);
                    }
                    break;
                default: await ReplyAsync(http, RpcError(id, -32601, "Method not found.")); return;
            }
            await ReplyAsync(http, Success(id, result));
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
        catch (BrokerOperationException ex) { await ReplyAsync(http, Success(id, WorkspaceTools.Failure(ex.Code, ex.Detail, ex.MayHaveExecuted))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or FormatException or OverflowException)
        { await ReplyAsync(http, Success(id, WorkspaceTools.Failure("WorkspaceRequestFailed", ex.Message))); }
    }

    private async Task StreamAsync(HttpContext http, WorkspaceSession session)
    {
        if (!_streams.TryAdd(session.Id, 0)) { http.Response.StatusCode = 409; return; }
        try
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var cursor = 0L;
            var supplied = http.Request.Headers["Last-Event-ID"].ToString();
            if (supplied.Length != 0 && !long.TryParse(supplied, out cursor)) { http.Response.StatusCode = 400; return; }
            try { session.Events(cursor); }
            catch (BrokerOperationException) { http.Response.StatusCode = 400; return; }
            while (!http.RequestAborted.IsCancellationRequested && _sessions.ContainsKey(session.Id))
            {
                var events = session.Events(cursor);
                if (events["gap"]!.GetValue<bool>())
                    await SendEventAsync(http, null, new JsonObject { ["type"] = "events.gap", ["recovery"] = "Read workspace_status; event history was evicted." });
                foreach (var item in events["events"]!.AsArray().OfType<JsonObject>())
                    await SendEventAsync(http, item["eventId"]!.GetValue<long>(), item);
                cursor = events["nextCursor"]!.GetValue<long>();
                await http.Response.WriteAsync(": heartbeat\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
                session.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Options.LeaseSeconds);
                await Task.Delay(1000, http.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
        finally { _streams.TryRemove(session.Id, out _); }
    }

    private static async Task SendEventAsync(HttpContext http, long? id, JsonObject item)
    {
        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/message", ["params"] = new JsonObject { ["level"] = "warning", ["logger"] = "YuzeEvalTool.workspace", ["data"] = item.DeepClone() } };
        if (id.HasValue) await http.Response.WriteAsync("id: " + id + "\n", http.RequestAborted);
        await http.Response.WriteAsync("event: message\ndata: " + notification.ToJsonString() + "\n\n", http.RequestAborted);
    }

    public async Task MaintainAsync(CancellationToken token)
    {
        await Task.WhenAll(_sessions.ToArray().Select(async pair =>
        {
            if (Volatile.Read(ref pair.Value.ActiveRequests) == 0 && pair.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow && _sessions.TryRemove(pair.Key, out var expired))
            { await expired.DisposeAsync(); return; }
            await service.MaintainAsync(pair.Value, token);
        }));
    }

    public async Task StopAsync()
    {
        foreach (var pair in _sessions)
            if (_sessions.TryRemove(pair.Key, out var session)) await session.DisposeAsync();
    }
    private static JsonObject Success(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    private static JsonObject RpcError(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    private static Task ReplyAsync(HttpContext context, JsonObject response)
    { context.Response.ContentType = "application/json"; return context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted); }
}

internal sealed class WorkspaceMaintenanceService(WorkspaceMcpEndpoint endpoint) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await endpoint.MaintainAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await endpoint.StopAsync(); }
    }
}
