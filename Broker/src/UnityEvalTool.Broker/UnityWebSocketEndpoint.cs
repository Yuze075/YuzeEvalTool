using System.Net.WebSockets;
using System.Text.Json;

namespace YuzeToolkit.Eval.Broker;

internal static class UnityWebSocketEndpoint
{
    public static async Task HandleAsync(HttpContext context, BrokerRegistry registry, AuthTokenStore tokens)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        if (!WebSocketAuthenticationGate.TryEnter(out var authenticationLease))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        using var authenticationSlot = authenticationLease!;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        UnityConnection? connection = null;
        var sendGate = new SemaphoreSlim(1, 1);
        try
        {
            using var firstDocument = await WebSocketAuthenticationGate.ReceiveFirstMessageAsync(socket,
                context.RequestAborted);
            if (firstDocument == null) return;
            var envelope = WebSocketJson.ParseEnvelope(firstDocument);
            if (!string.Equals(envelope.Type, "request", StringComparison.Ordinal) ||
                !string.Equals(envelope.Method, "unity/register", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(envelope.Id))
                throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                    "The first Unity message must be a unity/register request with an id.");
            var registration = envelope.Payload.Deserialize(BrokerJsonContext.Default.UnityRegistration)
                               ?? throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                                   "Unity registration payload is empty.");
            var storedTokens = tokens.GetTokens();
            authenticationSlot.Dispose();
            connection = registry.Register(socket, registration);
            var responsePayload = JsonSerializer.SerializeToElement(
                new UnityRegistrationResponse(registration.InstanceId, BrokerConstants.ProtocolVersion,
                    BrokerHost.InstanceId, storedTokens), BrokerJsonContext.Default.UnityRegistrationResponse);
            await WebSocketJson.SendAsync(socket,
                WebSocketJson.CreateEnvelope("response", envelope.Method, envelope.Id, responsePayload), sendGate,
                context.RequestAborted);
            await connection.RunAsync(context.RequestAborted);
        }
        catch (BrokerOperationException ex)
        {
            if (socket.State == WebSocketState.Open)
            {
                var error = new ProtocolError(ex.Code, ex.Message, ex.MayHaveExecuted);
                await WebSocketAuthenticationGate.SendErrorAndRejectAsync(socket, "unity/register", null, error,
                    sendGate);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Host or peer shutdown.
        }
        catch (WebSocketException)
        {
            // Peer disconnected.
        }
        finally
        {
            sendGate.Dispose();
            if (connection != null)
            {
                registry.MarkDisconnected(connection);
                await connection.DisposeAsync();
            }
        }
    }
}
