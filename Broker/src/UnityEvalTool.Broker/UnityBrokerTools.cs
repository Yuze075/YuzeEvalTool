#nullable enable
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace YuzeToolkit.Eval.Broker;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
[McpServerToolType]
internal sealed class UnityBrokerTools(BrokerRegistry registry, IHttpContextAccessor contexts)
{
    // A network export serves only this computer's Unity instances; the local endpoint aggregates remotes.
    private bool NetworkExport => (contexts.HttpContext ?? throw new InvalidOperationException("MCP HTTP context is unavailable."))
        .Items.ContainsKey(BrokerHost.NetworkExportRequest);
    // MCP protocol versions through 2025-11-25 require every outputSchema.properties value to be a
    // JSON object, but the MCP SDK derives the boolean JSON Schema "true" for JsonElement returns,
    // which strict clients then reject. "{}" is the object-form equivalent of "true": the SDK wraps
    // it into the same {"result": ...} envelope on those protocol versions, so the wire format of
    // both tools/list and structuredContent stays unchanged.
    private static readonly JsonDocument PermissiveOutputSchema = JsonDocument.Parse("{}");

    internal static IReadOnlyList<McpServerTool> CreateTools(UnityBrokerTools tools) =>
    [
        McpServerTool.Create(GetMethod(nameof(StatusAsync)), tools,
            new McpServerToolCreateOptions { OutputSchema = PermissiveOutputSchema.RootElement }),
        McpServerTool.Create(GetMethod(nameof(ConnectAsync)), tools,
            new McpServerToolCreateOptions { OutputSchema = PermissiveOutputSchema.RootElement }),
        McpServerTool.Create(GetMethod(nameof(EvalAsync)), tools),
    ];

    private static MethodInfo GetMethod(string name) => typeof(UnityBrokerTools).GetMethod(name)!;
    [McpServerTool(Name = "unity_status", UseStructuredContent = true)]
    [Description("List local and remote Unity instances with their lifecycle state, or wait for a state change. Call before unity_connect. Remote instances have ids like remote:<host>:<id>; choose them by host and projectPath, never by matching the local working directory. Check isConnected, source.isStale, status.phase, status.canEval and remoteBrokers errors. Before an eval that may compile, call this with the same connectionHandle and keep capturedAtUtc; it uses that instance's clock.")]
    public async Task<JsonElement> StatusAsync(
        [Description("Handle from unity_connect. Pass this or instanceId when waiting.")] string connectionHandle = "",
        [Description("instanceId from an earlier snapshot; allows waiting before unity_connect, e.g. while Unity compiles.")] string instanceId = "",
        [Description("snapshot (default); ready (Ready, or CompilationFailed repair mode); compilation-complete (succeeded or failed). Always check the returned phase.")] string waitFor = "snapshot",
        [Description("Optional compilationCycleId from unity_status to wait for. Not the requestId returned by scheduleAssetRefresh.")] string compilationCycleId = "",
        [Description("Deprecated alias of compilationCycleId.")] string requestId = "",
        [Description("capturedAtUtc from the unity_status call made just before the compiling eval; compilation-complete then ignores older cycles.")] string observedAfterUtc = "",
        [Description("Wait timeout in seconds, 0–3600; 0 returns immediately.")] int timeoutSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset? observedAfter = null;
        if (!string.IsNullOrWhiteSpace(observedAfterUtc))
        {
            if (!DateTimeOffset.TryParse(observedAfterUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                    "observedAfterUtc must be an ISO-8601 timestamp returned as capturedAtUtc by unity_status.");
            observedAfter = parsed;
        }
        if (!string.IsNullOrWhiteSpace(compilationCycleId) && !string.IsNullOrWhiteSpace(requestId) &&
            !string.Equals(compilationCycleId, requestId, StringComparison.Ordinal))
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                "compilationCycleId and its deprecated requestId alias must match when both are provided.");
        var selectedCompilationCycleId = string.IsNullOrWhiteSpace(compilationCycleId) ? requestId : compilationCycleId;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0, 3600));
        var snapshot = NetworkExport
            ? await registry.WaitAsync(connectionHandle, instanceId, waitFor, selectedCompilationCycleId, observedAfter, timeout, cancellationToken)
            : await registry.WaitForProjectAsync(connectionHandle, instanceId, waitFor, selectedCompilationCycleId, observedAfter, timeout, cancellationToken);
        return JsonSerializer.SerializeToElement(snapshot, BrokerJsonContext.Default.RegistrySnapshot);
    }

    [McpServerTool(Name = "unity_connect", UseStructuredContent = true)]
    [Description("Bind to one Unity instance chosen from unity_status, passing that snapshot's registryRevision. Reuse the returned connectionHandle across compilation, Domain Reload and brief disconnects; reconnect only when the handle is reported invalid or expired, or the Unity process was replaced.")]
    public async Task<JsonElement> ConnectAsync(
        [Description("Exact instanceId from unity_status.")] string instanceId,
        [Description("registryRevision from the same unity_status call.")] long registryRevision,
        CancellationToken cancellationToken = default)
    {
        var result = NetworkExport ? registry.Connect(instanceId, registryRevision)
            : await registry.ConnectProjectAsync(instanceId, registryRevision, cancellationToken);
        return JsonSerializer.SerializeToElement(result, BrokerJsonContext.Default.ConnectionLeaseResult);
    }

    [McpServerTool(Name = "eval")]
    [Description("Run JavaScript in the connected Unity's persistent PuerTS session. Unavailable while Unity is compiling, reloading, importing, changing Play Mode, stalled or disconnected. CompilationFailed still runs against the last successful assemblies, for repairs. For code that may compile: take capturedAtUtc from unity_status first, then call unity_status(waitFor: compilation-complete, observedAfterUtc: capturedAtUtc). Never automatically retry a request whose outcome is unknown.")]
    public async Task<CallToolResult> EvalAsync(
        [Description("Handle from unity_connect.")] string connectionHandle,
        [Description("JavaScript defining async function execute() that returns small serializable data. To discover helpers, import tools:// for summaries and getToolDetails(path), then import tools://<Path>; generated methods take positional arguments. Use CS.* only for APIs the helpers do not cover.")] string code,
        [Description("Unity-side timeout in seconds, 1–600.")] int timeout = 30,
        [Description("Recreate this handle's PuerTS VM before running.")] bool resetSession = false,
        CancellationToken cancellationToken = default) => NetworkExport
            ? UnityToolResultConverter.Convert(await registry.ExecuteEvalAsync(connectionHandle, code, timeout, resetSession, cancellationToken))
            : await registry.ExecuteProjectEvalAsync(connectionHandle, code, timeout, resetSession, cancellationToken);
}
