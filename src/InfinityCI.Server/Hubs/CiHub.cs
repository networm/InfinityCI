using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Runs;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Hubs;

public sealed record RunSubscription(Run Run, IReadOnlyList<JobRun> JobRuns, IReadOnlyDictionary<string, IReadOnlyList<LogLine>> Logs);

public sealed record RunsPageItem(Run Run, IReadOnlyList<JobRun> Jobs);

public sealed record UserRow(long Id, string Username, string? DisplayName, string Role, bool HasPassword, IReadOnlyList<UserProjectRef> Projects);

public sealed record UserProjectRef(long ProjectId, string Name);

/// <summary>
/// Real-time hub. Each browser tab opens its own connection with its own
/// ConnectionId; subscriptions are per-connection and SignalR removes group
/// membership automatically on disconnect, so tabs never affect each other.
/// Every snapshot and every joined event group respects project visibility.
/// </summary>
[Authorize]
public sealed class CiHub(JobLogStore logStore, RunRepository repository, AgentRegistry agentRegistry, WorkflowDirectory directory, QueueSnapshot queueSnapshot, CiDbContext db) : Hub
{
    /// <summary>Heartbeat target for the client watchdog; echoes the server time.</summary>
    public Task<DateTimeOffset> Ping() => Task.FromResult(DateTimeOffset.UtcNow);

    /// <summary>
    /// Joins the run's group and returns a full snapshot: run, all parallel job
    /// runs, and each job's complete log. Live events may duplicate backfill
    /// lines; clients drop any line whose index is below their cursor.
    /// Runs in invisible projects are rejected exactly like the REST API.
    /// </summary>
    public async Task<RunSubscription> SubscribeRun(long runId)
    {
        var run = await repository.GetRunAsync(runId)
            ?? throw new HubException($"Run {runId} not found.");
        if (!await CanSeeAsync(run.Project))
            throw new HubException($"Run {runId} not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Run(runId));
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

    /// <summary>
    /// Joins the caller's visible per-project groups and returns the
    /// visibility-filtered recent-runs snapshot.
    /// </summary>
    public async Task<IReadOnlyList<RunsPageItem>> SubscribeDashboard(int skip = 0, int take = 30)
    {
        var visible = await ProjectVisibility.VisibleProjectsAsync(Context.User!, db);
        await JoinProjectGroupsAsync(visible);

        var runs = await repository.ListRunsAsync(Math.Max(0, skip), take is < 1 or > 100 ? 30 : take);
        if (visible is not null)
            runs = runs.Where(r => visible.Contains(r.Project)).ToList();
        var jobs = await repository.GetJobRunsForAsync(runs.Select(r => r.Id));
        return runs.Select(r => new RunsPageItem(r, jobs.GetValueOrDefault(r.Id, []))).ToList();
    }

    public Task UnsubscribeDashboard() => Task.CompletedTask;

    public async Task<IReadOnlyList<AgentSnapshot>> SubscribeAgents()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Agents);
        return agentRegistry.Snapshot();
    }

    public Task UnsubscribeAgents() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Agents);

    public async Task<IReadOnlyList<WorkflowSummary>> SubscribeJobs()
    {
        var visible = await ProjectVisibility.VisibleProjectsAsync(Context.User!, db);
        await JoinProjectGroupsAsync(visible);
        return await directory.ListAsync(visible);
    }

    public Task UnsubscribeJobs() => Task.CompletedTask;

    public async Task<IReadOnlyList<QueueItem>> SubscribeQueue()
    {
        var visible = await ProjectVisibility.VisibleProjectsAsync(Context.User!, db);
        await JoinProjectGroupsAsync(visible);
        return await queueSnapshot.BuildAsync(visible);
    }

    public Task UnsubscribeQueue() => Task.CompletedTask;

    public async Task<IReadOnlyList<Storage.Project>> SubscribeProjects()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Projects);
        return await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
    }

    public Task UnsubscribeProjects() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Projects);

    [Authorize(Policy = "SuperAdmin")]
    public async Task<IReadOnlyList<UserRow>> SubscribeUsers()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Users);
        return await db.Users.AsNoTracking()
            .Include(u => u.Projects).ThenInclude(up => up.Project)
            .OrderBy(u => u.Username)
            .Select(u => new UserRow(
                u.Id,
                u.Username,
                u.DisplayName,
                u.Role,
                u.PasswordHash != "",
                u.Projects.Select(up => new UserProjectRef(up.ProjectId, up.Project.Name)).ToList()))
            .ToListAsync();
    }

    public Task UnsubscribeUsers() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Users);

    /// <summary>Joins project-{name} for every visible project; admins (visible
    /// == null) join all current projects. Project-group membership is computed
    /// at subscribe time; a newly created project requires a re-subscribe.</summary>
    private async Task JoinProjectGroupsAsync(HashSet<string>? visible)
    {
        var names = visible is null
            ? await db.Projects.AsNoTracking().Select(p => p.Name).ToListAsync()
            : visible.ToList();
        foreach (var name in names)
            await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Project(name));
    }

    private async Task<bool> CanSeeAsync(string project)
    {
        var visible = await ProjectVisibility.VisibleProjectsAsync(Context.User!, db);
        return visible is null || visible.Contains(project);
    }
}
