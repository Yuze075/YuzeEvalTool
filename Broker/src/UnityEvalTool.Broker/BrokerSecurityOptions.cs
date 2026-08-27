namespace YuzeToolkit.Eval.Broker;

internal sealed class BrokerSecurityOptions
{
    public const string RequireTokenEnvironmentVariable = "UNITYEVALTOOL_REQUIRE_TOKEN";

    private BrokerSecurityOptions(bool requireToken)
    {
        RequireToken = requireToken;
    }

    public bool RequireToken { get; }

    public static BrokerSecurityOptions FromEnvironment() =>
        new(ParseRequireToken(Environment.GetEnvironmentVariable(RequireTokenEnvironmentVariable)));

    internal static BrokerSecurityOptions Create(bool requireToken) => new(requireToken);

    internal bool Accepts(AuthTokenStore tokens, string? token) => !RequireToken || tokens.IsValid(token);

    internal static bool ParseRequireToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        if (bool.TryParse(normalized, out var parsed)) return parsed;
        return normalized switch
        {
            "1" => true,
            "0" => false,
            _ => throw new InvalidOperationException(
                $"{RequireTokenEnvironmentVariable} must be true, false, 1, or 0.")
        };
    }
}
