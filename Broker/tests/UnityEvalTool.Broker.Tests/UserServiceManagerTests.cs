using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class UserServiceManagerTests
{
    [Fact]
    public void SystemdExecStartQuotesSpecialPathCharacters()
    {
        var unit = UserServiceManager.BuildSystemdUnit("/home/user/My Tools/100%/unity\\broker");

        Assert.Contains("ExecStart=\"/home/user/My Tools/100%%/unity\\\\broker\" broker", unit);
    }

    [Fact]
    public void SystemdExecStartRejectsControlCharacters()
    {
        Assert.Throws<InvalidOperationException>(() => UserServiceManager.BuildSystemdUnit("/tmp/bad\npath"));
    }
}
