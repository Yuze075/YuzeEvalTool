using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace YuzeToolkit.Eval.Broker;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
[McpServerToolType]
internal sealed class UnityBrokerTools(BrokerRegistry registry)
{
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
        McpServerTool.Create(GetMethod(nameof(Connect)), tools,
            new McpServerToolCreateOptions { OutputSchema = PermissiveOutputSchema.RootElement }),
        McpServerTool.Create(GetMethod(nameof(EvalAsync)), tools),
    ];

    private static MethodInfo GetMethod(string name) => typeof(UnityBrokerTools).GetMethod(name)!;
    [McpServerTool(Name = "unity_status", UseStructuredContent = true)]
    [Description("Read Unity instances and lifecycle state, or wait for a state transition without polling eval. Call this before unity_connect. Event-driven waits survive temporary disconnects. ready includes normal Ready and executable CompilationFailed repair mode; compilation-complete returns after either compilation success or failure. Always inspect phase, canEval, and compiler counts in the result.")]
    public async Task<JsonElement> StatusAsync(
        [Description("Optional handle returned by unity_connect. Pass either this or instanceId when waiting.")] string connectionHandle = "",
        [Description("Optional instanceId returned by an earlier snapshot. Use this to wait before unity_connect, including while Unity is compiling or reloading.")] string instanceId = "",
        [Description("snapshot; ready (normal Ready or CompilationFailed repair mode); or compilation-complete (successful or failed terminal compilation). Always inspect the returned phase.")] string waitFor = "snapshot",
        [Description("Optional compilationCycleId from unity_status to match while waiting. Do not pass the Unity-side requestId returned by scheduleAssetRefresh.")] string compilationCycleId = "",
        [Description("Deprecated compatibility alias for compilationCycleId. New callers should use compilationCycleId.")] string requestId = "",
        [Description("Optional capturedAtUtc from a fresh unity_status snapshot taken immediately before the eval that requests compilation. compilation-complete then ignores older cycles.")] string observedAfterUtc = "",
        [Description("Wait timeout in seconds. Zero returns immediately.")] int timeoutSeconds = 0,
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
        var snapshot = await registry.WaitAsync(connectionHandle, instanceId, waitFor, selectedCompilationCycleId, observedAfter, timeout,
            cancellationToken);
        return JsonSerializer.SerializeToElement(snapshot, BrokerJsonContext.Default.RegistrySnapshot);
    }

    [McpServerTool(Name = "unity_connect", UseStructuredContent = true)]
    [Description("Bind this workflow to one Unity instance. First call unity_status, choose the exact instance, and pass that snapshot's registryRevision. Reuse the returned opaque connectionHandle across compilation, temporary disconnects, and same-process Domain Reload; reconnect only when the handle is invalid, expired, or belongs to a replaced Unity process.")]
    public JsonElement Connect(
        [Description("Exact instanceId returned by unity_status.")] string instanceId,
        [Description("Exact registryRevision returned by the preceding unity_status call.")] long registryRevision)
    {
        var result = registry.Connect(instanceId, registryRevision);
        return JsonSerializer.SerializeToElement(result, BrokerJsonContext.Default.ConnectionLeaseResult);
    }

    [McpServerTool(Name = "eval")]
    [Description("Run one JavaScript request in the Unity selected by unity_connect, using the persistent eval session and tools:// module contract described by the code parameter. Eval is unavailable while Unity is compiling, reloading, importing, changing PlayMode, stalled, or disconnected. Before a request that may compile, retain a fresh unity_status capturedAtUtc; after the request returns, wait with unity_status(waitFor: compilation-complete, observedAfterUtc: capturedAtUtc). CompilationFailed remains executable against the last successful assemblies for repair. Never automatically retry an interrupted request whose effects are uncertain.")]
    public async Task<CallToolResult> EvalAsync(
        [Description("Opaque handle returned by unity_connect.")] string connectionHandle,
        [Description("JavaScript defining async function execute(). Return concise serializable data. For unfamiliar Unity work, import tools:// for root summaries and getToolDetails(path), then import the relevant tools://Path module; generated tool methods use positional parameters. Prefer helper modules and use CS.* only for uncovered APIs.")] string code,
        [Description("Unity-side execution timeout in seconds, from 1 to 600.")] int timeout = 30,
        [Description("Dispose and recreate this handle's persistent Unity-side PuerTS VM before execution.")] bool resetSession = false,
        CancellationToken cancellationToken = default) =>
        UnityToolResultConverter.Convert(await registry.ExecuteEvalAsync(connectionHandle, code, timeout, resetSession,
            cancellationToken));
}
