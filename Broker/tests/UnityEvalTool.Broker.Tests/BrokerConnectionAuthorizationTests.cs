#nullable enable
using System.Text.Json;
using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class BrokerConnectionAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yuze-connection-auth-" + Guid.NewGuid().ToString("N"));
    private readonly string _secret = Guid.NewGuid().ToString("N");

    private BrokerConfiguration Configuration(string mode = "token")
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "peer-auth.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["connection"] = _secret }));
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            remoteAccess = new { enabled = true, authMode = mode, credentialRef = "connection" },
            workspace = new { projects = Array.Empty<object>() }
        }));
        return BrokerConfiguration.Load(path);
    }

    [Fact]
    public void HostCredentialAuthorizesConnectionBeforeAnyProjectExists()
    {
        var config = Configuration();
        Assert.Empty(config.Projects);
        Assert.True(config.AcceptsPeerToken("Bearer " + _secret));
        Assert.True(config.AcceptsPeerToken("bearer " + _secret));
        Assert.False(File.Exists(Path.Combine(_root, "auth.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Basic invalid")]
    [InlineData("Bearer invalid")]
    [InlineData("Bearer ")]
    public void MissingOrWrongConnectionCredentialIsRejected(string authorization) =>
        Assert.False(Configuration().AcceptsPeerToken(authorization));

    [Fact]
    public void CredentialListsDoNotGrantConnectionAccess()
    {
        var config = Configuration();
        Assert.False(config.AcceptsPeerToken("Bearer " + _secret + "/" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void ExplicitAnonymousHostPolicyNeedsNoProjectCredential() =>
        Assert.True(Configuration("none").AcceptsPeerToken(string.Empty));

    [Fact]
    public void ReferencedConnectionSecretMustExistBeforeStartup()
    {
        var config = Configuration();
        File.WriteAllText(Path.Combine(_root, "peer-auth.json"), "{}");
        Assert.Throws<InvalidDataException>(() => BrokerConfiguration.Load(config.FilePath));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
