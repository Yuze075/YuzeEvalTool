#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    public sealed class UnityBrokerClient : IDisposable
    {
        public const string EditorEnabledPreferenceKey = nameof(YuzeToolkit) + ".Broker.Enabled";

        private static readonly Lazy<UnityBrokerClient> LazyShared = new(() => new UnityBrokerClient());
        private readonly object _syncRoot = new();
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private BrokerEvalSessionRouter _sessions = new();
        private CancellationTokenSource? _lifetime;
        private ClientWebSocket? _socket;
        private Task? _runTask;
        private BrokerClientIdentity _identity = new();
        private Func<bool>? _ensureBrokerRunning;
        private BrokerUnityStatusSnapshot _latestStatus = new();
        private string _brokerInstanceId = string.Empty;
        private long _mainThreadTick;
        private long _runGeneration;
        private bool _configured;
        private bool _isConnected;
        private bool _authorizationRequired;
        private string _authorizationState = "NotRequired";
        private UnityEvalToolAuthorizationSettings.AuthorizationVerifier _authorizationVerifier =
            UnityEvalToolAuthorizationSettings.AuthorizationVerifier.Disabled;

        private UnityBrokerClient()
        {
        }

        public static UnityBrokerClient Shared => LazyShared.Value;

        public bool IsConnected
        {
            get { lock (_syncRoot) return _isConnected; }
        }

        public bool IsRunning
        {
            get { lock (_syncRoot) return _runTask != null; }
        }

        public string AuthorizationState
        {
            get { lock (_syncRoot) return _authorizationState; }
        }

        public BrokerClientIdentity Identity
        {
            get { lock (_syncRoot) return _identity; }
        }

        public BrokerUnityStatusSnapshot LatestStatus
        {
            get { lock (_syncRoot) return _latestStatus.Clone(); }
        }

        public IReadOnlyList<EvalSessionSnapshot> GetSessionSnapshots(string sessionPrefix)
        {
            if (sessionPrefix == null) throw new ArgumentNullException(nameof(sessionPrefix));
            BrokerEvalSessionRouter sessions;
            lock (_syncRoot) sessions = _sessions;
            return sessions.GetSnapshots(sessionPrefix);
        }

        public void Configure(BrokerClientIdentity identity, Func<bool>? ensureBrokerRunning = null)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            lock (_syncRoot)
            {
                if (_runTask != null) throw new InvalidOperationException("Configure the Broker client before Start.");
                _identity = identity;
                _ensureBrokerRunning = ensureBrokerRunning;
                _latestStatus.VmGeneration = identity.VmGeneration;
                _configured = true;
            }
        }

        public void Start()
        {
            var settings = UnityEvalToolAuthorizationSettings.Load();
            var verifier = settings == null
                ? UnityEvalToolAuthorizationSettings.AuthorizationVerifier.Disabled
                : settings.CreateVerifier();
            lock (_syncRoot)
            {
                if (_runTask != null) return;
                if (!_configured) _latestStatus.VmGeneration = _identity.VmGeneration;
                var generation = ++_runGeneration;
                var sessions = _sessions;
                _authorizationVerifier = verifier;
                var lifetime = new CancellationTokenSource();
                _lifetime = lifetime;
                _runTask = Task.Run(() => RunReconnectLoopAsync(generation, sessions, lifetime.Token));
            }
        }

        public void Tick(BrokerUnityStatusSnapshot? status = null)
        {
            lock (_syncRoot)
            {
                _mainThreadTick++;
                _latestStatus = status ?? BrokerUnityStatusSnapshot.CreateRuntime(_mainThreadTick, _identity.VmGeneration);
                _latestStatus.MainThreadTick = _mainThreadTick;
                _latestStatus.MainThreadTickAtUtc = DateTime.UtcNow;
                _latestStatus.VmGeneration = _identity.VmGeneration;
            }
        }

        public void PublishReloadingAndStop()
        {
            Task? publish = null;
            lock (_syncRoot)
            {
                _latestStatus.Phase = "Reloading";
                _latestStatus.CanEval = false;
                _latestStatus.BusyReason = "Unity is reloading assemblies.";
                if (_socket is { State: WebSocketState.Open })
                    publish = SendStatusAsync(_socket, CancellationToken.None);
            }

            try { publish?.Wait(TimeSpan.FromMilliseconds(300)); }
            catch (AggregateException) { }
            Stop();
        }

        public void PublishExitingAndStop()
        {
            Task? publish = null;
            lock (_syncRoot)
            {
                _latestStatus.Phase = "Exiting";
                _latestStatus.CanEval = false;
                _latestStatus.BusyReason = "Unity is exiting.";
                if (_socket is { State: WebSocketState.Open })
                    publish = SendStatusAsync(_socket, CancellationToken.None);
            }

            try { publish?.Wait(TimeSpan.FromMilliseconds(300)); }
            catch (AggregateException) { }
            Stop();
        }

        public void Stop()
        {
            CancellationTokenSource? lifetime;
            ClientWebSocket? socket;
            Task runTask;
            BrokerEvalSessionRouter sessions;
            lock (_syncRoot)
            {
                if (_runTask == null) return;
                _runGeneration++;
                lifetime = _lifetime;
                socket = _socket;
                runTask = _runTask;
                sessions = _sessions;
                _lifetime = null;
                _socket = null;
                _runTask = null;
                _isConnected = false;
                _authorizationState = _authorizationRequired ? "Pending" : "NotRequired";
                _sessions = new BrokerEvalSessionRouter();
            }

            lifetime?.Cancel();
            try { socket?.Abort(); }
            catch (ObjectDisposedException) { }
            _ = DisposeStoppedGenerationAsync(runTask, lifetime, sessions);
        }

        public void Reconnect()
        {
            Stop();
            Start();
        }

        public void Dispose()
        {
            Stop();
            _sessions.Dispose();
        }

        private async Task RunReconnectLoopAsync(long generation, BrokerEvalSessionRouter sessions,
            CancellationToken cancellationToken)
        {
            var attempt = 0;
            var brokerStartAttempted = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndRunAsync(generation, sessions, () =>
                    {
                        attempt = 0;
                        brokerStartAttempted = false;
                    }, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lock (_syncRoot)
                    {
                        if (generation == _runGeneration) _isConnected = false;
                    }
                    if (!brokerStartAttempted && _ensureBrokerRunning != null)
                    {
                        brokerStartAttempted = true;
                        try
                        {
                            if (_ensureBrokerRunning())
                            {
                                await Task.Delay(350, cancellationToken);
                                continue;
                            }
                        }
                        catch (Exception startException)
                        {
                            LogSys.LogWarning($"[Yuze Eval Tool] Failed to start the local Broker: {startException.Message}");
                        }
                    }

                    if (IsCurrentGeneration(generation) && (attempt == 0 || attempt % 10 == 0))
                        LogSys.LogWarning($"[Yuze Eval Tool] Broker connection is unavailable: {ex.Message}");
                }

                attempt++;
                var delay = Math.Min(10_000, 250 * (1 << Math.Min(attempt, 5)));
                await Task.Delay(delay, cancellationToken);
            }
        }

        private async Task ConnectAndRunAsync(long generation, BrokerEvalSessionRouter sessions,
            Action onConnected, CancellationToken cancellationToken)
        {
            UnityEvalToolAuthorizationSettings.AuthorizationVerifier authorizationVerifier;
            lock (_syncRoot) authorizationVerifier = _authorizationVerifier;
            var authorizationRequired = authorizationVerifier.RequireToken;
            lock (_syncRoot)
            {
                _authorizationRequired = authorizationRequired;
                _authorizationState = authorizationRequired ? "Pending" : "NotRequired";
            }
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(BrokerProtocolUtility.Endpoint), cancellationToken);
            lock (_syncRoot)
            {
                if (generation != _runGeneration)
                {
                    socket.Abort();
                    throw new OperationCanceledException(cancellationToken);
                }
                _socket = socket;
            }

            var registerId = Guid.NewGuid().ToString("N");
            var registration = BuildRegistration(authorizationRequired);
            await SendAsync(socket, BrokerProtocolUtility.Request(registerId, "unity/register", registration), cancellationToken);
            var registrationResponse = await ReceiveAsync(socket, cancellationToken);
            if (registrationResponse == null) throw new IOException("Broker closed during Unity registration.");
            var response = BrokerProtocolUtility.ParseEnvelope(registrationResponse);
            var error = EvalData.AsObject(response.TryGetValue("error", out var errorValue) ? errorValue : null);
            if (error != null)
                throw new InvalidOperationException(EvalData.GetString(error, "message") ?? "Broker rejected Unity registration.");
            if (!string.Equals(EvalData.GetString(response, "id"), registerId, StringComparison.Ordinal))
                throw new InvalidOperationException("Broker registration response id did not match the request.");

            var responsePayload = EvalData.AsObject(response.TryGetValue("payload", out var payloadValue)
                ? payloadValue
                : null) ?? EvalData.Obj();
            var brokerInstanceId = EvalData.GetString(responsePayload, "brokerInstanceId") ?? string.Empty;
            var initialTokens = ReadTokens(responsePayload.TryGetValue("tokens", out var tokenValue)
                ? tokenValue
                : null);
            ApplyTokens(authorizationVerifier, initialTokens);
            var resetSessions = false;
            lock (_syncRoot)
            {
                if (generation != _runGeneration)
                    throw new OperationCanceledException(cancellationToken);
                resetSessions = !string.IsNullOrWhiteSpace(brokerInstanceId) &&
                                !string.Equals(_brokerInstanceId, brokerInstanceId, StringComparison.Ordinal);
                _brokerInstanceId = brokerInstanceId;
                _isConnected = true;
            }
            await SendAuthorizationStateAsync(socket, cancellationToken);
            if (resetSessions) sessions.Reset();
            onConnected();
            LogSys.Log($"[Yuze Eval Tool] Connected to local Broker as {_identity.InstanceId} (epoch {_identity.ConnectionEpoch}).");
            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeat = RunHeartbeatAsync(socket, connectionLifetime.Token);
            try
            {
                await ReceiveLoopAsync(socket, sessions, authorizationVerifier, connectionLifetime.Token);
            }
            finally
            {
                connectionLifetime.Cancel();
                try { await heartbeat; }
                catch (OperationCanceledException) { }
                lock (_syncRoot)
                {
                    if (generation == _runGeneration && ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                        _isConnected = false;
                        _authorizationState = _authorizationRequired ? "Pending" : "NotRequired";
                    }
                }
                socket.Dispose();
            }
        }

        private async Task RunHeartbeatAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await SendStatusAsync(socket, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, BrokerEvalSessionRouter sessions,
            UnityEvalToolAuthorizationSettings.AuthorizationVerifier authorizationVerifier,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var json = await ReceiveAsync(socket, cancellationToken);
                if (json == null) return;
                var envelope = BrokerProtocolUtility.ParseEnvelope(json);
                var type = EvalData.GetString(envelope, "type") ?? string.Empty;
                var method = EvalData.GetString(envelope, "method") ?? string.Empty;
                var payload = EvalData.AsObject(envelope.TryGetValue("payload", out var rawPayload) ? rawPayload : null)
                              ?? EvalData.Obj();
                if (string.Equals(type, "event", StringComparison.Ordinal) &&
                    string.Equals(method, "auth/tokens", StringComparison.Ordinal))
                {
                    ApplyTokens(authorizationVerifier,
                        ReadTokens(payload.TryGetValue("tokens", out var tokens) ? tokens : null));
                    await SendAuthorizationStateAsync(socket, cancellationToken);
                    continue;
                }
                if (!string.Equals(type, "request", StringComparison.Ordinal)) continue;
                var id = EvalData.GetString(envelope, "id") ?? string.Empty;
                await HandleRequestAsync(socket, sessions, id, method, payload, cancellationToken);
            }
        }

        private async Task HandleRequestAsync(ClientWebSocket socket, BrokerEvalSessionRouter sessions,
            string id, string method,
            Dictionary<string, object?> payload, CancellationToken cancellationToken)
        {
            bool authorized;
            lock (_syncRoot)
                authorized = !_authorizationRequired ||
                             string.Equals(_authorizationState, "Authorized", StringComparison.Ordinal);
            if (!authorized)
            {
                var authorizationError = EvalData.Obj(
                    ("code", "UnityAuthorizationPending"),
                    ("message", "This Unity connection has not verified a project token."),
                    ("mayHaveExecuted", false));
                await SendAsync(socket,
                    BrokerProtocolUtility.Response(id, method, EvalData.Obj(), authorizationError),
                    cancellationToken);
                return;
            }
            try
            {
                object result;
                switch (method)
                {
                    case "eval/execute":
                        result = await sessions.ExecuteEvalAsync(payload, cancellationToken);
                        break;
                    case "cli/execute":
                        result = await sessions.ExecuteCliAsync(payload, cancellationToken);
                        break;
                    case "session/release":
                        sessions.Release(EvalData.GetString(payload, "sessionId") ?? string.Empty);
                        result = EvalData.Obj(("released", true));
                        break;
                    case "broker/ping":
                        result = EvalData.Obj(("timeUtc", DateTime.UtcNow.ToString("O")));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown Broker request method '{method}'.");
                }

                await SendAsync(socket, BrokerProtocolUtility.Response(id, method, result), cancellationToken);
            }
            catch (Exception ex)
            {
                var error = EvalData.Obj(
                    ("code", "UnityCommandFailed"),
                    ("message", ex.Message),
                    ("mayHaveExecuted", false));
                await SendAsync(socket, BrokerProtocolUtility.Response(id, method, EvalData.Obj(), error), cancellationToken);
            }
        }

        private Dictionary<string, object?> BuildRegistration(bool authorizationRequired)
        {
            var process = Process.GetCurrentProcess();
            var projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            BrokerUnityStatusSnapshot status;
            lock (_syncRoot) status = _latestStatus;
            return EvalData.Obj(
                ("authToken", string.Empty),
                ("instanceId", _identity.InstanceId),
                ("connectionEpoch", _identity.ConnectionEpoch),
                ("processId", process.Id),
                ("processStartedAtUtc", process.StartTime.ToUniversalTime().ToString("O")),
                ("projectName", new DirectoryInfo(projectPath).Name),
                ("projectPath", Path.GetFullPath(projectPath)),
                ("unityVersion", Application.unityVersion),
                ("packageVersion", UnityEvalToolVersion.Current),
                ("environment", Application.isEditor ? "Editor" : "Player"),
                ("authorizationRequired", authorizationRequired),
                ("authorizationState", authorizationRequired ? "Pending" : "NotRequired"),
                ("status", status.ToObject())
            );
        }

        private async Task SendStatusAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            BrokerUnityStatusSnapshot status;
            lock (_syncRoot) status = _latestStatus;
            await SendAsync(socket, BrokerProtocolUtility.Event("unity/status", status.ToObject()), cancellationToken);
        }

        private async Task SendAuthorizationStateAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            string state;
            lock (_syncRoot) state = _authorizationState;
            await SendAsync(socket, BrokerProtocolUtility.Event("unity/authorization",
                EvalData.Obj(("state", state))), cancellationToken);
        }

        private void ApplyTokens(UnityEvalToolAuthorizationSettings.AuthorizationVerifier verifier,
            IReadOnlyList<string> tokens)
        {
            lock (_syncRoot)
            {
                if (!_authorizationRequired)
                {
                    _authorizationState = "NotRequired";
                    return;
                }
                _authorizationState = verifier.VerifyTokens(tokens)
                    ? "Authorized"
                    : "Pending";
            }
        }

        private static IReadOnlyList<string> ReadTokens(object? value)
        {
            var rawTokens = EvalData.AsArray(value);
            if (rawTokens == null) return Array.Empty<string>();
            var tokens = new List<string>(rawTokens.Count);
            foreach (var raw in rawTokens)
            {
                if (raw is not string token)
                    throw new InvalidDataException("Broker auth token payload contains a non-string value.");
                tokens.Add(token);
            }
            return tokens;
        }

        private async Task SendAsync(ClientWebSocket socket, string json, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new IOException("Broker sent a non-text WebSocket message.");
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > 4 * 1024 * 1024) throw new IOException("Broker message exceeds 4 MiB.");
                if (result.EndOfMessage) break;
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private bool IsCurrentGeneration(long generation)
        {
            lock (_syncRoot) return generation == _runGeneration;
        }

        private static async Task DisposeStoppedGenerationAsync(Task runTask, CancellationTokenSource? lifetime,
            BrokerEvalSessionRouter sessions)
        {
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when this connection generation is stopped.
            }
            catch (WebSocketException)
            {
                // Expected when Abort interrupts a pending WebSocket operation.
            }
            finally
            {
                lifetime?.Dispose();
                sessions.Dispose();
            }
        }
    }
}
