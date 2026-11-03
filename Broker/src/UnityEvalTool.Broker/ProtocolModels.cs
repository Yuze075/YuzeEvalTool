#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuzeToolkit.Eval.Broker;

internal sealed record UnityRegistration(
    string AuthToken,
    string InstanceId,
    long ConnectionEpoch,
    int ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    string ProjectName,
    string ProjectPath,
    string UnityVersion,
    string PackageVersion,
    string Environment,
    UnityStatus Status)
{
    public bool AuthorizationRequired { get; init; }
    public string AuthorizationState { get; init; } = "NotRequired";
}

internal sealed record UnityStatus(
    string Phase,
    bool CanEval,
    string BusyReason,
    long MainThreadTick,
    DateTimeOffset MainThreadTickAtUtc,
    bool IsPlaying,
    bool IsPaused,
    bool IsUpdating,
    string CompilationCycleId,
    int CompilerErrorCount,
    int CompilerWarningCount,
    DateTimeOffset? LastCompilationStartedAtUtc,
    DateTimeOffset? LastCompilationFinishedAtUtc,
    long VmGeneration);

internal sealed record UnityInstanceSnapshot(
    string InstanceId,
    long ConnectionEpoch,
    int ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    string ProjectName,
    string ProjectPath,
    string UnityVersion,
    string PackageVersion,
    string Environment,
    bool IsConnected,
    DateTimeOffset ConnectedAtUtc,
    DateTimeOffset LastTransportHeartbeatAtUtc,
    UnityStatus Status)
{
    public bool AuthorizationRequired { get; init; }
    public string AuthorizationState { get; init; } = "NotRequired";
    public UnityProjectSource Source { get; init; } = new("local", null, string.Empty, false);
    public string DisplayName => Source.Kind == "remote"
        ? $"[Remote:{Source.RemoteId}] {ProjectName}"
        : $"[Local] {ProjectName}";
}

internal sealed record UnityProjectSource(string Kind, string? RemoteId, string BrokerId, bool IsStale);
internal sealed record FederationInfo(int Version, string BrokerId, string BrokerInstanceId, string View,
    int LeaseIdleTimeoutSeconds);
internal sealed record RemoteBrokerSnapshot(string Id, string Endpoint, string State, string? ErrorCode,
    DateTimeOffset? LastSeenAtUtc, int ProjectCount);

internal sealed record RegistrySnapshot(
    long RegistryRevision,
    DateTimeOffset CapturedAtUtc,
    int ConnectedCount,
    string? ConnectionHandle,
    UnityInstanceSnapshot? SelectedUnity,
    IReadOnlyList<UnityInstanceSnapshot> UnityInstances)
{
    public FederationInfo? Federation { get; init; }
    public IReadOnlyList<RemoteBrokerSnapshot> RemoteBrokers { get; init; } = [];
}

internal sealed record ConnectionLeaseResult(
    string ConnectionHandle,
    DateTimeOffset ExpiresAtUtc,
    UnityInstanceSnapshot Unity);

internal sealed record HealthSnapshot(
    string Status,
    string ProtocolVersion,
    string Endpoint,
    DateTimeOffset StartedAtUtc,
    long RegistryRevision,
    int ConnectedUnityCount,
    bool RequireToken)
{
    public int StoredTokenCount { get; init; }
    public int MaxStoredTokenCount { get; init; }
}

internal sealed record UnityRegistrationResponse(
    string InstanceId,
    string ProtocolVersion,
    string BrokerInstanceId,
    IReadOnlyList<string> Tokens);

internal sealed record AuthTokensPayload(IReadOnlyList<string> Tokens);

internal sealed record UnityAuthorizationUpdate(string State);

internal sealed record UnityCommandRequest(
    string SessionId,
    string RequestId,
    string? Code,
    string? Line,
    int TimeoutSeconds,
    bool ResetSession);

internal sealed record ProtocolEnvelope(
    string Protocol,
    string Type,
    string? Id,
    string Method,
    JsonElement Payload,
    ProtocolError? Error);

internal sealed record ProtocolError(string Code, string Message, bool MayHaveExecuted);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UnityRegistration))]
[JsonSerializable(typeof(UnityStatus))]
[JsonSerializable(typeof(UnityInstanceSnapshot))]
[JsonSerializable(typeof(List<UnityInstanceSnapshot>))]
[JsonSerializable(typeof(RegistrySnapshot))]
[JsonSerializable(typeof(ConnectionLeaseResult))]
[JsonSerializable(typeof(HealthSnapshot))]
[JsonSerializable(typeof(UnityRegistrationResponse))]
[JsonSerializable(typeof(AuthTokensPayload))]
[JsonSerializable(typeof(UnityAuthorizationUpdate))]
[JsonSerializable(typeof(UnityCommandRequest))]
[JsonSerializable(typeof(ProtocolEnvelope))]
[JsonSerializable(typeof(ProtocolError))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(BrokerConfiguration))]
[JsonSerializable(typeof(RemoteBrokerOptions))]
[JsonSerializable(typeof(RemoteAccessOptions))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonNode))]
[JsonSerializable(typeof(ModelContextProtocol.Protocol.CallToolResult))]
internal sealed partial class BrokerJsonContext : JsonSerializerContext;
