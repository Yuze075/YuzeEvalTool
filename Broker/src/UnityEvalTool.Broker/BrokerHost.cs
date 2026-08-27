using ModelContextProtocol.Server;

namespace YuzeToolkit.UnityEvalTool.Broker;

internal static class BrokerHost
{
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
    public static readonly string InstanceId = Guid.NewGuid().ToString("N");

    public static async Task RunAsync(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(BrokerConstants.Port));
        builder.Services.AddSingleton<AuthTokenStore>();
        var registry = new BrokerRegistry();
        builder.Services.AddSingleton(registry);
        builder.Services.AddHostedService<BrokerMaintenanceService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, BrokerJsonContext.Default));
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(UnityBrokerTools.CreateTools(new UnityBrokerTools(registry)));

        var app = builder.Build();
        // Invalid user-edited credential/config files fail before the port starts accepting clients.
        app.Services.GetRequiredService<AuthTokenStore>().GetTokens();
        app.UseHostFiltering();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
        app.Use(async (context, next) =>
        {
            if (!IsLoopbackHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!IsAllowedOrigin(context.Request.Headers.Origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                var tokenStore = context.RequestServices.GetRequiredService<AuthTokenStore>();
                var authorization = context.Request.Headers.Authorization.ToString();
                var tokenList = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authorization["Bearer ".Length..].Trim()
                    : context.Request.Headers["X-UnityEvalTool-Token"].ToString().Trim();
                if (!string.IsNullOrWhiteSpace(authorization) &&
                    !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Yuze Eval Tool accepts MCP credentials only as a Bearer token list.");
                    return;
                }
                if (!string.IsNullOrEmpty(tokenList))
                {
                    try
                    {
                        tokenStore.AddTokenList(tokenList);
                        var registry = context.RequestServices.GetRequiredService<BrokerRegistry>();
                        await registry.BroadcastTokensAsync(tokenStore.GetTokens(), context.RequestAborted);
                    }
                    catch (InvalidDataException ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsync(ex.Message);
                        return;
                    }
                }
            }

            await next(context);
        });

        app.MapGet("/health", (BrokerRegistry registry, AuthTokenStore tokens) =>
            new HealthSnapshot("ready", BrokerConstants.ProtocolVersion,
                $"http://{BrokerConstants.Host}:{BrokerConstants.Port}", StartedAtUtc,
                registry.Revision, registry.GetSnapshot().ConnectedCount, false)
            {
                StoredTokenCount = tokens.GetTokens().Count,
                MaxStoredTokenCount = tokens.MaxStoredTokens
            });
        app.Map("/unity", UnityWebSocketEndpoint.HandleAsync);
        app.Map("/cli", CliWebSocketEndpoint.HandleAsync);
        app.MapMcp("/mcp");
        await app.RunAsync();
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "::1", StringComparison.Ordinal) ||
        string.Equals(host, "[::1]", StringComparison.Ordinal);

    private static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }
}
