#nullable enable
namespace YuzeToolkit.Eval.Broker;

internal sealed class BrokerMaintenanceService(BrokerRegistry registry, WorkspaceService workspace) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(MaintainLocalAsync(stoppingToken), registry.RunRemoteDiscoveryAsync(stoppingToken));

    private async Task MaintainLocalAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await registry.RunMaintenanceAsync(stoppingToken);
                workspace.Maintain();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally { workspace.Stop(); }
    }
}
