
namespace InfinityCI.Server.Agents;

/// <summary>
/// Lease watchdog: agents must heartbeat within the timeout or they are marked
/// offline and their running builds are requeued, so builds never get stuck.
/// </summary>
public sealed class AgentLeaseMonitor(
    AgentRegistry registry,
    RemoteBuildCoordinator coordinator,
    ILogger<AgentLeaseMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var expired = registry.All
                    .Where(a => a.Online && DateTimeOffset.UtcNow - a.LastSeenUtc > LeaseTimeout)
                    .ToList();

                foreach (var agent in expired)
                {
                    logger.LogWarning("Agent {Name} ({Id}) lease expired", agent.Name, agent.Id);
                    var orphaned = registry.MarkOffline(agent.Id);
                    await coordinator.RequeueJobRunsAsync(orphaned);
                    await coordinator.PublishAgentsChangedAsync();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
