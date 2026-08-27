using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace YuzeToolkit.Eval.Broker;

internal static class WebSocketJson
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CloseOutputTimeout = TimeSpan.FromSeconds(1);

    public static async Task<JsonDocument?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, "Only UTF-8 text WebSocket messages are supported.");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxMessageBytes)
                throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, "WebSocket message exceeds the 4 MiB limit.");
            if (result.EndOfMessage) break;
        }

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public static async Task SendAsync(WebSocket socket, string json, SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public static async Task CloseOutputAndAbortAsync(WebSocket socket, WebSocketCloseStatus closeStatus,
        string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            socket.Abort();
            return;
        }

        using var timeout = new CancellationTokenSource(CloseOutputTimeout);
        try
        {
            // Sending the close frame is courteous, but waiting for an unresponsive peer's
            // close handshake must never keep a Broker request or shutdown path alive.
            await socket.CloseOutputAsync(closeStatus, reason, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // The bounded close interval elapsed.
        }
        catch (WebSocketException)
        {
            // The peer disconnected while the close frame was being sent.
        }
        finally
        {
            socket.Abort();
        }
    }

    public static string CreateEnvelope(string type, string method, string? id, JsonElement payload,
        ProtocolError? error = null)
    {
        var envelope = new ProtocolEnvelope(BrokerConstants.ProtocolVersion, type, id, method, payload, error);
        return JsonSerializer.Serialize(envelope, BrokerJsonContext.Default.ProtocolEnvelope);
    }

    public static ProtocolEnvelope ParseEnvelope(JsonDocument document)
    {
        var envelope = document.RootElement.Deserialize(BrokerJsonContext.Default.ProtocolEnvelope);
        if (envelope == null)
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, "Protocol envelope is empty.");
        if (!string.Equals(envelope.Protocol, BrokerConstants.ProtocolVersion, StringComparison.Ordinal))
            throw new BrokerOperationException(BrokerErrorCodes.ProtocolMismatch,
                $"Protocol '{envelope.Protocol}' is unsupported; expected '{BrokerConstants.ProtocolVersion}'.");
        if (string.IsNullOrWhiteSpace(envelope.Type) || string.IsNullOrWhiteSpace(envelope.Method))
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, "Protocol envelope type and method are required.");
        return envelope;
    }

    public static JsonElement ToElement<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToElement(value, typeInfo);

    public static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();
}
