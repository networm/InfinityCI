using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Runs;

public sealed record QueueItem(long JobRunId, long RunId, string WorkflowName, string Project, string JobKey, string RunsOn, string? RequiredLabel, DateTimeOffset CreatedAt);

/// <summary>
/// Builds the queued-job-run listing (FIFO order) shared by /api/queue, the
/// hub snapshot and the broadcaster's queueUpdated push.
/// </summary>
public sealed class QueueSnapshot(IServiceScopeFactory scopeFactory)
{
    /// <summary>Currently queued job runs, optionally filtered to visible projects (null = all).</summary>
    public async Task<IReadOnlyList<QueueItem>> BuildAsync(HashSet<string>? visible = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        var queued = await db.JobRuns.AsNoTracking()
            .Where(j => j.Status == JobRunStatus.Queued)
            .OrderBy(j => j.Id)
            .ToListAsync();
        var runIds = queued.Select(j => j.RunId).Distinct().ToArray();
        var runs = await db.Runs.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id);
        return queued
            .Select(j => new QueueItem(
                j.Id,
                j.RunId,
                runs.GetValueOrDefault(j.RunId)?.WorkflowName ?? "?",
                runs.GetValueOrDefault(j.RunId)?.Project ?? "",
                j.JobKey,
                j.RunsOn,
                RemoteBuildCoordinator.PendingFromRunsOn(j.RunsOn, j.Id).RequiredLabel,
                j.CreatedAt))
            .Where(i => visible is null || visible.Contains(i.Project))
            .ToList();
    }
}
