using ModelContextProtocol;

namespace YuzeToolkit.Eval.Broker;

internal static class BrokerConstants
{
    public const string ProtocolVersion = "2.0";
    public static readonly string PackageVersion =
        typeof(BrokerConstants).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public const string Host = "127.0.0.1";
    public const int Port = 2347;
    public static readonly TimeSpan MainThreadStallAfter = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DisconnectedRetention = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LeaseIdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(10);
    public const int MaxPendingAuthenticationConnections = 32;
}

internal static class BrokerErrorCodes
{
    public const string AuthenticationFailed = nameof(AuthenticationFailed);
    public const string UnityAuthorizationPending = nameof(UnityAuthorizationPending);
    public const string ProtocolMismatch = nameof(ProtocolMismatch);
    public const string InvalidRequest = nameof(InvalidRequest);
    public const string DiscoveryRequired = nameof(DiscoveryRequired);
    public const string RegistryChanged = nameof(RegistryChanged);
    public const string UnityNotFound = nameof(UnityNotFound);
    public const string ConnectionHandleRequired = nameof(ConnectionHandleRequired);
    public const string ConnectionHandleInvalid = nameof(ConnectionHandleInvalid);
    public const string UnityDisconnected = nameof(UnityDisconnected);
    public const string UnityBusy = nameof(UnityBusy);
    public const string CompilationFailed = nameof(CompilationFailed);
    public const string RequestTimedOut = nameof(RequestTimedOut);
    public const string ExecutionOutcomeUnknown = nameof(ExecutionOutcomeUnknown);
    public const string BrokerUnavailable = nameof(BrokerUnavailable);
}

internal sealed class BrokerOperationException : McpException
{
    public BrokerOperationException(string code, string message, bool mayHaveExecuted = false)
        : base(mayHaveExecuted ? $"{code}: {message} (mayHaveExecuted=true)" : $"{code}: {message}")
    {
        Code = code;
        MayHaveExecuted = mayHaveExecuted;
    }

    public string Code { get; }
    public bool MayHaveExecuted { get; }
}
