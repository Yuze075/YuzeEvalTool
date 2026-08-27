using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class BrokerStatePolicyTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 13, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompilationFailedIsExecutableForLegacyAndCurrentClients()
    {
        var legacy = CreateSnapshot("CompilationFailed", canEval: false);
        var current = CreateSnapshot("CompilationFailed", canEval: true);

        Assert.True(BrokerStatePolicy.CanExecute(legacy.Status));
        Assert.True(BrokerStatePolicy.CanExecute(current.Status));
        BrokerStatePolicy.EnsureCanExecute(legacy);
        BrokerStatePolicy.EnsureCanExecute(current);
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Importing")]
    [InlineData("Compiling")]
    [InlineData("Reloading")]
    [InlineData("PlayModeTransition")]
    [InlineData("MainThreadStalled")]
    public void BusyPhasesRemainRejected(string phase)
    {
        var exception = Assert.Throws<BrokerOperationException>(() =>
            BrokerStatePolicy.EnsureCanExecute(CreateSnapshot(phase, canEval: false)));

        Assert.Equal(BrokerErrorCodes.UnityBusy, exception.Code);
    }

    [Fact]
    public void ReadyWaitMeansExecutionIsAvailable()
    {
        Assert.True(BrokerStatePolicy.MatchesWait(CreateSnapshot("Ready", canEval: true),
            "ready", null, null));
        Assert.True(BrokerStatePolicy.MatchesWait(CreateSnapshot("CompilationFailed", canEval: false),
            "ready", null, null));
        Assert.False(BrokerStatePolicy.MatchesWait(CreateSnapshot("Compiling", canEval: false),
            "ready", null, null));
        Assert.False(BrokerStatePolicy.MatchesWait(CreateSnapshot("Ready", canEval: true, connected: false),
            "ready", null, null));
    }

    [Fact]
    public void CompilationCompleteMatchesSuccessOrFailureAfterObservationMarker()
    {
        var marker = StartedAt.AddSeconds(-1);

        Assert.True(BrokerStatePolicy.MatchesWait(CreateSnapshot("Ready", canEval: true),
            "compilation-complete", "cycle", marker));
        Assert.True(BrokerStatePolicy.MatchesWait(CreateSnapshot("CompilationFailed", canEval: false),
            "compilation-complete", "cycle", marker));
        Assert.False(BrokerStatePolicy.MatchesWait(CreateSnapshot("Ready", canEval: true),
            "compilation-complete", "other-cycle", marker));
        Assert.False(BrokerStatePolicy.MatchesWait(CreateSnapshot("Ready", canEval: true),
            "compilation-complete", "cycle", StartedAt.AddSeconds(1)));
    }

    [Fact]
    public async Task RegistryRejectsUnknownWaitModeEvenForImmediateSnapshot()
    {
        var registry = new BrokerRegistry();
        var error = await Assert.ThrowsAsync<BrokerOperationException>(() =>
            registry.WaitAsync(null, null, "compilaton-complete", null, null, TimeSpan.Zero,
                CancellationToken.None));
        Assert.Equal(BrokerErrorCodes.InvalidRequest, error.Code);
    }

    private static UnityInstanceSnapshot CreateSnapshot(string phase, bool canEval, bool connected = true)
    {
        var status = new UnityStatus(phase, canEval, canEval ? string.Empty : "busy", 1, StartedAt,
            false, false, false, "cycle", phase == "CompilationFailed" ? 1 : 0, 0,
            StartedAt, StartedAt.AddSeconds(1), 1);
        return new UnityInstanceSnapshot("instance", 1, 42, StartedAt, "Project", "/Project",
            "2022.3", "2.0.1", "Editor", connected, StartedAt, StartedAt, status);
    }
}
