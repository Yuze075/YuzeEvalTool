using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace YuzeToolkit.Eval.Broker;

internal sealed class UnityConnection : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtocolEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<UnityConnection, UnityStatus> _onStatus;
    private readonly Action<UnityConnection> _onHeartbeat;
    private string _authorizationState;
    private int _disposed;

    public UnityConnection(WebSocket socket, UnityRegistration registration,
        Action<UnityConnection, UnityStatus> onStatus, Action<UnityConnection> onHeartbeat)
    {
        _socket = socket;
        Registration = registration;
        Status = registration.Status;
        ConnectedAtUtc = DateTimeOffset.UtcNow;
        LastTransportHeartbeatAtUtc = ConnectedAtUtc;
        _onStatus = onStatus;
        _onHeartbeat = onHeartbeat;
        _authorizationState = registration.AuthorizationRequired
            ? NormalizeAuthorizationState(registration.AuthorizationState, "Pending")
            : "NotRequired";
    }

    public UnityRegistration Registration { get; }
    public UnityStatus Status { get; private set; }
    public DateTimeOffset ConnectedAtUtc { get; }
    public DateTimeOffset LastTransportHeartbeatAtUtc { get; private set; }
    public bool IsConnected => _socket.State == WebSocketState.Open && Volatile.Read(ref _disposed) == 0;
    public bool AuthorizationRequired => Registration.AuthorizationRequired;
    public string AuthorizationState => Volatile.Read(ref _authorizationState);
    public bool IsAuthorized => !AuthorizationRequired ||
                                string.Equals(AuthorizationState, "Authorized", StringComparison.Ordinal);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            while (!linked.Token.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var document = await WebSocketJson.ReceiveAsync(_socket, linked.Token);
                if (document == null) break;
                LastTransportHeartbeatAtUtc = DateTimeOffset.UtcNow;
                _onHeartbeat(this);
                var envelope = WebSocketJson.ParseEnvelope(document);
                if (string.Equals(envelope.Type, "response", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(envelope.Id) && _pending.TryRemove(envelope.Id, out var completion))
                        completion.TrySetResult(envelope);
                    continue;
                }

                if (string.Equals(envelope.Type, "event", StringComparison.Ordinal) &&
                    string.Equals(envelope.Method, "unity/status", StringComparison.Ordinal))
                {
                    var status = envelope.Payload.Deserialize(BrokerJsonContext.Default.UnityStatus)
                                 ?? throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, "Unity status payload is empty.");
                    Status = status;
                    _onStatus(this, status);
                    continue;
                }

                if (string.Equals(envelope.Type, "event", StringComparison.Ordinal) &&
                    string.Equals(envelope.Method, "unity/heartbeat", StringComparison.Ordinal))
                    continue;

                if (string.Equals(envelope.Type, "event", StringComparison.Ordinal) &&
                    string.Equals(envelope.Method, "unity/authorization", StringComparison.Ordinal))
                {
                    var update = envelope.Payload.Deserialize(BrokerJsonContext.Default.UnityAuthorizationUpdate)
                                 ?? throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                                     "Unity authorization payload is empty.");
                    _authorizationState = AuthorizationRequired
                        ? NormalizeAuthorizationState(update.State, "Pending")
                        : "NotRequired";
                    _onHeartbeat(this);
                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The connection was deliberately invalidated after an ambiguous transport send.
        }
    }

    public async Task<JsonElement> RequestAsync(string method, UnityCommandRequest request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsConnected)
            throw new BrokerOperationException(BrokerErrorCodes.UnityDisconnected,
                $"Unity instance '{Registration.InstanceId}' is disconnected.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(timeout);
        var gateAcquired = false;
        var lateCompletionOwnsGate = false;
        try
        {
            try
            {
                await _executionGate.WaitAsync(deadline.Token);
                gateAcquired = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (_lifetime.IsCancellationRequested)
                    throw new BrokerOperationException(BrokerErrorCodes.UnityDisconnected,
                        $"Unity disconnected while '{method}' was queued; the command was not sent.");
                throw new BrokerOperationException(BrokerErrorCodes.RequestTimedOut,
                    $"Unity request '{method}' timed out while queued and was not sent.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected)
                throw new BrokerOperationException(BrokerErrorCodes.UnityDisconnected,
                    $"Unity instance '{Registration.InstanceId}' disconnected before '{method}' was sent.");

            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<ProtocolEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate Broker request id.");
            var payload = WebSocketJson.ToElement(request, BrokerJsonContext.Default.UnityCommandRequest);
            var message = WebSocketJson.CreateEnvelope("request", method, id, payload);
            var sendAttempted = false;
            var sendCompleted = false;
            var lateCompletionOwnsPending = false;
            try
            {
                sendAttempted = true;
                await WebSocketJson.SendAsync(_socket, message, _sendGate, deadline.Token);
                sendCompleted = true;
                var envelope = await completion.Task.WaitAsync(deadline.Token);
                if (envelope.Error != null)
                    throw new BrokerOperationException(envelope.Error.Code, envelope.Error.Message, envelope.Error.MayHaveExecuted);
                return envelope.Payload.Clone();
            }
            catch (OperationCanceledException)
            {
                if (sendAttempted)
                {
                    _ = ObserveLateCompletionAsync(id, completion);
                    lateCompletionOwnsPending = true;
                    lateCompletionOwnsGate = true;
                    if (!sendCompleted)
                    {
                        _socket.Abort();
                        _lifetime.Cancel();
                    }
                    throw new BrokerOperationException(BrokerErrorCodes.ExecutionOutcomeUnknown,
                        $"Unity '{method}' was interrupted after sending began. The command may have executed.",
                        true);
                }
                if (cancellationToken.IsCancellationRequested) throw;
                if (_lifetime.IsCancellationRequested)
                    throw new BrokerOperationException(BrokerErrorCodes.UnityDisconnected,
                        $"Unity disconnected before '{method}' was sent.");
                throw new BrokerOperationException(BrokerErrorCodes.RequestTimedOut,
                    $"Unity request '{method}' timed out before it was sent.");
            }
            catch (WebSocketException ex)
            {
                if (sendAttempted)
                {
                    _socket.Abort();
                    _lifetime.Cancel();
                }
                throw new BrokerOperationException(sendAttempted ? BrokerErrorCodes.ExecutionOutcomeUnknown : BrokerErrorCodes.UnityDisconnected,
                    sendAttempted ? $"Unity connection was lost after sending '{method}' began: {ex.Message}" : ex.Message,
                    sendAttempted);
            }
            finally
            {
                if (!lateCompletionOwnsPending) _pending.TryRemove(id, out _);
            }
        }
        finally
        {
            if (gateAcquired && !lateCompletionOwnsGate) _executionGate.Release();
        }
    }

    private async Task ObserveLateCompletionAsync(string id, TaskCompletionSource<ProtocolEnvelope> completion)
    {
        try { await completion.Task.WaitAsync(_lifetime.Token); }
        catch (Exception) { }
        finally
        {
            _pending.TryRemove(id, out _);
            _executionGate.Release();
        }
    }

    public async Task ReleaseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var request = new UnityCommandRequest(sessionId, Guid.NewGuid().ToString("N"), null, null, 30, false);
        await RequestAsync("session/release", request, TimeSpan.FromSeconds(35), cancellationToken);
    }

    public async Task SendTokensAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        if (!AuthorizationRequired || IsAuthorized || !IsConnected) return;
        var payload = JsonSerializer.SerializeToElement(new AuthTokensPayload(tokens),
            BrokerJsonContext.Default.AuthTokensPayload);
        await WebSocketJson.SendAsync(_socket,
            WebSocketJson.CreateEnvelope("event", "auth/tokens", null, payload), _sendGate, cancellationToken);
    }

    public UnityInstanceSnapshot ToSnapshot()
    {
        var status = Status;
        if (IsConnected && DateTimeOffset.UtcNow - status.MainThreadTickAtUtc > BrokerConstants.MainThreadStallAfter)
        {
            var reportedPhase = status.Phase;
            status = status with
            {
                Phase = "MainThreadStalled",
                CanEval = false,
                BusyReason = $"Unity transport is connected but the main thread heartbeat is stale. " +
                             $"The last Unity-reported phase was {reportedPhase}."
            };
        }

        var registration = Registration;
        return new UnityInstanceSnapshot(registration.InstanceId, registration.ConnectionEpoch,
            registration.ProcessId, registration.ProcessStartedAtUtc, registration.ProjectName,
            registration.ProjectPath, registration.UnityVersion, registration.PackageVersion,
            registration.Environment, IsConnected, ConnectedAtUtc, LastTransportHeartbeatAtUtc, status)
        {
            AuthorizationRequired = AuthorizationRequired,
            AuthorizationState = AuthorizationState
        };
    }

    private static string NormalizeAuthorizationState(string? state, string fallback)
    {
        if (string.IsNullOrWhiteSpace(state)) return fallback;
        if (string.Equals(state, "NotRequired", StringComparison.Ordinal) ||
            string.Equals(state, "Pending", StringComparison.Ordinal) ||
            string.Equals(state, "Authorized", StringComparison.Ordinal))
            return state;
        throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
            $"Unity reported unsupported authorization state '{state}'.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        foreach (var pending in _pending.Values)
            pending.TrySetException(new BrokerOperationException(BrokerErrorCodes.ExecutionOutcomeUnknown,
                "Unity disconnected while the request was pending.", true));
        _pending.Clear();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await WebSocketJson.CloseOutputAndAbortAsync(_socket, WebSocketCloseStatus.NormalClosure,
                    "Broker connection closed");
        }
        catch (ObjectDisposedException) { }
        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
