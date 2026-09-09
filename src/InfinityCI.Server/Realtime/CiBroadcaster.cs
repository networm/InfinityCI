using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace InfinityCI.Server.Realtime;

/// <summary>
/// Bridges in-process events to SignalR groups: build status changes go to the
/// build's group and the dashboard; log lines go to the owning build's group
/// only; agent changes go to the agents group.
/// </summary>
public sealed class CiBroadcaster(
    BuildEvents events,
    AgentRegistry agentRegistry,
    IHubContext<CiHub> hubContext,
    ILogger<CiBroadcaster> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        events.BuildUpdated += OnBuildUpdatedAsync;
        events.LogAppended += OnLogAppendedAsync;
        agentRegistry.AgentsChanged += OnAgentsChangedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        events.BuildUpdated -= OnBuildUpdatedAsync;
        events.LogAppended -= OnLogAppendedAsync;
        agentRegistry.AgentsChanged -= OnAgentsChangedAsync;
        return Task.CompletedTask;
    }

    private Task OnBuildUpdatedAsync(Build build) =>
        hubContext.Clients
            .Groups(CiGroups.Build(build.Id), CiGroups.Dashboard)
            .SendAsync("buildUpdated", build);

    private Task OnLogAppendedAsync(LogAppendedEventArgs args) =>
        hubContext.Clients
            .Group(CiGroups.Build(args.BuildId))
            .SendAsync("logAppended", args.BuildId, args.Offset, args.Text);

    private async Task OnAgentsChangedAsync(IReadOnlyList<AgentSnapshot> snapshot)
    {
        try
        {
            await hubContext.Clients.Group(CiGroups.Agents).SendAsync("agentsUpdated", snapshot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to broadcast agents update");
        }
    }
}
