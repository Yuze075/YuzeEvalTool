using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

internal sealed class BrokerCliConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public async Task ConnectAsync(string? tokenList, CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(new Uri($"ws://{BrokerConstants.Host}:{BrokerConstants.Port}/cli"), cancellationToken);
        await RequestAsync("cli/hello", new JsonObject { ["token"] = tokenList }, cancellationToken);
    }

    public Task<JsonElement> ListAsync(CancellationToken cancellationToken) =>
        RequestAsync("unity/list", new JsonObject(), cancellationToken);

    public Task<JsonElement> ConnectUnityAsync(string instanceId, long registryRevision,
        CancellationToken cancellationToken) =>
        RequestAsync("unity/connect", new JsonObject
        {
            ["instanceId"] = instanceId,
            ["registryRevision"] = registryRevision
        }, cancellationToken);

    public Task<JsonElement> StatusAsync(string waitFor, int timeoutSeconds, CancellationToken cancellationToken) =>
        StatusAsync(waitFor, timeoutSeconds, null, cancellationToken);

    public Task<JsonElement> StatusAsync(string waitFor, int timeoutSeconds, string? compilationCycleId,
        CancellationToken cancellationToken) =>
        StatusAsync(waitFor, timeoutSeconds, compilationCycleId, null, cancellationToken);

    public Task<JsonElement> StatusAsync(string waitFor, int timeoutSeconds, string? compilationCycleId,
        DateTimeOffset? observedAfterUtc, CancellationToken cancellationToken) =>
        RequestAsync("unity/status", new JsonObject
        {
            ["waitFor"] = waitFor,
            ["timeoutSeconds"] = timeoutSeconds,
            ["compilationCycleId"] = compilationCycleId,
            ["observedAfterUtc"] = observedAfterUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken);

    public Task<JsonElement> ExecuteAsync(string line, CancellationToken cancellationToken) =>
        RequestAsync("cli/execute", new JsonObject { ["line"] = line }, cancellationToken);

    private async Task<JsonElement> RequestAsync(string method, JsonObject payload,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var envelope = new JsonObject
            {
                ["protocol"] = BrokerConstants.ProtocolVersion,
                ["type"] = "request",
                ["id"] = id,
                ["method"] = method,
                ["payload"] = payload,
                ["error"] = null
            };
            var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            using var response = await WebSocketJson.ReceiveAsync(_socket, cancellationToken)
                                 ?? throw new BrokerOperationException(BrokerErrorCodes.BrokerUnavailable,
                                     "Broker closed the CLI connection.");
            var parsed = WebSocketJson.ParseEnvelope(response);
            if (!string.Equals(parsed.Id, id, StringComparison.Ordinal))
                throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                    "Broker CLI response id did not match the request.");
            if (parsed.Error != null)
                throw new BrokerOperationException(parsed.Error.Code, parsed.Error.Message, parsed.Error.MayHaveExecuted);
            return parsed.Payload.Clone();
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await WebSocketJson.CloseOutputAndAbortAsync(_socket, WebSocketCloseStatus.NormalClosure,
                    "CLI closed");
        }
        catch (ObjectDisposedException) { }
        _socket.Dispose();
        _requestGate.Dispose();
    }
}
