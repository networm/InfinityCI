using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Storage;

namespace InfinityCI.Server.Runs;

/// <summary>
/// Run status aggregation plus needs-based dispatch. After any job run reaches
/// a terminal state: succeeded jobs release their dependents, failed/cancelled
/// jobs cascade "skipped" to their transitive dependents (GitHub semantics —
/// no `if: always()` yet), and the run status is recomputed.
/// </summary>
public sealed class RunAggregator(
    IServiceScopeFactory scopeFactory,
    RunEvents events,
    JobLogStore logStore,
    AgentRegistry registry,
    LocalJobRunQueue localQueue,
    ILogger<RunAggregator> logger)
{
    public async Task OnJobRunTerminalAsync(JobRun jobRun)
    {
        if (!jobRun.Status.IsTerminal())
            return;
        await EvaluateRunAsync(jobRun.RunId);
    }

    /// <summary>Applies needs gating to all queued job runs of the run, then recomputes run status.</summary>
    public async Task EvaluateRunAsync(long runId)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();

        var run = await repo.GetRunAsync(runId);
        if (run is null || run.IsTerminal)
            return;
        var jobRuns = await repo.GetJobRunsAsync(runId);
        if (jobRuns.Count == 0)
            return;

        var byKey = jobRuns.ToDictionary(j => j.JobKey, StringComparer.OrdinalIgnoreCase);

        // 1) Cascade "skipped" from failed/cancelled roots to transitive dependents.
        var poison = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobRuns.Where(j => j.Status is JobRunStatus.Failed or JobRunStatus.Cancelled))
            poison.Add(job.JobKey);

        var pendingSkip = new Queue<string>(poison);
        var skipChanged = false;
        while (pendingSkip.Count > 0)
        {
            var failedKey = pendingSkip.Dequeue();
            foreach (var dependent in jobRuns.Where(j =>
                         j.Status == JobRunStatus.Queued &&
                         j.Needs.Contains(failedKey, StringComparer.OrdinalIgnoreCase)))
            {
                dependent.Status = JobRunStatus.Skipped;
                dependent.FinishedAt = DateTimeOffset.UtcNow;
                await repo.SaveJobRunTransitionAsync(dependent);
                await events.PublishJobRunUpdatedAsync(dependent);
                await logStore.AppendAndPublishAsync(events, runId, dependent.JobKey, 0,
                    $"[server] job '{dependent.JobKey}' skipped: dependency '{failedKey}' did not succeed.");
                skipChanged = true;
                pendingSkip.Enqueue(dependent.JobKey);
                logger.LogInformation("Job run {JobRunId} ({Job}) skipped due to failed dependency '{Dependency}'",
                    dependent.Id, dependent.JobKey, failedKey);
            }
            _ = poison; // poison set retained for clarity; queue drives the cascade
        }

        // 2) Dispatch queued job runs whose needs are all satisfied.
        foreach (var jobRun in jobRuns.Where(j => j.Status == JobRunStatus.Queued))
        {
            var allSucceeded = jobRun.Needs.All(need =>
                byKey.TryGetValue(need, out var dep) && dep.Status == JobRunStatus.Success);
            if (!allSucceeded)
                continue;

            if (jobRun.RunsOn.StartsWith("agent", StringComparison.OrdinalIgnoreCase))
            {
                var requiredLabel = jobRun.RunsOn.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) && jobRun.RunsOn.Length > "agent:".Length
                    ? jobRun.RunsOn["agent:".Length..].Trim()
                    : null;
                registry.EnqueuePending(new PendingJobRun(jobRun.Id, requiredLabel));
            }
            else
            {
                localQueue.TryWrite(jobRun);
            }
            logger.LogInformation("Dependencies met for job run {JobRunId} ({Job}); dispatched", jobRun.Id, jobRun.JobKey);
        }

        // 3) Recompute run status from job run states.
        await RecomputeAsync(runId);
    }

    public async Task RecomputeAsync(long runId)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();

        var run = await repo.GetRunAsync(runId);
        if (run is null || run.IsTerminal)
            return;

        var jobRuns = await repo.GetJobRunsAsync(runId);
        if (jobRuns.Count == 0)
            return;

        RunStatus next;
        if (jobRuns.All(j => j.IsTerminal))
        {
            next = jobRuns.Any(j => j.Status == JobRunStatus.Failed) ? RunStatus.Failed
                : jobRuns.Any(j => j.Status == JobRunStatus.Cancelled) ? RunStatus.Cancelled
                : RunStatus.Success;
        }
        else
        {
            next = RunStatus.Running;
        }

        if (run.Status == next && run.StartedAt is not null)
            return;

        run.Status = next;
        run.StartedAt ??= jobRuns.Where(j => j.StartedAt is not null).Select(j => j.StartedAt).Min() ?? run.CreatedAt;
        if (next is RunStatus.Success or RunStatus.Failed or RunStatus.Cancelled)
            run.FinishedAt = jobRuns.Where(j => j.FinishedAt is not null).Select(j => j.FinishedAt).Max();
        await repo.SaveRunTransitionAsync(run);
        await events.PublishRunUpdatedAsync(run);
        logger.LogInformation("Run {RunId} for workflow {Workflow} is now {Status}", run.Id, run.WorkflowName, next);
    }

}
