using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Runs;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace InfinityCI.Server.Hubs;

public sealed record RunSubscription(Run Run, IReadOnlyList<JobRun> JobRuns, IReadOnlyDictionary<string, IReadOnlyList<LogLine>> Logs);

public sealed record RunsPageItem(Run Run, IReadOnlyList<JobRun> Jobs);

/// <summary>
/// Real-time hub. Each browser tab opens its own connection with its own
/// ConnectionId; subscriptions are per-connection and SignalR removes group
/// membership automatically on disconnect, so tabs never affect each other.
/// </summary>
[Authorize]
public sealed class CiHub(JobLogStore logStore, RunRepository repository, AgentRegistry agentRegistry) : Hub
{
    /// <summary>
    /// Joins the run's group and returns a full snapshot: run, all parallel job
    /// runs, and each job's complete log. Live events may duplicate backfill
    /// lines; clients drop any line whose index is below their cursor.
    /// </summary>
    public async Task<RunSubscription> SubscribeRun(long runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Run(runId));

        var run = await repository.GetRunAsync(runId)
            ?? throw new HubException($"Run {runId} not found.");
        var jobRuns = await repository.GetJobRunsAsync(runId);

        // Join first, then backfill: lines appended in between are delivered
        // twice (live + backfill) and deduplicated client-side by line index.
        var logs = new Dictionary<string, IReadOnlyList<LogLine>>();
        foreach (var jobRun in jobRuns)
            logs[jobRun.JobKey] = await logStore.ReadAfterAsync(runId, run.WorkflowName, run.RunNumber, jobRun.JobKey, 0);

        return new RunSubscription(run, jobRuns, logs);
    }

    public Task UnsubscribeRun(long runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Run(runId));

    public async Task<IReadOnlyList<RunsPageItem>> SubscribeDashboard(int skip = 0, int take = 30)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Dashboard);
        var runs = await repository.ListRunsAsync(Math.Max(0, skip), take is < 1 or > 100 ? 30 : take);
        var jobs = await repository.GetJobRunsForAsync(runs.Select(r => r.Id));
        return runs.Select(r => new RunsPageItem(r, jobs.GetValueOrDefault(r.Id, []))).ToList();
    }

    public Task UnsubscribeDashboard() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Dashboard);

    public async Task<IReadOnlyList<AgentSnapshot>> SubscribeAgents()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Agents);
        return agentRegistry.Snapshot();
    }

    public Task UnsubscribeAgents() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Agents);
}
