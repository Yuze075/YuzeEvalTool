using System.Net.WebSockets;

namespace YuzeToolkit.Eval.Broker;

internal static class WebSocketAuthenticationGate
{
    private static readonly TimeSpan ErrorSendTimeout = TimeSpan.FromSeconds(1);
    private static readonly SemaphoreSlim Pending = new(BrokerConstants.MaxPendingAuthenticationConnections,
        BrokerConstants.MaxPendingAuthenticationConnections);

    public static bool TryEnter(out IDisposable? lease)
    {
        if (!Pending.Wait(0))
        {
            lease = null;
            return false;
        }

        lease = new GateLease();
        return true;
    }

    public static async Task<System.Text.Json.JsonDocument?> ReceiveFirstMessageAsync(WebSocket socket,
        CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(BrokerConstants.AuthenticationTimeout);
        try
        {
            return await WebSocketJson.ReceiveAsync(socket, timeout.Token);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            throw new BrokerOperationException(BrokerErrorCodes.AuthenticationFailed,
                $"The first handshake message was not received within {BrokerConstants.AuthenticationTimeout.TotalSeconds:0}s.");
        }
    }

    public static async Task RejectAsync(WebSocket socket, string reason)
    {
        await WebSocketJson.CloseOutputAndAbortAsync(socket, WebSocketCloseStatus.PolicyViolation, reason);
    }

    public static async Task SendErrorAndRejectAsync(WebSocket socket, string method, string? id,
        ProtocolError error, SemaphoreSlim sendGate)
    {
        using (var timeout = new CancellationTokenSource(ErrorSendTimeout))
        {
            try
            {
                await WebSocketJson.SendAsync(socket,
                    WebSocketJson.CreateEnvelope("response", method, id, WebSocketJson.EmptyObject(), error),
                    sendGate, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Cleanup remains bounded even when the pre-handshake peer stops reading.
            }
            catch (WebSocketException)
            {
                // The peer disconnected before the policy response completed.
            }
        }

        await RejectAsync(socket, error.Code);
    }

    private sealed class GateLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Pending.Release();
        }
    }
}
