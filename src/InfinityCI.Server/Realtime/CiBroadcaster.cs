using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Hubs;
using InfinityCI.Server.Runs;
using Microsoft.AspNetCore.SignalR;

namespace InfinityCI.Server.Realtime;

/// <summary>
/// Bridges in-process events to SignalR groups. Run/job/workflow events fan
/// out to per-project groups so members only ever receive projects they are
/// allowed to see; agent changes go to the agents group; queue movement is
/// broadcast as a payload-less signal (clients re-fetch the filtered queue).
/// </summary>
public sealed class CiBroadcaster(
    RunEvents events,
    ChangeEvents changeEvents,
    AgentRegistry agentRegistry,
    QueueSnapshot queueSnapshot,
    IHubContext<CiHub> hubContext,
    ILogger<CiBroadcaster> logger) : IHostedService
{
    private int _queueRefreshPending;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        events.RunUpdated += OnRunUpdatedAsync;
        events.JobRunUpdated += OnJobRunUpdatedAsync;
        events.LogAppended += OnLogAppendedAsync;
        agentRegistry.AgentsChanged += OnAgentsChangedAsync;
        changeEvents.WorkflowChanged += OnWorkflowChangedAsync;
        changeEvents.AdminChanged += OnAdminChangedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        events.RunUpdated -= OnRunUpdatedAsync;
        events.JobRunUpdated -= OnJobRunUpdatedAsync;
        events.LogAppended -= OnLogAppendedAsync;
        agentRegistry.AgentsChanged -= OnAgentsChangedAsync;
        changeEvents.WorkflowChanged -= OnWorkflowChangedAsync;
        changeEvents.AdminChanged -= OnAdminChangedAsync;
        return Task.CompletedTask;
    }

    private Task OnRunUpdatedAsync(Run run) =>
        hubContext.Clients
            .Groups(CiGroups.Run(run.Id), CiGroups.Project(run.Project))
            .SendAsync("runUpdated", run);

    private Task OnJobRunUpdatedAsync(JobRun jobRun)
    {
        ScheduleQueueRefresh();
        return hubContext.Clients
            .Groups(CiGroups.Run(jobRun.RunId), CiGroups.Project(jobRun.Project))
            .SendAsync("jobUpdated", jobRun);
    }

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

    private Task OnWorkflowChangedAsync(WorkflowChangedEventArgs args) =>
        hubContext.Clients
            .Group(CiGroups.Project(args.Project))
            .SendAsync("workflowChanged", args.Kind, args.Name);

    private Task OnAdminChangedAsync(AdminChangedEventArgs args) => args.Kind switch
    {
        AdminChangeKind.Projects => hubContext.Clients.Group(CiGroups.Projects).SendAsync("projectsChanged"),
        AdminChangeKind.Users => hubContext.Clients.Group(CiGroups.Users).SendAsync("usersChanged"),
        AdminChangeKind.Credentials => hubContext.Clients.Group(CiGroups.Agents).SendAsync("credentialsChanged"),
        AdminChangeKind.Enrollments => hubContext.Clients.Group(CiGroups.Agents).SendAsync("enrollmentsChanged"),
        AdminChangeKind.Agents => hubContext.Clients.Group(CiGroups.Agents).SendAsync("enrolledChanged"),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Queue membership changes are inferred from job run updates; bursts are
    /// coalesced into one debounced signal. The signal carries no payload —
    /// each client re-fetches /api/queue, which filters by its own visibility.
    /// </summary>
    private void ScheduleQueueRefresh()
    {
        if (Interlocked.CompareExchange(ref _queueRefreshPending, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200);
                Interlocked.Exchange(ref _queueRefreshPending, 0);
                await hubContext.Clients.All.SendAsync("queueUpdated");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to broadcast queue update");
            }
        });
    }
}
