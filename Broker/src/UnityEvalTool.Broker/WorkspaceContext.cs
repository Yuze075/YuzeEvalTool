#nullable enable
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

// A lease on one workspace, valid only on the listener that created it. Local contexts (network
// export view) own files and terminal tasks; remote contexts (gateway view) forward to a peer context.
internal sealed class WorkspaceContext(bool networkExport, string workspaceId, string cwd) : IDisposable
{
    private long _lastUsedTicks = DateTimeOffset.UtcNow.UtcTicks;
    public string Handle { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public bool NetworkExport { get; } = networkExport;
    public string WorkspaceId { get; } = workspaceId;
    public string Cwd { get; } = cwd;
    public WorkspaceFiles? Files { get; init; }
    public WorkspaceDefinition? Definition { get; init; }
    public string? HostId { get; init; }
    public string? RemoteHandle { get; init; }
    public string? RemoteBrokerInstanceId { get; init; }
    public JsonObject? Snapshot { get; set; }
    public ConcurrentDictionary<string, WorkspaceTerminal> Tasks { get; } = new(StringComparer.Ordinal);
    public int ActiveRequests;
    public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);
    public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTimeOffset.UtcNow.UtcTicks);
    public void Dispose() { foreach (var task in Tasks.Values) task.Dispose(); Tasks.Clear(); }
}
