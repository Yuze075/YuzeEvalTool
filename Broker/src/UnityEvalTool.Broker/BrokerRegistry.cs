using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace YuzeToolkit.Eval.Broker;

internal sealed class BrokerRegistry
{
    private sealed record InstanceEntry(UnityConnection? Connection, UnityInstanceSnapshot Snapshot,
        DateTimeOffset DisconnectedAtUtc);

    private sealed class Lease
    {
        public required string InstanceId { get; init; }
        public required int ProcessId { get; init; }
        public required DateTimeOffset ProcessStartedAtUtc { get; init; }
        public required string SessionId { get; init; }
        public DateTimeOffset LastUsedAtUtc { get; set; }
    }

    private sealed record PendingSessionRelease(string InstanceId, int ProcessId,
        DateTimeOffset ProcessStartedAtUtc, string SessionId, DateTimeOffset QueuedAtUtc);

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, InstanceEntry> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PendingSessionRelease> _pendingSessionReleases = new();
    private TaskCompletionSource _changed = CreateChangedSource();
    private long _revision;

    public long Revision
    {
        get { lock (_syncRoot) return _revision; }
    }

    public UnityConnection Register(System.Net.WebSockets.WebSocket socket, UnityRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.InstanceId) || string.IsNullOrWhiteSpace(registration.ProjectPath))
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                "Unity registration requires instanceId and projectPath.");

        var connection = new UnityConnection(socket, registration, OnStatus, OnHeartbeat);
        UnityConnection? previous = null;
        lock (_syncRoot)
        {
            if (_instances.TryGetValue(registration.InstanceId, out var existing))
            {
                if (existing.Snapshot.ProcessId != registration.ProcessId ||
                    existing.Snapshot.ProcessStartedAtUtc != registration.ProcessStartedAtUtc)
                    RemoveLeasesForInstance(registration.InstanceId);
                previous = existing.Connection;
            }

            _instances[registration.InstanceId] = new InstanceEntry(connection, connection.ToSnapshot(), default);
            SignalRegistryChanged();
        }

        if (previous != null) _ = previous.DisposeAsync();
        return connection;
    }

    public void MarkDisconnected(UnityConnection connection)
    {
        lock (_syncRoot)
        {
            var instanceId = connection.Registration.InstanceId;
            if (!_instances.TryGetValue(instanceId, out var entry) || !ReferenceEquals(entry.Connection, connection)) return;
            var snapshot = connection.ToSnapshot() with { IsConnected = false };
            _instances[instanceId] = new InstanceEntry(null, snapshot, DateTimeOffset.UtcNow);
            SignalRegistryChanged();
        }
    }

    public async Task BroadcastTokensAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        UnityConnection[] pending;
        lock (_syncRoot)
        {
            pending = _instances.Values
                .Select(entry => entry.Connection)
                .Where(connection => connection is { IsConnected: true, AuthorizationRequired: true, IsAuthorized: false })
                .Cast<UnityConnection>()
                .ToArray();
        }

        foreach (var connection in pending)
        {
            try
            {
                await connection.SendTokensAsync(tokens, cancellationToken);
            }
            catch (WebSocketException)
            {
                // The persisted tokens will be sent again when this Unity reconnects.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    public RegistrySnapshot GetSnapshot(string? connectionHandle = null, string? instanceId = null)
    {
        lock (_syncRoot)
        {
            Cleanup();
            UnityInstanceSnapshot? selected = null;
            if (!string.IsNullOrWhiteSpace(connectionHandle))
            {
                var lease = ResolveLease(connectionHandle, touch: true);
                selected = _instances.TryGetValue(lease.InstanceId, out var entry) ? entry.Snapshot : null;
            }
            else if (!string.IsNullOrWhiteSpace(instanceId))
            {
                selected = _instances.TryGetValue(instanceId, out var entry) ? entry.Snapshot : null;
            }

            var instances = _instances.Values.Select(entry => entry.Snapshot)
                .OrderByDescending(snapshot => snapshot.IsConnected)
                .ThenBy(snapshot => snapshot.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(snapshot => snapshot.InstanceId, StringComparer.Ordinal)
                .ToList();
            return new RegistrySnapshot(_revision, DateTimeOffset.UtcNow,
                instances.Count(instance => instance.IsConnected), connectionHandle, selected, instances);
        }
    }

    public ConnectionLeaseResult Connect(string instanceId, long registryRevision, string? sessionId = null)
    {
        lock (_syncRoot)
        {
            Cleanup();
            if (registryRevision <= 0)
                throw new BrokerOperationException(BrokerErrorCodes.DiscoveryRequired,
                    "Call unity_status first and pass its registryRevision to unity_connect.");
            if (registryRevision != _revision)
                throw new BrokerOperationException(BrokerErrorCodes.RegistryChanged,
                    $"Unity registry changed from revision {registryRevision} to {_revision}. Query unity_status again.");
            if (!_instances.TryGetValue(instanceId, out var entry))
                throw new BrokerOperationException(BrokerErrorCodes.UnityNotFound, $"Unity instance '{instanceId}' was not found.");
            if (!entry.Snapshot.IsConnected)
                throw new BrokerOperationException(BrokerErrorCodes.UnityDisconnected, $"Unity instance '{instanceId}' is disconnected.");

            var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            _leases[handle] = new Lease
            {
                InstanceId = instanceId,
                ProcessId = entry.Snapshot.ProcessId,
                ProcessStartedAtUtc = entry.Snapshot.ProcessStartedAtUtc,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? "mcp:" + handle : sessionId,
                LastUsedAtUtc = now
            };
            return new ConnectionLeaseResult(handle, now + BrokerConstants.LeaseIdleTimeout, entry.Snapshot);
        }
    }

    public async Task<RegistrySnapshot> WaitAsync(string? connectionHandle, string? instanceId, string waitFor,
        string? compilationCycleId, DateTimeOffset? observedAfterUtc, TimeSpan timeout, CancellationToken cancellationToken)
    {
        BrokerStatePolicy.ValidateWaitFor(waitFor);
        if (string.Equals(waitFor, "snapshot", StringComparison.OrdinalIgnoreCase) || timeout <= TimeSpan.Zero)
            return GetSnapshot(connectionHandle, instanceId);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            Task changedTask;
            RegistrySnapshot snapshot;
            lock (_syncRoot)
            {
                snapshot = GetSnapshot(connectionHandle, instanceId);
                if (BrokerStatePolicy.MatchesWait(snapshot.SelectedUnity, waitFor, compilationCycleId, observedAfterUtc))
                    return snapshot;
                changedTask = _changed.Task;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new BrokerOperationException(BrokerErrorCodes.RequestTimedOut,
                    $"Unity did not reach '{waitFor}' within {timeout.TotalSeconds:0}s.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(remaining);
            try { await changedTask.WaitAsync(timeoutSource.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BrokerOperationException(BrokerErrorCodes.RequestTimedOut,
                    $"Unity did not reach '{waitFor}' within {timeout.TotalSeconds:0}s.");
            }
        }
    }

    public async Task<JsonElement> ExecuteEvalAsync(string connectionHandle, string code, int timeoutSeconds,
        bool resetSession, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionHandle))
            throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleRequired,
                "Call unity_status, then unity_connect, and pass the returned connectionHandle to eval.");
        if (string.IsNullOrWhiteSpace(code))
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, "Eval code is required.");
        var (connection, snapshot) = ResolveConnected(connectionHandle);
        BrokerStatePolicy.EnsureCanExecute(snapshot);
        var normalizedTimeout = Math.Clamp(timeoutSeconds <= 0 ? 30 : timeoutSeconds, 1, 600);
        var requestId = Guid.NewGuid().ToString("N");
        var request = new UnityCommandRequest("mcp:" + connectionHandle, requestId, code, null,
            normalizedTimeout, resetSession);
        return await connection.RequestAsync("eval/execute", request,
            TimeSpan.FromSeconds(normalizedTimeout + 5), cancellationToken);
    }

    public async Task<JsonElement> ExecuteCliAsync(string connectionHandle, string consoleId, string line,
        CancellationToken cancellationToken)
    {
        var (connection, snapshot) = ResolveConnected(connectionHandle);
        BrokerStatePolicy.EnsureCanExecute(snapshot);
        var request = new UnityCommandRequest("cli:" + consoleId, Guid.NewGuid().ToString("N"), null, line,
            600, false);
        return await connection.RequestAsync("cli/execute", request, TimeSpan.FromMinutes(11), cancellationToken);
    }

    public void ReleaseLease(string connectionHandle, bool releaseSession = true)
    {
        if (string.IsNullOrWhiteSpace(connectionHandle)) return;
        lock (_syncRoot)
        {
            if (!_leases.Remove(connectionHandle, out var lease)) return;
            if (releaseSession) QueueSessionRelease(lease);
            Cleanup();
        }
    }

    public void ReleaseSupersededLease(string previousHandle, string replacementHandle)
    {
        if (string.IsNullOrWhiteSpace(previousHandle) ||
            string.Equals(previousHandle, replacementHandle, StringComparison.Ordinal)) return;
        lock (_syncRoot)
        {
            if (!_leases.Remove(previousHandle, out var previous)) return;
            var preserveSession = _leases.TryGetValue(replacementHandle, out var replacement) &&
                                  previous.ProcessId == replacement.ProcessId &&
                                  previous.ProcessStartedAtUtc == replacement.ProcessStartedAtUtc &&
                                  string.Equals(previous.InstanceId, replacement.InstanceId, StringComparison.Ordinal) &&
                                  string.Equals(previous.SessionId, replacement.SessionId, StringComparison.Ordinal);
            if (!preserveSession) QueueSessionRelease(previous);
            Cleanup();
        }
    }

    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot) Cleanup();

        var pendingCount = _pendingSessionReleases.Count;
        for (var index = 0; index < pendingCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pendingSessionReleases.TryDequeue(out var pending)) break;

            UnityConnection? connection;
            var retry = false;
            lock (_syncRoot)
            {
                if (!_instances.TryGetValue(pending.InstanceId, out var entry))
                {
                    retry = DateTimeOffset.UtcNow - pending.QueuedAtUtc <= BrokerConstants.DisconnectedRetention;
                    connection = null;
                }
                else if (entry.Snapshot.ProcessId != pending.ProcessId ||
                         entry.Snapshot.ProcessStartedAtUtc != pending.ProcessStartedAtUtc)
                {
                    connection = null;
                }
                else
                {
                    connection = entry.Connection is { IsConnected: true } ? entry.Connection : null;
                    retry = connection == null &&
                            DateTimeOffset.UtcNow - pending.QueuedAtUtc <= BrokerConstants.DisconnectedRetention;
                }
            }

            if (connection == null)
            {
                if (retry) _pendingSessionReleases.Enqueue(pending);
                continue;
            }

            try
            {
                await connection.ReleaseSessionAsync(pending.SessionId, cancellationToken);
            }
            catch (BrokerOperationException) when (DateTimeOffset.UtcNow - pending.QueuedAtUtc <=
                                                   BrokerConstants.DisconnectedRetention)
            {
                _pendingSessionReleases.Enqueue(pending);
            }
        }
    }

    private (UnityConnection Connection, UnityInstanceSnapshot Snapshot) ResolveConnected(string handle)
    {
        lock (_syncRoot)
        {
            Cleanup();
            var lease = ResolveLease(handle, touch: true);
            if (!_instances.TryGetValue(lease.InstanceId, out var entry))
                throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleInvalid,
                    "The selected Unity instance is no longer registered. Query and connect again.");
            if (entry.Snapshot.ProcessId != lease.ProcessId || entry.Snapshot.ProcessStartedAtUtc != lease.ProcessStartedAtUtc)
                throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleInvalid,
                    "The selected Unity process was replaced. Query and connect again.");
            if (entry.Connection == null || !entry.Connection.IsConnected)
                throw new BrokerOperationException(BrokerErrorCodes.UnityDisconnected,
                    $"Unity instance '{lease.InstanceId}' is temporarily disconnected.");
            return (entry.Connection, entry.Connection.ToSnapshot());
        }
    }

    private Lease ResolveLease(string handle, bool touch)
    {
        if (!_leases.TryGetValue(handle, out var lease))
            throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleInvalid,
                "The connection handle is invalid or expired. Query unity_status and connect again.");
        if (DateTimeOffset.UtcNow - lease.LastUsedAtUtc > BrokerConstants.LeaseIdleTimeout)
        {
            _leases.Remove(handle);
            QueueSessionRelease(lease);
            throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleInvalid,
                "The connection handle expired. Query unity_status and connect again.");
        }
        if (touch) lease.LastUsedAtUtc = DateTimeOffset.UtcNow;
        return lease;
    }

    private void OnStatus(UnityConnection connection, UnityStatus status)
    {
        lock (_syncRoot)
        {
            var id = connection.Registration.InstanceId;
            if (!_instances.TryGetValue(id, out var entry) || !ReferenceEquals(entry.Connection, connection)) return;
            _instances[id] = entry with { Snapshot = connection.ToSnapshot() };
            SignalStatusChanged();
        }
    }

    private void OnHeartbeat(UnityConnection connection)
    {
        lock (_syncRoot)
        {
            var id = connection.Registration.InstanceId;
            if (!_instances.TryGetValue(id, out var entry) || !ReferenceEquals(entry.Connection, connection)) return;
            _instances[id] = entry with { Snapshot = connection.ToSnapshot() };
            SignalStatusChanged();
        }
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var handle in _leases.Where(pair => now - pair.Value.LastUsedAtUtc > BrokerConstants.LeaseIdleTimeout)
                     .Select(pair => pair.Key).ToArray())
        {
            var lease = _leases[handle];
            _leases.Remove(handle);
            QueueSessionRelease(lease);
        }
        foreach (var instanceId in _instances.Where(pair => pair.Value.Connection == null &&
                                                            now - pair.Value.DisconnectedAtUtc > BrokerConstants.DisconnectedRetention)
                     .Select(pair => pair.Key).ToArray())
        {
            foreach (var pair in _leases.Where(pair => pair.Value.InstanceId == instanceId).ToArray())
            {
                _leases.Remove(pair.Key);
                QueueSessionRelease(pair.Value);
            }
            _instances.Remove(instanceId);
            SignalRegistryChanged();
        }
    }

    private void QueueSessionRelease(Lease lease)
    {
        _pendingSessionReleases.Enqueue(new PendingSessionRelease(lease.InstanceId, lease.ProcessId,
            lease.ProcessStartedAtUtc, lease.SessionId, DateTimeOffset.UtcNow));
    }

    private void RemoveLeasesForInstance(string instanceId)
    {
        foreach (var handle in _leases.Where(pair => pair.Value.InstanceId == instanceId).Select(pair => pair.Key).ToArray())
            _leases.Remove(handle);
    }

    private void SignalRegistryChanged()
    {
        _revision++;
        SignalStatusChanged();
    }

    private void SignalStatusChanged()
    {
        var previous = _changed;
        _changed = CreateChangedSource();
        previous.TrySetResult();
    }

    private static TaskCompletionSource CreateChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
