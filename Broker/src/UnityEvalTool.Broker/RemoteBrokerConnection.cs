#nullable enable
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace YuzeToolkit.Eval.Broker;

// One MCP client session to a peer Broker's network export. Unity federation (/mcp) and remote
// workspaces (/remote/mcp) share its transport, export validation, failure classification and backoff.
internal sealed class RemoteBrokerConnection : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClientTransport _transport;

    private RemoteBrokerConnection(HttpClient http, HttpClientTransport transport, McpClient client)
    {
        _http = http;
        _transport = transport;
        Client = client;
    }

    public McpClient Client { get; }

    public static async Task<RemoteBrokerConnection> ConnectAsync(BrokerConfiguration config, RemoteBrokerOptions remote,
        string path, TimeSpan httpTimeout, CancellationToken cancellationToken)
    {
        var connectTimeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds);
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = connectTimeout })
            { Timeout = httpTimeout };
        var credential = config.GetCredential(remote.CredentialRef);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new UriBuilder(remote.Scheme, remote.Host, remote.Port, path).Uri,
            Name = remote.Id,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 0,
            ConnectionTimeout = connectTimeout,
            AdditionalHeaders = credential == null ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + credential }
        }, http, ownsHttpClient: false);
        try
        {
            var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ProtocolVersion = "2025-11-25",
                InitializationTimeout = connectTimeout,
                ClientInfo = new Implementation { Name = "YuzeEvalTool.Broker", Version = BrokerConstants.PackageVersion }
            }, cancellationToken: cancellationToken);
            return new RemoteBrokerConnection(http, transport, client);
        }
        catch
        {
            await transport.DisposeAsync();
            http.Dispose();
            throw;
        }
    }

    public Task<CallToolResult> CallAsync(string tool, JsonObject arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        Client.CallToolAsync(new CallToolRequestParams
        {
            Name = tool,
            Arguments = arguments.ToDictionary(pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value, BrokerJsonContext.Default.JsonNode))
        }, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _transport.DisposeAsync();
        _http.Dispose();
    }

    public static bool IsFailure(Exception error) => error is HttpRequestException or IOException or TimeoutException or
        OperationCanceledException or McpException or JsonException or InvalidDataException or ObjectDisposedException;

    // A JSON-RPC error or an HTTP rejection proves the peer answered without executing the call.
    public static bool ProvesNotExecuted(Exception error) => error is McpProtocolException or HttpRequestException
        { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound };

    public static string FailureCode(Exception error) => error switch
    {
        BrokerOperationException operation => operation.Code,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => BrokerErrorCodes.RemoteAuthenticationFailed,
        JsonException or InvalidDataException or McpProtocolException => BrokerErrorCodes.RemoteIncompatible,
        _ => BrokerErrorCodes.RemoteUnavailable
    };

    public static void RequireLocalExport(string? view, string brokerId, string brokerInstanceId)
    {
        if (view != "local" || !Guid.TryParseExact(brokerId, "N", out _) || !Guid.TryParseExact(brokerInstanceId, "N", out _))
            throw new BrokerOperationException(BrokerErrorCodes.RemoteIncompatible,
                "Configure a compatible Broker network endpoint that exports local projects only.");
    }

    public static TimeSpan RetryDelay(BrokerConfiguration config, int failures) => TimeSpan.FromSeconds(Math.Min(
        config.ReconnectMaxDelaySeconds, config.DiscoveryIntervalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 16))));
}
