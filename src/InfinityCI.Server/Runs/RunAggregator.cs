using System.Collections.Concurrent;
using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Scm;
using InfinityCI.Server.Storage;

namespace InfinityCI.Server.Runs;

/// <summary>
/// Run status aggregation plus needs-based dispatch. After any job run reaches
/// a terminal state: succeeded jobs release their dependents, failed/cancelled
/// jobs cascade "skipped" to their transitive dependents — except jobs that
/// declared `if: always()`, which run once their needs are all terminal
/// (GitHub semantics) — and the run status is recomputed.
/// </summary>
public sealed class RunAggregator(
    IServiceScopeFactory scopeFactory,
    WorkflowStore workflowStore,
    RunEvents events,
    JobLogStore logStore,
    AgentRegistry registry,
    LocalJobRunQueue localQueue,
    CommitStatusReporter commitStatus,
    ILogger<RunAggregator> logger)
{
    // Evaluations mutate shared run state (cascade skips, dispatch); concurrent
    // evaluations for the same run would race their read-modify-write cycles.
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _runGates = new();

    public async Task OnJobRunTerminalAsync(JobRun jobRun)
    {
        if (!jobRun.Status.IsTerminal())
            return;
        await EvaluateRunAsync(jobRun.RunId);
    }

    /// <summary>Applies needs gating to all queued job runs of the run, then recomputes run status.</summary>
    public async Task EvaluateRunAsync(long runId)
    {
        var gate = _runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await EvaluateCoreAsync(runId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EvaluateCoreAsync(long runId)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();

        // Sibling job runs finalize concurrently; a single pass can observe a
        // snapshot that is already stale, so re-evaluate until nothing changes.
        for (var pass = 0; pass < 10; pass++)
        {
            var run = await repo.GetRunAsync(runId);
            if (run is null || run.IsTerminal)
                return;
            var changed = await EvaluatePassAsync(repo, runId);
            if (!changed)
                break;
        }

        await RecomputeAsync(runId);
    }

    /// <summary>One cascade+dispatch pass. Returns true when any job run changed.</summary>
    private async Task<bool> EvaluatePassAsync(RunRepository repo, long runId)
    {
        var run = await repo.GetRunAsync(runId);
        if (run is null)
            return false;
        var jobRuns = await repo.GetJobRunsAsync(runId);
        if (jobRuns.Count == 0)
            return false;

        var byKey = jobRuns.ToDictionary(j => j.JobKey, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        // Job definitions may have changed since the run was created; missing
        // ones behave like plain (non-always) jobs.
        var definitions = workflowStore.TryGet(run.WorkflowName)?.Jobs;
        var isAlways = new Func<string, bool>(key =>
            definitions?.GetValueOrDefault(key)?.RunAlways == true);

        // 1) Cascade "skipped" from failed/cancelled roots to transitive
        //    dependents; `if: always()` jobs are immune to the cascade.
        var pendingSkip = new Queue<string>(
            jobRuns.Where(j => j.Status is JobRunStatus.Failed or JobRunStatus.Cancelled).Select(j => j.JobKey));
        var poisoned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pendingSkip.Count > 0)
        {
            var failedKey = pendingSkip.Dequeue();
            if (!poisoned.Add(failedKey))
                continue;
            foreach (var dependent in jobRuns.Where(j =>
                         j.Status == JobRunStatus.Queued &&
                         j.Needs.Contains(failedKey, StringComparer.OrdinalIgnoreCase)))
            {
                if (isAlways(dependent.JobKey))
                    continue;
                dependent.Status = JobRunStatus.Skipped;
                dependent.FinishedAt = DateTimeOffset.UtcNow;
                await repo.SaveJobRunTransitionAsync(dependent);
                await events.PublishJobRunUpdatedAsync(dependent);
                await logStore.AppendAndPublishAsync(events, runId, dependent.JobKey, 0,
                    $"[server] job '{dependent.JobKey}' skipped: dependency '{failedKey}' did not succeed.");
                changed = true;
                pendingSkip.Enqueue(dependent.JobKey);
                logger.LogInformation("Job run {JobRunId} ({Job}) skipped due to failed dependency '{Dependency}'",
                    dependent.Id, dependent.JobKey, failedKey);
            }
        }

        // 2) Dispatch queued job runs: plain jobs need every dependency
        //    succeeded; `if: always()` jobs need every dependency terminal
        //    (success, failure, cancellation or skip all release them).
        foreach (var jobRun in jobRuns.Where(j => j.Status == JobRunStatus.Queued))
        {
            var always = isAlways(jobRun.JobKey);
            var satisfied = jobRun.Needs.All(need =>
                byKey.TryGetValue(need, out var dep)
                && (always ? dep.Status.IsTerminal() : dep.Status == JobRunStatus.Success));
            if (!satisfied)
                continue;

            bool dispatched;
            if (jobRun.RunsOn.StartsWith("agent", StringComparison.OrdinalIgnoreCase))
            {
                var requiredLabel = jobRun.RunsOn.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) && jobRun.RunsOn.Length > "agent:".Length
                    ? jobRun.RunsOn["agent:".Length..].Trim()
                    : null;
                dispatched = registry.EnqueuePending(new PendingJobRun(jobRun.Id, requiredLabel));
                if (dispatched)
                    logger.LogInformation("Dependencies met for job run {JobRunId} ({Job}); queued for agent dispatch", jobRun.Id, jobRun.JobKey);
            }
            else
            {
                dispatched = localQueue.TryWrite(jobRun);
                if (dispatched)
                    logger.LogInformation("Dependencies met for job run {JobRunId} ({Job}); queued locally", jobRun.Id, jobRun.JobKey);
            }
            changed |= dispatched;
        }

        return changed;
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
        {
            run.FinishedAt = jobRuns.Where(j => j.FinishedAt is not null).Select(j => j.FinishedAt).Max();
            TryReportCommitStatus(run, jobRuns);
        }
        await repo.SaveRunTransitionAsync(run);
        await events.PublishRunUpdatedAsync(run);
        logger.LogInformation("Run {RunId} for workflow {Workflow} is now {Status}", run.Id, run.WorkflowName, next);
    }

    /// <summary>Fire-and-forget commit status report for `commit_status: true` workflows.</summary>
    private void TryReportCommitStatus(Run run, List<JobRun> jobRuns)
    {
        try
        {
            var scm = workflowStore.TryGet(run.WorkflowName)?.Scm;
            if (scm is not { CommitStatus: true })
                return;
            var sha = jobRuns.FirstOrDefault(j => !string.IsNullOrEmpty(j.CommitSha))?.CommitSha;
            if (string.IsNullOrEmpty(sha))
                return;
            _ = commitStatus.ReportFinalAsync(scm, run.Id, run.WorkflowName, run.RunNumber, sha, run.Status);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report commit status for run {RunId}", run.Id);
        }
    }
}
