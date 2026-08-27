using System.Text.Json;
using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class CliWebSocketEndpointTests
{
    [Theory]
    [InlineData("{\"compilationCycleId\":\"cycle\"}", "cycle")]
    [InlineData("{\"requestId\":\"legacy\"}", "legacy")]
    [InlineData("{\"compilationCycleId\":\"same\",\"requestId\":\"same\"}", "same")]
    public void CompilationCycleIdSupportsFormalNameAndLegacyAlias(string json, string expected)
    {
        using var payload = JsonDocument.Parse(json);
        Assert.Equal(expected, CliWebSocketEndpoint.ResolveCompilationCycleId(payload.RootElement));
    }

    [Fact]
    public void CompilationCycleIdRejectsConflictingAlias()
    {
        using var payload = JsonDocument.Parse(
            "{\"compilationCycleId\":\"new\",\"requestId\":\"old\"}");
        var error = Assert.Throws<BrokerOperationException>(() =>
            CliWebSocketEndpoint.ResolveCompilationCycleId(payload.RootElement));
        Assert.Equal(BrokerErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public void CompilationCycleIdRejectsNonStringValues()
    {
        using var payload = JsonDocument.Parse("{\"compilationCycleId\":42}");
        var error = Assert.Throws<BrokerOperationException>(() =>
            CliWebSocketEndpoint.ResolveCompilationCycleId(payload.RootElement));
        Assert.Equal(BrokerErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public void ObservedAfterUtcParsesStrictRoundTripTimestamp()
    {
        using var payload = JsonDocument.Parse("{\"observedAfterUtc\":\"2026-08-13T03:04:05.1234567+00:00\"}");
        Assert.Equal(new DateTimeOffset(2026, 8, 13, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567),
            CliWebSocketEndpoint.ResolveObservedAfterUtc(payload.RootElement));
    }

    [Theory]
    [InlineData("{\"observedAfterUtc\":42}")]
    [InlineData("{\"observedAfterUtc\":\"not-a-timestamp\"}")]
    [InlineData("{\"observedAfterUtc\":\"08/13/2026 03:04:05\"}")]
    public void ObservedAfterUtcRejectsInvalidValues(string json)
    {
        using var payload = JsonDocument.Parse(json);
        var error = Assert.Throws<BrokerOperationException>(() =>
            CliWebSocketEndpoint.ResolveObservedAfterUtc(payload.RootElement));
        Assert.Equal(BrokerErrorCodes.InvalidRequest, error.Code);
    }
}
