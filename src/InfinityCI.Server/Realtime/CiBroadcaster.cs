using System.Security.Claims;
using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Hubs;
using InfinityCI.Server.Runs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Realtime;

/// <summary>
/// Bridges in-process events to SignalR groups: run/job status changes go to
/// the run's group and the dashboard; log lines go to the owning run's group
/// only; agent changes go to the agents group.
/// </summary>
public sealed class CiBroadcaster(
    RunEvents events,
    AgentRegistry agentRegistry,
    IServiceScopeFactory scopeFactory,
    IHubContext<CiHub> hubContext,
    ILogger<CiBroadcaster> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        events.RunUpdated += OnRunUpdatedAsync;
        events.JobRunUpdated += OnJobRunUpdatedAsync;
        events.LogAppended += OnLogAppendedAsync;
        agentRegistry.AgentsChanged += OnAgentsChangedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        events.RunUpdated -= OnRunUpdatedAsync;
        events.JobRunUpdated -= OnJobRunUpdatedAsync;
        events.LogAppended -= OnLogAppendedAsync;
        agentRegistry.AgentsChanged -= OnAgentsChangedAsync;
        return Task.CompletedTask;
    }

    private Task OnRunUpdatedAsync(Run run) =>
        hubContext.Clients
            .Groups(CiGroups.Run(run.Id), CiGroups.Dashboard)
            .SendAsync("runUpdated", run);

    private Task OnJobRunUpdatedAsync(JobRun jobRun) =>
        hubContext.Clients
            .Groups(CiGroups.Run(jobRun.RunId), CiGroups.Dashboard)
            .SendAsync("jobUpdated", jobRun);

    private Task OnLogAppendedAsync(LogAppendedEventArgs args) =>
        hubContext.Clients
            .Group(CiGroups.Run(args.RunId))
            .SendAsync("logAppended", args.RunId, args.JobKey, args.Line);

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
