using System.Collections.Concurrent;
using System.Threading.Channels;
using InfinityCI.Core;
using Microsoft.EntityFrameworkCore;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Storage;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Runs;

/// <summary>
/// Triggering and scheduling: a trigger creates a run plus one job run per
/// workflow job; job runs are dispatched in parallel — local ones to the
/// embedded executor pool, agent ones to the pull-dispatch pending queue.
/// </summary>
public sealed class RunQueueService(
    IOptions<CiServerOptions> optionsAccessor,
    WorkflowStore workflowStore,
    JobLogStore logStore,
    RunEvents events,
    AgentRegistry agentRegistry,
    RunAggregator aggregator,
    JobRunExecutor executor,
    LocalJobRunQueue localQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<RunQueueService> logger) : BackgroundService
{
    private readonly CiServerOptions _options = optionsAccessor.Value;
    private readonly JobRunExecutor _executor = executor;
    private readonly LocalJobRunQueue _localQueue = localQueue;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();
    private readonly TaskCompletionSource _recoveryCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once startup recovery has finished; triggers before this may race recovery.</summary>
    public Task Ready => _recoveryCompleted.Task;

    public async Task<Run> TriggerAsync(string workflowName, string triggeredBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        var workflow = workflowStore.TryGet(workflowName) ?? throw new InvalidOperationException($"Unknown workflow '{workflowName}'.");

        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();

        var run = new Run
        {
            WorkflowName = workflow.Name,
            Project = workflow.Project,
            TriggeredBy = triggeredBy,
            Status = RunStatus.Running, // jobs are enqueued immediately below
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await repo.AddRunAsync(run, ct);

        // Phase 1: persist every job run BEFORE dispatching anything. A fast job
        // finishing while later job runs are still being created would let the
        // aggregator see a partial (all-terminal) view and finalize the run early.
        var created = new List<JobRun>(workflow.Jobs.Count);
        foreach (var (jobKey, job) in workflow.Jobs.OrderBy(j => j.Key, StringComparer.OrdinalIgnoreCase))
        {
            var jobRun = new JobRun
            {
                RunId = run.Id,
                JobKey = jobKey,
                RunsOn = job.RunsOn,
                Needs = job.Needs.ToList(),
                Status = JobRunStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                Steps = job.Steps.Select(s => new JobStepResult { Name = s.Name, Status = JobRunStatus.Pending }).ToList(),
            };
            await repo.AddJobRunAsync(jobRun, ct);
            created.Add(jobRun);
            await events.PublishJobRunUpdatedAsync(jobRun);
        }

        // Phase 2: dispatch under the aggregator's per-run lock — the single
        // dispatch path for needs-free and needs-released jobs alike.
        await aggregator.EvaluateRunAsync(run.Id);

        // NOTE: no trailing save here — fast jobs may already have finalized the
        // run via the aggregator; writing our in-memory copy would stomp it.
        await events.PublishRunUpdatedAsync(run);
        logger.LogInformation("Run {RunId} for workflow {Workflow} triggered by {User} with {Jobs} parallel job(s)",
            run.Id, workflow.Name, triggeredBy, workflow.Jobs.Count);
        return run;
    }

    private void Enqueue(JobRun jobRun, WorkflowJob job)
    {
        if (job.RunsOnAgent)
        {
            // Pull dispatch: a connected agent with a matching label picks it up.
            agentRegistry.EnqueuePending(new PendingJobRun(jobRun.Id, job.RequiredAgentLabel));
        }
        else
        {
            _localQueue.TryWrite(jobRun);
        }
    }

    /// <summary>Cancels a run: active job runs are killed/forwarded, queued ones marked cancelled.</summary>
    public async Task<bool> TryCancelRunAsync(long runId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();
        var jobRuns = await repo.GetJobRunsAsync(runId, ct);
        var hadOpen = false;

        foreach (var jobRun in jobRuns.Where(j => !j.IsTerminal))
        {
            hadOpen = true;
            if (_active.TryGetValue(jobRun.Id, out var cts))
            {
                cts.Cancel();
                continue;
            }
            if (await agentRegistry.TrySendCancelAsync(jobRun.Id))
                continue;

            // Still queued (local channel or pending agent queue): cancel outright.
            jobRun.Status = JobRunStatus.Cancelled;
            jobRun.FinishedAt = DateTimeOffset.UtcNow;
            foreach (var step in jobRun.Steps.Where(s => !s.Status.IsTerminal()))
                step.Status = JobRunStatus.Cancelled;
            await repo.SaveJobRunTransitionAsync(jobRun, ct);
            await events.PublishJobRunUpdatedAsync(jobRun);
            await logStore.AppendAndPublishAsync(events, jobRun.RunId, jobRun.JobKey, 0, "[server] job cancelled while queued.");
        }

        await aggregator.EvaluateRunAsync(runId);
        return hadOpen;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedJobRunsAsync(stoppingToken);
        _recoveryCompleted.TrySetResult();

        var workers = Enumerable.Range(0, Math.Max(1, _options.MaxConcurrentJobs))
            .Select(_ => WorkerLoopAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var channelJobRun in _localQueue.ReadAllAsync(stoppingToken))
        {
            // Atomic claim: a conditional UPDATE closes the double-dispatch and
            // cancel races (0 rows = the job run was finalized/skipped meanwhile).
            JobRun? jobRun;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
                var claimed = await db.JobRuns
                    .Where(j => j.Id == channelJobRun.Id
                                && (j.Status == JobRunStatus.Queued || j.Status == JobRunStatus.Running))
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(j => j.Status, JobRunStatus.Running)
                              .SetProperty(j => j.StartedAt, DateTimeOffset.UtcNow),
                        stoppingToken);
                if (claimed == 0)
                    continue;

                jobRun = await scope.ServiceProvider.GetRequiredService<RunRepository>()
                    .GetJobRunAsync(channelJobRun.Id, stoppingToken);
            }

            if (jobRun is null)
                continue;

            var (job, workflowScm) = await ResolveJobAsync(jobRun, stoppingToken);
            if (job is null)
            {
                jobRun.Status = JobRunStatus.Failed;
                jobRun.StartedAt = DateTimeOffset.UtcNow;
                jobRun.FinishedAt = DateTimeOffset.UtcNow;
                using (var scope = scopeFactory.CreateScope())
                {
                    await scope.ServiceProvider.GetRequiredService<RunRepository>().SaveJobRunTransitionAsync(jobRun, stoppingToken);
                }
                await logStore.AppendAndPublishAsync(events, jobRun.RunId, jobRun.JobKey, 0, "[server] workflow definition changed; marking job failed.");
                await events.PublishJobRunUpdatedAsync(jobRun);
                await aggregator.RecomputeAsync(jobRun.RunId);
                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _active[jobRun.Id] = cts;
            try
            {
                await _executor.ExecuteAsync(jobRun, job, workflowScm, cts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job run {JobRunId} crashed the worker loop", jobRun.Id);
            }
            finally
            {
                _active.TryRemove(jobRun.Id, out _);
                cts.Dispose();
            }

            await aggregator.OnJobRunTerminalAsync(jobRun);
        }
    }

    private async Task<(WorkflowJob?, ScmConfig?)> ResolveJobAsync(JobRun jobRun, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<RunRepository>().GetRunAsync(jobRun.RunId, ct);
        if (run is null)
            return (null, null);
        var workflow = workflowStore.TryGet(run.WorkflowName);
        return (workflow?.Jobs.GetValueOrDefault(jobRun.JobKey), workflow?.Scm);
    }

    /// <summary>Job runs left unfinished by a previous server run are requeued.</summary>
    private async Task RecoverInterruptedJobRunsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();

        // Heal runs stuck non-terminal despite terminal job runs (e.g. pre-fix bugs).
        foreach (var staleRun in await repo.ListNonTerminalRunsWithTerminalJobsAsync(ct))
        {
            var jobRuns = await repo.GetJobRunsAsync(staleRun.Id, ct);
            if (jobRuns.Count > 0 && jobRuns.All(j => j.IsTerminal))
                await aggregator.RecomputeAsync(staleRun.Id);
        }

        var unfinished = await repo.ListUnfinishedJobRunsAsync(ct);
        var affectedRuns = new HashSet<long>();

        foreach (var jobRun in unfinished)
        {
            var wasRunning = jobRun.Status == JobRunStatus.Running;
            jobRun.Status = JobRunStatus.Queued;
            jobRun.StartedAt = null;
            jobRun.ExitCode = null;
            foreach (var step in jobRun.Steps)
            {
                step.Status = JobRunStatus.Pending;
                step.ExitCode = null;
                step.StartedAt = null;
                step.FinishedAt = null;
            }
            await repo.SaveJobRunTransitionAsync(jobRun, ct);
            await events.PublishJobRunUpdatedAsync(jobRun);
            await logStore.AppendAndPublishAsync(events, jobRun.RunId, jobRun.JobKey, 0,
                wasRunning ? "[server] server restarted while the job was running; requeued." : "[server] requeued after server restart.");
            affectedRuns.Add(jobRun.RunId);
        }

        // Needs-aware dispatch: ready jobs enqueue, skip cascades apply, runs recompute.
        foreach (var runId in affectedRuns)
            await aggregator.EvaluateRunAsync(runId);
    }
}

public static class JobLogStoreExtensions
{
    public static async Task AppendAndPublishAsync(this JobLogStore store, RunEvents events, long runId, string jobKey, int stepIndex, string text)
    {
        var line = await store.AppendAsync(runId, jobKey, stepIndex, text);
        await events.PublishLogAppendedAsync(new LogAppendedEventArgs(runId, jobKey, line));
    }
}

public static class JobRunStatusExtensions
{
    public static bool IsTerminal(this JobRunStatus status) =>
        status is JobRunStatus.Success or JobRunStatus.Failed or JobRunStatus.Cancelled;
}
