#nullable enable
using System.Net;
using ModelContextProtocol.Server;

namespace YuzeToolkit.Eval.Broker;

internal static class BrokerHost
{
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
    public static readonly string InstanceId = Guid.NewGuid().ToString("N");
    internal static readonly object LocalOnlyRequest = new();

    public static async Task RunAsync(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var configuration = BrokerConfiguration.Load(builder.Configuration["config"] ?? BrokerConfiguration.DefaultPath);
        var localPort = builder.Configuration.GetValue<int?>("local-port") ?? BrokerConstants.Port;
        BrokerConfiguration.RequireRange(localPort, 1, 65535, "local-port");
        if (configuration.RemoteAccess.Enabled && configuration.RemoteAccess.Port == localPort)
            throw new InvalidDataException("Local and network MCP listeners must use different ports.");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(localPort);
            if (configuration.RemoteAccess.Enabled)
                options.Listen(IPAddress.Parse(configuration.RemoteAccess.ListenAddress), configuration.RemoteAccess.Port);
        });
        builder.Services.AddSingleton(new AuthTokenStore(Path.Combine(Path.GetDirectoryName(configuration.FilePath)!, "auth.json")));
        var registry = new BrokerRegistry(configuration);
        builder.Services.AddSingleton(registry);
        var workspace = new WorkspaceService(configuration, registry);
        var workspaceEndpoint = new WorkspaceMcpEndpoint(workspace);
        builder.Services.AddSingleton(workspaceEndpoint);
        builder.Services.AddHostedService<WorkspaceMaintenanceService>();
        builder.Services.AddHostedService<BrokerMaintenanceService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, BrokerJsonContext.Default));
        var contexts = new HttpContextAccessor();
        builder.Services.AddSingleton<IHttpContextAccessor>(contexts);
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(UnityBrokerTools.CreateTools(new UnityBrokerTools(registry, contexts)));

        var app = builder.Build();
        // Invalid user-edited credential/config files fail before the port starts accepting clients.
        app.Services.GetRequiredService<AuthTokenStore>().GetTokens();
        app.UseHostFiltering();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
        app.Use(async (context, next) =>
        {
            // The listener, not a caller-supplied header, chooses the export boundary.
            if (context.Connection.LocalPort != localPort)
            {
                var workspaceRequest = context.Request.Path.Equals("/remote/mcp", StringComparison.Ordinal);
                if (!workspaceRequest && !context.Request.Path.Equals("/mcp", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (!IsAllowedNetworkHost(context.Request.Host.Host, configuration.RemoteAccess))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                var origin = context.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin) && !configuration.RemoteAccess.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (!(workspaceRequest ? workspace.Accepts(context.Request.Headers.Authorization.ToString()) :
                    configuration.AcceptsPeerToken(context.Request.Headers.Authorization.ToString())))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer realm=\"UnityEvalTool\"";
                    return;
                }
                context.Items[LocalOnlyRequest] = true;
                await next(context);
                return;
            }
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
                $"http://{BrokerConstants.Host}:{localPort}", StartedAtUtc,
                registry.Revision, registry.GetSnapshot().ConnectedCount, false)
            {
                StoredTokenCount = tokens.GetTokens().Count,
                MaxStoredTokenCount = tokens.MaxStoredTokens
            });
        app.Map("/unity", UnityWebSocketEndpoint.HandleAsync);
        app.Map("/cli", CliWebSocketEndpoint.HandleAsync);
        app.MapMcp("/mcp");
        app.MapMethods("/remote/mcp", ["GET", "POST", "DELETE"], workspaceEndpoint.HandleAsync);
        app.MapGet("/remote/status", () => Results.Content(WorkspaceStatusPage.Html, "text/html; charset=utf-8"));
        await app.RunAsync();
    }

    private static bool IsAllowedNetworkHost(string host, RemoteAccessOptions options)
    {
        host = host.Trim('[', ']');
        return string.Equals(host, options.ListenAddress.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
               options.AllowedHosts.Any(allowed => string.Equals(host, allowed.Trim('[', ']'), StringComparison.OrdinalIgnoreCase));
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
