using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class UnityConnectionConcurrencyTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 13, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QueuedTimeoutAndCancellationDoNotSend()
    {
        using var socket = new ScriptedWebSocket();
        await using var connection = CreateConnection(socket);
        using var runCancellation = new CancellationTokenSource();
        var receiveLoop = connection.RunAsync(runCancellation.Token);
        try
        {
            var first = connection.RequestAsync("eval/execute", Request("first"), TimeSpan.FromSeconds(5),
                CancellationToken.None);
            var firstEnvelope = await socket.ReadSentAsync();

            var timedOut = await Assert.ThrowsAsync<BrokerOperationException>(() =>
                connection.RequestAsync("eval/execute", Request("second"), TimeSpan.FromMilliseconds(100),
                    CancellationToken.None));
            Assert.Equal(BrokerErrorCodes.RequestTimedOut, timedOut.Code);
            Assert.False(timedOut.MayHaveExecuted);

            using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                connection.RequestAsync("eval/execute", Request("third"), TimeSpan.FromSeconds(5),
                    callerCancellation.Token));
            Assert.Equal(1, socket.SentCount);

            socket.Respond(firstEnvelope);
            await first;
        }
        finally
        {
            runCancellation.Cancel();
            await IgnoreCancellation(receiveLoop);
        }
    }

    [Fact]
    public async Task SentTimeoutKeepsFollowingRequestsQueuedUntilLateResponse()
    {
        using var socket = new ScriptedWebSocket();
        await using var connection = CreateConnection(socket);
        using var runCancellation = new CancellationTokenSource();
        var receiveLoop = connection.RunAsync(runCancellation.Token);
        try
        {
            var first = connection.RequestAsync("eval/execute", Request("first"), TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            var firstEnvelope = await socket.ReadSentAsync();
            var unknown = await Assert.ThrowsAsync<BrokerOperationException>(() => first);
            Assert.Equal(BrokerErrorCodes.ExecutionOutcomeUnknown, unknown.Code);
            Assert.True(unknown.MayHaveExecuted);

            var second = await Assert.ThrowsAsync<BrokerOperationException>(() =>
                connection.RequestAsync("eval/execute", Request("second"), TimeSpan.FromMilliseconds(100),
                    CancellationToken.None));
            Assert.Equal(BrokerErrorCodes.RequestTimedOut, second.Code);
            Assert.False(second.MayHaveExecuted);
            Assert.Equal(1, socket.SentCount);

            socket.Respond(firstEnvelope);
            var third = connection.RequestAsync("eval/execute", Request("third"), TimeSpan.FromSeconds(2),
                CancellationToken.None);
            var thirdEnvelope = await socket.ReadSentAsync();
            Assert.Equal(2, socket.SentCount);
            socket.Respond(thirdEnvelope);
            await third;
        }
        finally
        {
            runCancellation.Cancel();
            await IgnoreCancellation(receiveLoop);
        }
    }

    [Fact]
    public async Task CancellationDuringSendReportsUnknownOutcome()
    {
        using var socket = new ScriptedWebSocket { BlockSend = true };
        await using var connection = CreateConnection(socket);
        using var callerCancellation = new CancellationTokenSource();
        var request = connection.RequestAsync("eval/execute", Request("send-race"), TimeSpan.FromSeconds(5),
            callerCancellation.Token);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();

        var unknown = await Assert.ThrowsAsync<BrokerOperationException>(() => request);
        Assert.Equal(BrokerErrorCodes.ExecutionOutcomeUnknown, unknown.Code);
        Assert.True(unknown.MayHaveExecuted);
        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    [Fact]
    public async Task WebSocketFailureAfterSendBeginsMakesConnectionUnusable()
    {
        using var socket = new ScriptedWebSocket { FailSendWithWebSocketException = true };
        await using var connection = CreateConnection(socket);

        var error = await Assert.ThrowsAsync<BrokerOperationException>(() =>
            connection.RequestAsync("eval/execute", Request("send-failure"), TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.Equal(BrokerErrorCodes.ExecutionOutcomeUnknown, error.Code);
        Assert.True(error.MayHaveExecuted);
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task CloseOutputIsBoundedForUnresponsivePeer()
    {
        using var socket = new ScriptedWebSocket { BlockCloseOutput = true };

        await WebSocketJson.CloseOutputAndAbortAsync(socket, WebSocketCloseStatus.NormalClosure, "test")
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    private static UnityConnection CreateConnection(WebSocket socket)
    {
        var status = new UnityStatus("Ready", true, string.Empty, 1, StartedAt, false, false, false,
            string.Empty, 0, 0, null, null, 1);
        var registration = new UnityRegistration("token", "instance", 1, 42, StartedAt, "Project", "/Project",
            "2022.3", "2.0.1", "Editor", status);
        return new UnityConnection(socket, registration, (_, _) => { }, _ => { });
    }

    private static UnityCommandRequest Request(string requestId) =>
        new("session", requestId, "async function execute() { return 1; }", null, 30, false);

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private int _sentCount;
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public int SentCount => Volatile.Read(ref _sentCount);
        public bool BlockSend { get; set; }
        public bool BlockCloseOutput { get; set; }
        public bool FailSendWithWebSocketException { get; set; }
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public async Task<string> ReadSentAsync() =>
            await _sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        public void Respond(string requestJson)
        {
            using var request = JsonDocument.Parse(requestJson);
            var root = request.RootElement;
            var response = WebSocketJson.CreateEnvelope("response", root.GetProperty("method").GetString()!,
                root.GetProperty("id").GetString(), WebSocketJson.EmptyObject());
            _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(response));
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryComplete();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            if (BlockCloseOutput)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await CloseAsync(closeStatus, statusDescription, cancellationToken);
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var message = await _incoming.Reader.ReadAsync(cancellationToken);
            message.AsSpan().CopyTo(buffer.Array!.AsSpan(buffer.Offset, buffer.Count));
            return new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            if (BlockSend) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (FailSendWithWebSocketException)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            cancellationToken.ThrowIfCancellationRequested();
            var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            Interlocked.Increment(ref _sentCount);
            _sent.Writer.TryWrite(text);
        }
    }
}
