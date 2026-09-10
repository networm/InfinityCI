using InfinityCI.Core;
using InfinityCI.Server.Storage;

namespace InfinityCI.Server.Runs;

/// <summary>Derives run status from its job runs; safe to call after every transition.</summary>
public sealed class RunAggregator(IServiceScopeFactory scopeFactory, RunEvents events, ILogger<RunAggregator> logger)
{
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
