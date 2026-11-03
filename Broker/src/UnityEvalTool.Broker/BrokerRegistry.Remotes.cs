#nullable enable
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace YuzeToolkit.Eval.Broker;

internal sealed partial class BrokerRegistry
{
    private sealed class RemotePeer(RemoteBrokerOptions options)
    {
        public RemoteBrokerOptions Options { get; } = options;
        public McpClient? Client { get; set; }
        public RegistrySnapshot? Snapshot { get; set; }
        public FederationInfo? Identity { get; set; }
        public string State { get; set; } = options.Enabled ? "connecting" : "disabled";
        public string? ErrorCode { get; set; }
        public DateTimeOffset? LastSeenAtUtc { get; set; }
        public DateTimeOffset? DisconnectedAtUtc { get; set; }
    }

    private sealed class RemoteLease(RemotePeer peer, string handle, UnityInstanceSnapshot unity,
        string brokerInstanceId, TimeSpan idleTimeout, DateTimeOffset expiresAtUtc)
    {
        public RemotePeer Peer { get; } = peer;
        public string Handle { get; } = handle;
        public UnityInstanceSnapshot Unity { get; } = unity;
        public string BrokerInstanceId { get; } = brokerInstanceId;
        public TimeSpan IdleTimeout { get; } = idleTimeout;
        public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
        public int ActiveRequests { get; set; }
    }

    private readonly BrokerConfiguration _configuration;
    private readonly Dictionary<string, RemotePeer> _remotePeers;
    private readonly Dictionary<string, RemoteLease> _remoteLeases = new(StringComparer.Ordinal);
    private long _aggregateRevision = 1;

    public BrokerRegistry(BrokerConfiguration? configuration = null)
    {
        _configuration = configuration ?? new BrokerConfiguration();
        _remotePeers = _configuration.Remotes.ToDictionary(remote => remote.Id,
            remote => new RemotePeer(remote), StringComparer.Ordinal);
    }

    private UnityInstanceSnapshot DescribeLocal(UnityInstanceSnapshot unity) => unity with
    {
        WorkspaceId = _configuration.Projects.SingleOrDefault(project => WorkspaceFiles.SamePath(unity.ProjectPath, project.RootPath))?.Id,
        Source = new UnityProjectSource("local", null, _configuration.BrokerId, false)
    };

    private FederationInfo DescribeFederation(string view) => new(1, _configuration.BrokerId,
        BrokerHost.InstanceId, view, (int)BrokerConstants.LeaseIdleTimeout.TotalSeconds);

    private static string RemoteInstanceId(RemotePeer peer, string instanceId) =>
        $"remote:{peer.Options.Id}:{instanceId}";

    private static UnityInstanceSnapshot DescribeRemote(RemotePeer peer, UnityInstanceSnapshot unity) => unity with
    {
        InstanceId = RemoteInstanceId(peer, unity.InstanceId),
        IsConnected = peer.State == "online" && unity.IsConnected,
        Source = new UnityProjectSource("remote", peer.Options.Id, unity.Source.BrokerId, peer.State != "online"),
        Status = peer.State == "online" ? unity.Status : unity.Status with
        { CanEval = false, BusyReason = "Remote Broker is unavailable; this is a retained snapshot." }
    };

