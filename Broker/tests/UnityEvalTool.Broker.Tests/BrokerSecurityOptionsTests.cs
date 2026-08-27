using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class BrokerSecurityOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("1", true)]
    public void ParsesExplicitAuthenticationMode(string? value, bool expected)
    {
        Assert.Equal(expected, BrokerSecurityOptions.ParseRequireToken(value));
    }

    [Fact]
    public void InvalidAuthenticationModeFailsExplicitly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BrokerSecurityOptions.ParseRequireToken("sometimes"));

        Assert.Contains(BrokerSecurityOptions.RequireTokenEnvironmentVariable, error.Message);
    }

    [Fact]
    public void DefaultModeAcceptsMissingTokenWithoutCreatingAuthFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "unityevaltool-security-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "auth.json");
        try
        {
            var tokens = new AuthTokenStore(path);

            Assert.True(BrokerSecurityOptions.Create(requireToken: false).Accepts(tokens, null));
            Assert.Null(tokens.TryReadExistingToken());
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnabledModeRejectsMissingTokenAndAcceptsPublishedToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "unityevaltool-security-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "auth.json");
        try
        {
            var tokens = new AuthTokenStore(path);
            var security = BrokerSecurityOptions.Create(requireToken: true);

            Assert.False(security.Accepts(tokens, null));
            var token = tokens.GetOrCreateToken();
            Assert.True(security.Accepts(tokens, token));
            Assert.False(security.Accepts(tokens, token + "-wrong"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
