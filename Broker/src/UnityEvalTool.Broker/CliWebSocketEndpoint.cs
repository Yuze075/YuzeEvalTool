using System.Net.WebSockets;
using System.Text.Json;

namespace YuzeToolkit.Eval.Broker;

internal static class CliWebSocketEndpoint
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
        using var sendGate = new SemaphoreSlim(1, 1);
        var consoleId = Guid.NewGuid().ToString("N");
        string? selectedHandle = null;
        var initialized = false;
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var document = initialized
                    ? await WebSocketJson.ReceiveAsync(socket, context.RequestAborted)
                    : await WebSocketAuthenticationGate.ReceiveFirstMessageAsync(socket, context.RequestAborted);
                if (document == null) break;
                var envelope = WebSocketJson.ParseEnvelope(document);
                if (!string.Equals(envelope.Type, "request", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(envelope.Id))
                    throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                        "CLI messages must be requests with an id.");
                try
                {
                    JsonElement result;
                    if (!initialized)
                    {
                        if (!string.Equals(envelope.Method, "cli/hello", StringComparison.Ordinal))
                            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                                "The first CLI request must be cli/hello.");
                        var tokenList = envelope.Payload.TryGetProperty("token", out var tokenElement)
                            ? tokenElement.GetString()
                            : envelope.Payload.TryGetProperty("authToken", out var legacyTokenElement)
                                ? legacyTokenElement.GetString()
                                : null;
                        try
                        {
                            if (!string.IsNullOrEmpty(tokenList))
                            {
                                tokens.AddTokenList(tokenList);
                                await registry.BroadcastTokensAsync(tokens.GetTokens(), context.RequestAborted);
                            }
                        }
                        catch (InvalidDataException ex)
                        {
                            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, ex.Message);
                        }
                        initialized = true;
                        authenticationSlot.Dispose();
                        result = JsonDocument.Parse($"{{\"consoleId\":\"{consoleId}\",\"protocolVersion\":\"{BrokerConstants.ProtocolVersion}\"}}")
                            .RootElement.Clone();
                    }
                    else
                    {
                        result = envelope.Method switch
                        {
                            "unity/list" => Serialize(registry.GetSnapshot(selectedHandle)),
                            "unity/connect" => Connect(registry, envelope.Payload, consoleId,
                                selectedHandle, out selectedHandle),
                            "unity/status" => await StatusAsync(registry, selectedHandle, envelope.Payload,
                                context.RequestAborted),
                            "cli/execute" => await ExecuteAsync(registry, selectedHandle, consoleId,
                                envelope.Payload, context.RequestAborted),
                            _ => throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                                $"Unknown CLI Broker method '{envelope.Method}'.")
                        };
                    }

                    await WebSocketJson.SendAsync(socket,
                        WebSocketJson.CreateEnvelope("response", envelope.Method, envelope.Id, result), sendGate,
                        context.RequestAborted);
                }
                catch (BrokerOperationException ex)
                {
                    var error = new ProtocolError(ex.Code, ex.Message, ex.MayHaveExecuted);
                    if (!initialized)
                    {
                        await WebSocketAuthenticationGate.SendErrorAndRejectAsync(socket, envelope.Method,
                            envelope.Id, error, sendGate);
                        break;
                    }

                    await WebSocketJson.SendAsync(socket,
                        WebSocketJson.CreateEnvelope("response", envelope.Method, envelope.Id,
                            WebSocketJson.EmptyObject(), error), sendGate, context.RequestAborted);
                }
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
        catch (BrokerOperationException ex)
        {
            if (socket.State == WebSocketState.Open)
            {
                var error = new ProtocolError(ex.Code, ex.Message, ex.MayHaveExecuted);
                await WebSocketAuthenticationGate.SendErrorAndRejectAsync(socket, "cli/hello", null, error,
                    sendGate);
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(selectedHandle))
                registry.ReleaseLease(selectedHandle);
        }
    }

    private static JsonElement Connect(BrokerRegistry registry, JsonElement payload, string consoleId,
        string? previousHandle, out string handle)
    {
        var instanceId = payload.TryGetProperty("instanceId", out var instanceElement)
            ? instanceElement.GetString() ?? string.Empty
            : string.Empty;
        var revision = payload.TryGetProperty("registryRevision", out var revisionElement)
            ? revisionElement.GetInt64()
            : 0;
        var result = registry.Connect(instanceId, revision, "cli:" + consoleId);
        handle = result.ConnectionHandle;
        if (!string.IsNullOrWhiteSpace(previousHandle))
            registry.ReleaseSupersededLease(previousHandle, handle);
        return JsonSerializer.SerializeToElement(result, BrokerJsonContext.Default.ConnectionLeaseResult);
    }

    private static async Task<JsonElement> StatusAsync(BrokerRegistry registry, string? handle, JsonElement payload,
        CancellationToken cancellationToken)
    {
        var waitFor = payload.TryGetProperty("waitFor", out var waitElement)
            ? waitElement.GetString() ?? "snapshot"
            : "snapshot";
        var selectedCompilationCycleId = ResolveCompilationCycleId(payload);
        var observedAfterUtc = ResolveObservedAfterUtc(payload);
        var timeout = payload.TryGetProperty("timeoutSeconds", out var timeoutElement)
            ? TimeSpan.FromSeconds(Math.Clamp(timeoutElement.GetInt32(), 0, 3600))
            : TimeSpan.Zero;
        var snapshot = await registry.WaitAsync(handle, null, waitFor, selectedCompilationCycleId, observedAfterUtc, timeout,
            cancellationToken);
        return Serialize(snapshot);
    }

    internal static string? ResolveCompilationCycleId(JsonElement payload)
    {
        var compilationCycleId = ReadOptionalString(payload, "compilationCycleId");
        var requestId = ReadOptionalString(payload, "requestId");
        if (!string.IsNullOrWhiteSpace(compilationCycleId) && !string.IsNullOrWhiteSpace(requestId) &&
            !string.Equals(compilationCycleId, requestId, StringComparison.Ordinal))
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                "compilationCycleId and its deprecated requestId alias must match when both are provided.");
        return string.IsNullOrWhiteSpace(compilationCycleId) ? requestId : compilationCycleId;
    }

    internal static DateTimeOffset? ResolveObservedAfterUtc(JsonElement payload)
    {
        if (!payload.TryGetProperty("observedAfterUtc", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
            return null;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var parsed))
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                "observedAfterUtc must be an ISO-8601 timestamp returned as capturedAtUtc by unity_status.");
        return parsed;
    }

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                $"{propertyName} must be a string when provided.");
        return value.GetString();
    }

    private static async Task<JsonElement> ExecuteAsync(BrokerRegistry registry, string? handle, string consoleId,
        JsonElement payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(handle))
            throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleRequired,
                "Select a Unity instance before executing CLI commands.");
        var line = payload.TryGetProperty("line", out var lineElement)
            ? lineElement.GetString() ?? string.Empty
            : string.Empty;
        return await registry.ExecuteCliAsync(handle, consoleId, line, cancellationToken);
    }

    private static JsonElement Serialize(RegistrySnapshot value) =>
        JsonSerializer.SerializeToElement(value, BrokerJsonContext.Default.RegistrySnapshot);
}
