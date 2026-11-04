#nullable enable
using System.Net;
using ModelContextProtocol.Server;

namespace YuzeToolkit.Eval.Broker;

internal static class BrokerHost
{
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
    public static readonly string InstanceId = Guid.NewGuid().ToString("N");
    private static readonly object NetworkExportRequest = new();

    // The listener, not a caller-supplied value, decides between the local and network export views.
    public static bool IsNetworkExport(IHttpContextAccessor contexts) =>
        (contexts.HttpContext ?? throw new InvalidOperationException("MCP HTTP context is unavailable.")).Items.ContainsKey(NetworkExportRequest);

    private const string Instructions = "Unity: unity_status, then unity_connect, then eval; remote instances are remote:<host>:<id>. " +
        "Remote hosts: workspace_list, then workspace_connect, then read the returned AGENTS guidance and relevant Skills with file_read, then work through contextHandle. " +
        "Workspace tools reach remote hosts only, never this machine. For large edits, prefer a local checkout synced through git and use the remote host to build, test and verify Unity; for small edits use file_edit or file_apply_diff. " +
        "Never automatically retry a call that returned ExecutionOutcomeUnknown; inspect the actual state first. Foreground desktop input needs the user's explicit permission.";

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
        var registry = new BrokerRegistry(configuration);
        builder.Services.AddSingleton(registry);
        var workspace = new WorkspaceService(configuration, registry);
        builder.Services.AddSingleton(workspace);
        builder.Services.AddHostedService<BrokerMaintenanceService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, BrokerJsonContext.Default));
        var contexts = new HttpContextAccessor();
        builder.Services.AddSingleton<IHttpContextAccessor>(contexts);
        builder.Services.AddMcpServer(options => options.ServerInstructions = Instructions)
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(UnityBrokerTools.CreateTools(new UnityBrokerTools(registry, contexts)).Concat(WorkspaceTools.Create(workspace, contexts)));

        var app = builder.Build();
        app.UseHostFiltering();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
        app.Use(async (context, next) =>
        {
            // The listener, not a caller-supplied header, chooses the export boundary.
            if (context.Connection.LocalPort != localPort)
            {
                if (!context.Request.Path.Equals("/mcp", StringComparison.Ordinal))
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
                if (!configuration.AcceptsPeerToken(context.Request.Headers.Authorization.ToString()))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer realm=\"UnityEvalTool\"";
                    return;
                }
                context.Items[NetworkExportRequest] = true;
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

            await next(context);
        });

        app.MapGet("/health", (BrokerRegistry registry) =>
            new HealthSnapshot("ready", BrokerConstants.ProtocolVersion,
                $"http://{BrokerConstants.Host}:{localPort}", StartedAtUtc,
                registry.Revision, registry.GetSnapshot().ConnectedCount));
        app.Map("/unity", UnityWebSocketEndpoint.HandleAsync);
        app.Map("/cli", CliWebSocketEndpoint.HandleAsync);
        app.MapMcp("/mcp");
        app.MapGet("/status", () => Results.Content(WorkspaceStatusPage.Html, "text/html; charset=utf-8"));
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