    public RegistrySnapshot GetProjectSnapshot(string? connectionHandle = null, string? instanceId = null)
    {
        lock (_syncRoot)
        {
            CleanupRemoteState();
            var remoteLease = string.IsNullOrEmpty(connectionHandle) ? null : ResolveRemoteLease(connectionHandle);
            var local = GetSnapshot(remoteLease == null ? connectionHandle : null, instanceId);
            var instances = local.UnityInstances.Concat(_remotePeers.Values
                    .Where(peer => peer.Snapshot != null)
                    .SelectMany(peer => peer.Snapshot!.UnityInstances.Select(unity => DescribeRemote(peer, unity))))
                .OrderByDescending(unity => unity.IsConnected)
                .ThenBy(unity => unity.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(unity => unity.InstanceId, StringComparer.Ordinal).ToList();
            var selectedId = remoteLease == null ? instanceId : RemoteInstanceId(remoteLease.Peer, remoteLease.Unity.InstanceId);
            var selected = remoteLease == null && !string.IsNullOrEmpty(connectionHandle)
                ? local.SelectedUnity
                : instances.FirstOrDefault(unity => unity.InstanceId == selectedId);
            return new RegistrySnapshot(_aggregateRevision, DateTimeOffset.UtcNow,
                instances.Count(unity => unity.IsConnected), connectionHandle, selected, instances)
            {
                Federation = DescribeFederation("aggregate"),
                RemoteBrokers = _remotePeers.Values.Select(peer => new RemoteBrokerSnapshot(peer.Options.Id,
                    peer.Options.Endpoint.AbsoluteUri, peer.State, peer.ErrorCode, peer.LastSeenAtUtc,
                    peer.Snapshot?.UnityInstances.Count ?? 0)).ToList()
            };
        }
    }

    public ConnectionLeaseResult ConnectCli(string instanceId, long registryRevision, string sessionId)
    {
        lock (_syncRoot)
        {
            ValidateAggregateRevision(registryRevision);
            if (instanceId.StartsWith("remote:", StringComparison.Ordinal))
                throw new BrokerOperationException(BrokerErrorCodes.RemoteCliUnsupported,
                    "Remote projects support MCP unity_status / unity_connect / eval. Interactive CLI execution is local only.");
            return Connect(instanceId, _revision, sessionId);
        }
    }

    public async Task<ConnectionLeaseResult> ConnectProjectAsync(string instanceId, long registryRevision,
        CancellationToken cancellationToken)
    {
        RemotePeer peer;
        RegistrySnapshot snapshot;
        UnityInstanceSnapshot unity;
        McpClient client;
        lock (_syncRoot)
        {
            ValidateAggregateRevision(registryRevision);
            if (_instances.ContainsKey(instanceId)) return Connect(instanceId, _revision);
            (peer, unity) = FindRemoteInstance(instanceId);
            client = RequireRemoteClient(peer);
            snapshot = peer.Snapshot!;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var result = await CallRemoteAsync(peer, client, "unity_connect", new JsonObject
        {
            ["instanceId"] = unity.InstanceId,
            ["registryRevision"] = snapshot.RegistryRevision
        }, TimeSpan.FromSeconds(_configuration.ConnectionTimeoutSeconds), false, cancellationToken);
        var connected = DeserializeRemoteResult(result, BrokerJsonContext.Default.ConnectionLeaseResult);
        if (string.IsNullOrWhiteSpace(connected.ConnectionHandle) || connected.Unity == null ||
            connected.Unity.InstanceId != unity.InstanceId || connected.Unity.ProcessId != unity.ProcessId ||
            connected.Unity.ProcessStartedAtUtc != unity.ProcessStartedAtUtc || connected.Unity.Source == null ||
            connected.Unity.Source.Kind != "local" || connected.Unity.Source.BrokerId != snapshot.Federation!.BrokerId)
            throw Incompatible("unity_connect returned a different Unity instance.");
        lock (_syncRoot)
        {
            if (peer.Snapshot?.Federation?.BrokerInstanceId != snapshot.Federation!.BrokerInstanceId || peer.State != "online")
                throw new BrokerOperationException(BrokerErrorCodes.RegistryChanged, "Remote Broker changed while connecting; discover again.");
            var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var expires = RemoteDeadline(snapshot, startedAt);
            _remoteLeases.Add(handle, new RemoteLease(peer, connected.ConnectionHandle, connected.Unity,
                snapshot.Federation.BrokerInstanceId, RemoteIdleTimeout(snapshot), expires));
            return new ConnectionLeaseResult(handle, expires, DescribeRemote(peer, connected.Unity));
        }
    }

    public async Task<RegistrySnapshot> WaitForProjectAsync(string? connectionHandle, string? instanceId,
        string waitFor, string? compilationCycleId, DateTimeOffset? observedAfterUtc, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        BrokerStatePolicy.ValidateWaitFor(waitFor);
        RemotePeer? peer = null;
        RemoteLease? lease;
        UnityInstanceSnapshot? unity = null;
        McpClient? client = null;
        string? requestedEpoch = null;
        lock (_syncRoot)
        {
            CleanupRemoteState();
            lease = string.IsNullOrEmpty(connectionHandle) ? null : ResolveRemoteLease(connectionHandle);
            if (lease != null) { peer = lease.Peer; unity = lease.Unity; }
            else if (string.IsNullOrEmpty(connectionHandle) && instanceId?.StartsWith("remote:", StringComparison.Ordinal) == true)
                (peer, unity) = FindRemoteInstance(instanceId);
            if (peer != null)
            {
                client = RequireRemoteClient(peer);
                requestedEpoch = peer.Identity!.BrokerInstanceId;
                if (lease != null) lease.ActiveRequests++;
            }
        }
        if (peer == null)
        {
            var local = await WaitAsync(connectionHandle, instanceId, waitFor, compilationCycleId, observedAfterUtc,
                timeout, cancellationToken);
            return GetProjectSnapshot(connectionHandle, instanceId) with
            { CapturedAtUtc = local.CapturedAtUtc, SelectedUnity = local.SelectedUnity };
        }

        try
        {
            var result = await CallRemoteAsync(peer, client!, "unity_status", new JsonObject
            {
                ["connectionHandle"] = lease?.Handle ?? string.Empty,
                ["instanceId"] = lease == null ? unity!.InstanceId : string.Empty,
                ["waitFor"] = waitFor,
                ["compilationCycleId"] = compilationCycleId ?? string.Empty,
                ["observedAfterUtc"] = observedAfterUtc?.ToString("O") ?? string.Empty,
                ["timeoutSeconds"] = (int)timeout.TotalSeconds
            }, timeout + TimeSpan.FromSeconds(_configuration.ConnectionTimeoutSeconds + _configuration.TransportGraceSeconds),
                false, cancellationToken);
            var remote = ReadSnapshot(result);
            lock (_syncRoot)
            {
                if (peer.Identity?.BrokerInstanceId != requestedEpoch || remote.Federation!.BrokerInstanceId != requestedEpoch)
                    throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleInvalid,
                        "Remote Broker changed during status retrieval; discover and connect again.");
                // A late successful read must not revive a client already retired by discovery.
                if (peer.State == "online" && ReferenceEquals(peer.Client, client))
                    PublishRemoteSnapshot(peer, remote);
                if (remote.SelectedUnity == null || remote.SelectedUnity.InstanceId != unity!.InstanceId)
                    throw Incompatible("unity_status did not return the selected local Unity instance.");
                if (lease != null)
                {
                    ValidateRemoteLease(lease);
                    // The remote status wait touches its lease on the final state observation.
                    RenewRemoteLease(lease, DateTimeOffset.UtcNow);
                }
                // This timestamp belongs to the selected remote, so observedAfterUtc stays in its clock domain.
                return GetProjectSnapshot(connectionHandle, instanceId) with
                { CapturedAtUtc = remote.CapturedAtUtc, SelectedUnity = DescribeRemote(peer, remote.SelectedUnity) };
            }
        }
        finally
        {
            if (lease != null) lock (_syncRoot) lease.ActiveRequests--;
        }
    }

    public async Task<CallToolResult> ExecuteProjectEvalAsync(string connectionHandle, string code,
        int timeoutSeconds, bool resetSession, CancellationToken cancellationToken)
    {
        RemoteLease? lease;
        McpClient? client = null;
        lock (_syncRoot)
        {
            lease = ResolveRemoteLease(connectionHandle);
            if (lease != null)
            {
                client = RequireRemoteClient(lease.Peer);
                lease.ActiveRequests++;
            }
        }
        if (lease == null)
            return UnityToolResultConverter.Convert(await ExecuteEvalAsync(connectionHandle, code, timeoutSeconds,
                resetSession, cancellationToken));
        try
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, "Eval code is required.");
            var timeout = Math.Clamp(timeoutSeconds <= 0 ? 30 : timeoutSeconds, 1, 600);
            var startedAt = DateTimeOffset.UtcNow;
            var result = await CallRemoteAsync(lease.Peer, client!, "eval", new JsonObject
            {
                ["connectionHandle"] = lease.Handle,
                ["code"] = code,
                ["timeout"] = timeout,
                ["resetSession"] = resetSession
            }, TimeSpan.FromSeconds(timeout + _configuration.TransportGraceSeconds), true, cancellationToken);
            lock (_syncRoot)
                RenewRemoteLease(lease, startedAt);
            return result;
        }
        finally { lock (_syncRoot) lease.ActiveRequests--; }
    }

    public async Task RunRemoteDiscoveryAsync(CancellationToken stoppingToken)
    {
        using var discoverySlots = new SemaphoreSlim(_configuration.MaxConcurrentDiscoveries);
        await Task.WhenAll(_remotePeers.Values.Where(peer => peer.Options.Enabled)
            .Select(peer => RunRemotePeerAsync(peer, discoverySlots, stoppingToken)));
    }

    private async Task RunRemotePeerAsync(RemotePeer peer, SemaphoreSlim discoverySlots, CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            McpClient? client = null;
            var timeout = TimeSpan.FromSeconds(_configuration.ConnectionTimeoutSeconds);
            using var http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = timeout
            }) { Timeout = TimeSpan.FromSeconds(3600 + _configuration.ConnectionTimeoutSeconds + _configuration.TransportGraceSeconds) };
            var headers = new Dictionary<string, string>();
            var credential = _configuration.GetCredential(peer.Options.CredentialRef);
            if (credential != null) headers.Add("Authorization", "Bearer " + credential);
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = peer.Options.Endpoint,
                Name = peer.Options.Id,
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false,
                MaxReconnectionAttempts = 0,
                ConnectionTimeout = timeout,
                AdditionalHeaders = headers
            }, http, ownsHttpClient: false);
            try
            {
                await discoverySlots.WaitAsync(stoppingToken);
                try
                {
                    client = await McpClient.CreateAsync(transport, new McpClientOptions
                    {
                        ProtocolVersion = "2025-11-25",
                        InitializationTimeout = timeout,
                        ClientInfo = new Implementation { Name = "UnityEvalTool.Broker", Version = BrokerConstants.PackageVersion }
                    }, cancellationToken: stoppingToken);
                }
                finally { discoverySlots.Release(); }
                while (!stoppingToken.IsCancellationRequested)
                {
                    await discoverySlots.WaitAsync(stoppingToken);
                    try
                    {
                        var result = await InvokeRemoteAsync(client, "unity_status", new JsonObject(), timeout, stoppingToken);
                        PublishRemoteSnapshot(peer, ReadSnapshot(result), client);
                        failures = 0;
                    }
                    finally { discoverySlots.Release(); }
                    await Task.Delay(TimeSpan.FromSeconds(_configuration.DiscoveryIntervalSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (IsRemoteFailure(ex))
            {
                MarkRemoteUnavailable(peer, RemoteFailureCode(ex));
                failures++;
            }
            finally
            {
                lock (_syncRoot) peer.Client = null;
                if (client != null) await client.DisposeAsync();
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(_configuration.ReconnectMaxDelaySeconds,
                    Math.Pow(2, Math.Min(failures - 1, 10)))), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<CallToolResult> CallRemoteAsync(RemotePeer peer, McpClient client, string tool,
        JsonObject arguments, TimeSpan timeout, bool mayExecute, CancellationToken cancellationToken)
    {
        try { return await InvokeRemoteAsync(client, tool, arguments, timeout, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (mayExecute)
                throw new BrokerOperationException(BrokerErrorCodes.ExecutionOutcomeUnknown,
                    $"Remote '{peer.Options.Id}' eval was interrupted. Do not automatically retry.", true);
            throw;
        }
        catch (Exception ex) when (IsRemoteFailure(ex))
        {
            var code = RemoteFailureCode(ex);
            // Only a JSON-RPC error proves the peer replied. Transport/session closure remains ambiguous.
            if (ex is McpProtocolException) throw;
            lock (_syncRoot)
                if (ReferenceEquals(peer.Client, client)) MarkRemoteUnavailable(peer, code);
            throw new BrokerOperationException(mayExecute ? BrokerErrorCodes.ExecutionOutcomeUnknown : code,
                $"Remote '{peer.Options.Id}' request failed. Inspect unity_status.remoteBrokers; do not replay interrupted eval.", mayExecute);
        }
    }

    private static Task<CallToolResult> InvokeRemoteAsync(McpClient client, string tool, JsonObject arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            using var document = JsonDocument.Parse(pair.Value?.ToJsonString() ?? "null");
            parameters.Add(pair.Key, document.RootElement.Clone());
        }
        return client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = parameters }, cancellationToken)
            .AsTask().WaitAsync(timeout, cancellationToken);
    }

    private static JsonElement ReadRemoteResult(CallToolResult result)
    {
        if (result.IsError == true)
            throw new McpException(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } content ||
            !content.TryGetProperty("result", out var payload))
            throw Incompatible("The remote does not return structured Unity Broker results.");
        return payload.Clone();
    }

    private static RegistrySnapshot ReadSnapshot(CallToolResult result)
    {
        var snapshot = DeserializeRemoteResult(result, BrokerJsonContext.Default.RegistrySnapshot);
        var info = snapshot.Federation;
        if (info == null || info.Version != 1 || info.View != "local" ||
            !Guid.TryParseExact(info.BrokerId, "N", out _) || !Guid.TryParseExact(info.BrokerInstanceId, "N", out _) ||
            info.LeaseIdleTimeoutSeconds is <= 0 or > 86400 || snapshot.UnityInstances == null || snapshot.RegistryRevision < 0)
            throw Incompatible("Configure a compatible Broker network endpoint that exports local projects only.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unity in snapshot.UnityInstances)
        {
            if (unity == null || string.IsNullOrWhiteSpace(unity.InstanceId) || !ids.Add(unity.InstanceId) ||
                unity.InstanceId.StartsWith("remote:", StringComparison.Ordinal) || unity.Source == null ||
                unity.Source.Kind != "local" || unity.Source.BrokerId != info.BrokerId || unity.Source.RemoteId != null ||
                unity.Source.IsStale || unity.Status == null || string.IsNullOrWhiteSpace(unity.ProjectPath))
                throw Incompatible("Remote registry contains an invalid or imported Unity instance.");
        }
        if (snapshot.SelectedUnity != null && !snapshot.UnityInstances.Any(unity => unity == snapshot.SelectedUnity))
            throw Incompatible("Remote selectedUnity must belong to its local registry.");
        return snapshot;
    }

    private static T DeserializeRemoteResult<T>(CallToolResult result,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return ReadRemoteResult(result).Deserialize(typeInfo)
                   ?? throw Incompatible("The remote returned an empty structured result.");
        }
        catch (JsonException)
        {
            throw Incompatible("The remote structured result does not match the Unity Broker contract.");
        }
    }

    private void PublishRemoteSnapshot(RemotePeer peer, RegistrySnapshot snapshot, McpClient? client = null)
    {
        lock (_syncRoot)
        {
            var info = snapshot.Federation!;
            if (info.BrokerId == _configuration.BrokerId || _remotePeers.Values.Any(other =>
                    other != peer && other.Identity?.BrokerId == info.BrokerId) ||
                (peer.Identity != null && peer.Identity.BrokerId != info.BrokerId))
                throw new BrokerOperationException(BrokerErrorCodes.RemoteIdentityConflict,
                    "A remote points to this Broker or duplicates another configured Broker.");
            var previous = peer.Snapshot;
            if (previous?.Federation?.BrokerInstanceId == info.BrokerInstanceId &&
                snapshot.RegistryRevision < previous.RegistryRevision) return;
            if (peer.Identity?.BrokerInstanceId != info.BrokerInstanceId)
                foreach (var pair in _remoteLeases.Where(pair => pair.Value.Peer == peer).ToArray())
                    _remoteLeases.Remove(pair.Key);
            if (peer.State != "online" || previous?.RegistryRevision != snapshot.RegistryRevision ||
                peer.Identity?.BrokerInstanceId != info.BrokerInstanceId)
                _aggregateRevision++;
            peer.Identity = info;
            if (client != null) peer.Client = client;
            peer.Snapshot = snapshot;
            peer.LastSeenAtUtc = DateTimeOffset.UtcNow;
            peer.DisconnectedAtUtc = null;
            peer.State = "online";
            peer.ErrorCode = null;
        }
    }

    private void MarkRemoteUnavailable(RemotePeer peer, string code)
    {
        lock (_syncRoot)
        {
            if (peer.State != "offline" || peer.ErrorCode != code) _aggregateRevision++;
            peer.State = "offline";
            peer.ErrorCode = code;
            peer.DisconnectedAtUtc ??= DateTimeOffset.UtcNow;
            if (code is BrokerErrorCodes.RemoteIncompatible or BrokerErrorCodes.RemoteIdentityConflict or BrokerErrorCodes.AuthenticationFailed)
            {
                peer.Snapshot = null;
                foreach (var pair in _remoteLeases.Where(pair => pair.Value.Peer == peer).ToArray())
                    _remoteLeases.Remove(pair.Key);
            }
        }
    }

    private void CleanupRemoteState()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _remoteLeases.Where(pair => pair.Value.ActiveRequests == 0 && pair.Value.ExpiresAtUtc <= now).ToArray())
            _remoteLeases.Remove(pair.Key);
        foreach (var peer in _remotePeers.Values)
        {
            if (peer.Snapshot != null && peer.DisconnectedAtUtc.HasValue &&
                now - peer.DisconnectedAtUtc.Value > TimeSpan.FromSeconds(_configuration.RemoteRetentionSeconds))
            {
                peer.Snapshot = null;
                _aggregateRevision++;
            }
        }
    }

    private void ValidateAggregateRevision(long revision)
    {
        Cleanup();
        CleanupRemoteState();
        if (revision <= 0)
            throw new BrokerOperationException(BrokerErrorCodes.DiscoveryRequired, "Call unity_status first.");
        if (revision != _aggregateRevision)
            throw new BrokerOperationException(BrokerErrorCodes.RegistryChanged, "Project registry changed; call unity_status again.");
    }

    private (RemotePeer Peer, UnityInstanceSnapshot Unity) FindRemoteInstance(string instanceId)
    {
        foreach (var peer in _remotePeers.Values)
        {
            var unity = peer.Snapshot?.UnityInstances.FirstOrDefault(unity => RemoteInstanceId(peer, unity.InstanceId) == instanceId);
            if (unity != null) return (peer, unity);
        }
        throw new BrokerOperationException(BrokerErrorCodes.UnityNotFound, "The selected remote Unity is not registered.");
    }

    private RemoteLease? ResolveRemoteLease(string handle)
    {
        if (!_remoteLeases.TryGetValue(handle, out var lease)) return null;
        if (lease.ActiveRequests == 0 && lease.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _remoteLeases.Remove(handle);
            throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleInvalid, "Remote lease expired; discover and connect again.");
        }
        ValidateRemoteLease(lease);
        return lease;
    }

    private static void ValidateRemoteLease(RemoteLease lease)
    {
        var snapshot = lease.Peer.Snapshot;
        if (lease.Peer.Identity?.BrokerInstanceId != lease.BrokerInstanceId)
            throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleInvalid,
                "Remote Broker process changed; discover and connect again.");
        if (snapshot == null) return; // The transport availability check reports a retained/offline source.
        var current = snapshot.UnityInstances.FirstOrDefault(unity => unity.InstanceId == lease.Unity.InstanceId);
        if (snapshot.Federation!.BrokerInstanceId != lease.BrokerInstanceId || current == null ||
            current.ProcessId != lease.Unity.ProcessId || current.ProcessStartedAtUtc != lease.Unity.ProcessStartedAtUtc)
            throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleInvalid,
                "Remote Broker or Unity process changed; discover and connect again.");
    }

    private static McpClient RequireRemoteClient(RemotePeer peer) => peer.State == "online" && peer.Client != null
        ? peer.Client
        : throw new BrokerOperationException(BrokerErrorCodes.RemoteUnavailable, $"Remote '{peer.Options.Id}' is unavailable.");

    private static TimeSpan RemoteIdleTimeout(RegistrySnapshot snapshot) =>
        TimeSpan.FromSeconds(Math.Min(snapshot.Federation!.LeaseIdleTimeoutSeconds, BrokerConstants.LeaseIdleTimeout.TotalSeconds));

    private static DateTimeOffset RemoteDeadline(RegistrySnapshot snapshot, DateTimeOffset requestStartedAt) =>
        requestStartedAt + RemoteIdleTimeout(snapshot);

    private static void RenewRemoteLease(RemoteLease lease, DateTimeOffset requestStartedAt)
    {
        var deadline = requestStartedAt + lease.IdleTimeout;
        if (deadline > lease.ExpiresAtUtc) lease.ExpiresAtUtc = deadline;
    }

    private static BrokerOperationException Incompatible(string message) =>
        new(BrokerErrorCodes.RemoteIncompatible, message);

    private static bool IsRemoteFailure(Exception error) => error is HttpRequestException or IOException or InvalidDataException or
        TimeoutException or OperationCanceledException or McpException or ClientTransportClosedException or JsonException or ObjectDisposedException;

    private static string RemoteFailureCode(Exception error) => error switch
    {
        BrokerOperationException operation => operation.Code,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => BrokerErrorCodes.AuthenticationFailed,
        JsonException or InvalidDataException or McpProtocolException => BrokerErrorCodes.RemoteIncompatible,
        _ => BrokerErrorCodes.RemoteUnavailable
    };
}
