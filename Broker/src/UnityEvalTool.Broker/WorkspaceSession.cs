#nullable enable
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace YuzeToolkit.Eval.Broker;

internal sealed class WorkspaceSession(bool exporting, string authorizationHash, WorkspaceOptions options) : IAsyncDisposable
{
    private readonly object _eventsLock = new();
    private readonly Queue<JsonObject> _events = new();
    private long _eventId;
    private long _expiresAtTicks = DateTimeOffset.UtcNow.AddSeconds(options.LeaseSeconds).UtcTicks;
    private int _closed;
    private bool? _desktopToolsVisible;
    public string Id { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public bool Exporting { get; } = exporting;
    public string AuthorizationHash { get; } = authorizationHash;
    public bool ExportDesktopAuthorized { get; init; }
    public DateTimeOffset ExpiresAtUtc
    {
        get => new(Interlocked.Read(ref _expiresAtTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _expiresAtTicks, value.UtcTicks);
    }
    public bool Closed => Volatile.Read(ref _closed) != 0;
    public int ActiveRequests;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public ConcurrentDictionary<string, WorkspaceContext> Contexts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, WorkspacePeer> Peers { get; } = new(StringComparer.Ordinal);

    // Called under Gate; the event stream reads the resulting event through its own lock.
    public void UpdateDesktopTools(bool available)
    {
        if (_desktopToolsVisible.HasValue && _desktopToolsVisible != available)
            Publish("tools.changed", new JsonObject { ["desktopToolsAvailable"] = available });
        _desktopToolsVisible = available;
    }

    public void Publish(string type, JsonObject detail)
    {
        lock (_eventsLock)
        {
            var item = (JsonObject)detail.DeepClone();
            item["eventId"] = ++_eventId; item["atUtc"] = DateTimeOffset.UtcNow.ToString("O"); item["type"] = type;
            _events.Enqueue(item);
            while (_events.Count > options.EventRetention) _events.Dequeue();
        }
    }

    public JsonObject Events(long after)
    {
        lock (_eventsLock)
        {
            if (after < 0 || after > _eventId) throw WorkspaceFiles.Error("InvalidCursor", "Event cursor is outside this session's range.");
            var first = _events.TryPeek(out var item) ? item["eventId"]!.GetValue<long>() : _eventId + 1;
            return new JsonObject
            {
                ["events"] = new JsonArray(_events.Where(e => e["eventId"]!.GetValue<long>() > after).Select(e => e.DeepClone()).ToArray()),
                ["nextCursor"] = _eventId, ["firstAvailableCursor"] = first, ["gap"] = after < first - 1,
                ["recovery"] = after < first - 1 ? "Read a fresh workspace_status snapshot; older events were evicted." : null
            };
        }
    }

    public WorkspaceContext RequireContext(string handle)
    {
        if (!Contexts.TryGetValue(handle, out var context))
            throw WorkspaceFiles.Error("ContextHandleInvalid", "The context is not owned by this MCP session, or has expired. Connect again.");
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        // Unblock pending stdin before waiting for the per-session request gate.
        foreach (var context in Contexts.Values)
            foreach (var task in context.Tasks.Values) task.Cancel("Workspace session closed or expired.");
        await Gate.WaitAsync();
        try
        {
            foreach (var context in Contexts.Values) context.Dispose();
            Contexts.Clear();
            foreach (var peer in Peers.Values) await peer.DisposeAsync();
            Peers.Clear();
        }
        finally { Gate.Release(); }
    }
}

internal sealed class WorkspaceContext(string workspaceId, string cwd) : IDisposable
{
    public string Handle { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public string WorkspaceId { get; } = workspaceId;
    public string Cwd { get; } = cwd;
    public WorkspaceFiles? Files { get; init; }
    public WorkspaceDefinition? Definition { get; init; }
    public WorkspacePeer? Peer { get; init; }
    public string? RemoteHandle { get; init; }
    public string? RemoteInstanceId { get; init; }
    public JsonObject? Snapshot { get; set; }
    public ConcurrentDictionary<string, WorkspaceTerminal> Tasks { get; } = new(StringComparer.Ordinal);
    public void Dispose() { foreach (var task in Tasks.Values) task.Dispose(); Tasks.Clear(); }
}

internal sealed class WorkspacePeer(string id, HttpClient http, McpClient client) : IAsyncDisposable
{
    public string Id { get; } = id;
    public McpClient Client { get; } = client;
    public string? BrokerId { get; set; }
    public string? InstanceId { get; set; }
    public bool DesktopToolsAvailable { get; set; }
    public string State { get; set; } = "connecting";
    public string? Error { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public long EventCursor { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset NextProbeAtUtc { get; set; }
    public async ValueTask DisposeAsync() { await Client.DisposeAsync(); http.Dispose(); }
}
