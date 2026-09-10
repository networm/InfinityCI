using InfinityCI.Core;
using InfinityCI.Grpc;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Runs;
using InfinityCI.Server.Storage;

namespace InfinityCI.Server.Agents;

/// <summary>
/// Build-side operations for remote (agent-executed) job runs: claiming pending
/// work with label matching, applying agent-reported log/step/finish updates,
/// and requeueing job runs orphaned by agent disconnects.
/// </summary>
public sealed class RemoteBuildCoordinator(
    IServiceScopeFactory scopeFactory,
    WorkflowStore workflowStore,
    JobLogStore logStore,
    RunEvents events,
    AgentRegistry registry,
    RunAggregator aggregator,
    Auth.CredentialStore credentialStore,
    ILogger<RemoteBuildCoordinator> logger)
{
    public Task PublishAgentsChangedAsync() => registry.PublishChangedAsync();

    /// <summary>Pull dispatch: claims the oldest still-queued pending job run that matches the agent.</summary>
    public async Task<bool> TryClaimNextPendingAsync(AgentConnection agent)
    {
        while (registry.TryDequeuePending(out var pending))
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();
            var jobRun = await repo.GetJobRunAsync(pending.JobRunId);
            if (jobRun is not { Status: JobRunStatus.Queued })
                continue; // cancelled or finalized while waiting in the queue

            if (pending.RequiredLabel is { } label
                && !agent.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                // This agent does not match; another one may. Hand it back.
                registry.RequeuePending(pending);
                return false;
            }

            var run = await repo.GetRunAsync(jobRun.RunId);
            var rawYaml = run is null ? null : workflowStore.TryGetRawYaml(run.WorkflowName);
            if (run is null || rawYaml is null)
            {
                jobRun.Status = JobRunStatus.Failed;
                jobRun.StartedAt = DateTimeOffset.UtcNow;
                jobRun.FinishedAt = DateTimeOffset.UtcNow;
                await repo.SaveJobRunTransitionAsync(jobRun);
                await logStore.AppendAndPublishAsync(events, jobRun.RunId, jobRun.JobKey, 0, "[server] workflow definition disappeared; marking job failed.");
                await events.PublishJobRunUpdatedAsync(jobRun);
                continue;
            }

            // Re-derive steps from the workflow so the UI and the agent agree on indexes.
            var workflow = WorkflowYaml.Parse(rawYaml);
            var job = workflow.Jobs.GetValueOrDefault(jobRun.JobKey);
            if (job is null)
            {
                jobRun.Status = JobRunStatus.Failed;
                jobRun.StartedAt = DateTimeOffset.UtcNow;
                jobRun.FinishedAt = DateTimeOffset.UtcNow;
                await repo.SaveJobRunTransitionAsync(jobRun);
                await logStore.AppendAndPublishAsync(events, jobRun.RunId, jobRun.JobKey, 0, "[server] job removed from workflow; marking failed.");
                await events.PublishJobRunUpdatedAsync(jobRun);
                continue;
            }
            jobRun.Steps = job.Steps.Select(s => new JobStepResult { Name = s.Name, Status = JobRunStatus.Pending }).ToList();
            jobRun.AgentId = agent.Id;
            await repo.SaveJobRunTransitionAsync(jobRun);
            await events.PublishJobRunUpdatedAsync(jobRun);

            registry.Assign(jobRun.Id, agent.Id);
            jobRun.AgentId = agent.Id;

            // Resolve SCM + credentials master-side so agents never see the
            // credential store, only the resolved values for this assignment.
            InfinityCI.Grpc.JobAssignment assignment;
            if (workflow.Scm is { } scm)
            {
                var credential = credentialStore.Resolve(scm.Credentials);
                assignment = new JobAssignment
                {
                    RunId = jobRun.RunId,
                    JobRunId = jobRun.Id,
                    JobKey = jobRun.JobKey,
                    WorkflowName = run.WorkflowName,
                    WorkflowYaml = rawYaml,
                    ScmUrl = scm.Url ?? "",
                    ScmBranch = scm.Branch ?? "",
                    ScmRef = scm.Ref ?? "",
                    ScmUsername = credential?.Username ?? "",
                    ScmPassword = credential?.Password ?? "",
                };
            }
            else
            {
                assignment = new JobAssignment
                {
                    RunId = jobRun.RunId,
                    JobRunId = jobRun.Id,
                    JobKey = jobRun.JobKey,
                    WorkflowName = run.WorkflowName,
                    WorkflowYaml = rawYaml,
                };
            }
            var sent = await registry.TrySendAsync(agent.Id, new MasterToAgent { Assignment = assignment });
            if (!sent)
            {
                // Connection died between reserve and send — drop the claim; the
                // stream-end handler requeues everything assigned to this agent.
                registry.ClearAssignment(jobRun.Id);
                registry.RequeuePending(pending);
                return false;
            }

            logger.LogInformation("Assigned job run {JobRunId} (run {RunId}, {Job}) to agent {Agent}",
                jobRun.Id, jobRun.RunId, jobRun.JobKey, agent.Name);
            return true;
        }
        return false;
    }

    /// <summary>Persists the branch/commit an agent checked out, for the dashboard.</summary>
    public async Task ApplyScmCheckoutAsync(ScmCheckout checkout)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();
        var jobRun = await repo.GetJobRunAsync(checkout.JobRunId);
        if (jobRun is null)
            return;
        jobRun.SourceBranch = checkout.Branch;
        jobRun.CommitSha = checkout.CommitSha;
        await repo.SaveJobRunTransitionAsync(jobRun);
        await events.PublishJobRunUpdatedAsync(jobRun);
    }

    public async Task AppendLogAsync(LogChunk chunk)
    {
        if (chunk.Offset != await logStore.GetEndLineAsync(chunk.RunId, chunk.JobKey))
        {
            logger.LogWarning("Log line-index mismatch for job run {JobRunId}: agent sent {Sent} (appending anyway)",
                chunk.JobRunId, chunk.Offset);
        }
        // The master-side log file is the single source of truth for line indexes.
        var line = await logStore.AppendAsync(chunk.RunId, chunk.JobKey, chunk.StepIndex, chunk.Text);
        await events.PublishLogAppendedAsync(new LogAppendedEventArgs(chunk.RunId, chunk.JobKey, line));
    }

    public async Task ApplyStepUpdateAsync(StepUpdate update)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();
        var jobRun = await repo.GetJobRunAsync(update.JobRunId);
        if (jobRun is null || update.StepIndex < 0 || update.StepIndex >= jobRun.Steps.Count)
        {
            logger.LogWarning("Ignoring step update for unknown job run {JobRunId} / step {Index}", update.JobRunId, update.StepIndex);
            return;
        }

        var step = jobRun.Steps[update.StepIndex];
        if (Enum.TryParse<JobRunStatus>(update.Status, ignoreCase: true, out var status))
            step.Status = status;
        step.ExitCode = update.HasExitCode ? update.ExitCode : null;
        step.StartLine = update.StartLine;
        step.EndLine = update.EndLine;
        if (status == JobRunStatus.Running && step.StartedAt is null)
            step.StartedAt = DateTimeOffset.UtcNow;
        if (status is JobRunStatus.Success or JobRunStatus.Failed or JobRunStatus.Cancelled)
            step.FinishedAt = DateTimeOffset.UtcNow;

        await repo.SaveJobRunTransitionAsync(jobRun);
        await events.PublishJobRunUpdatedAsync(jobRun);
    }

    public async Task FinalizeAsync(JobFinished finished)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();
        var jobRun = await repo.GetJobRunAsync(finished.JobRunId);
        if (jobRun is null)
            return;

        var status = Enum.TryParse<JobRunStatus>(finished.Status, ignoreCase: true, out var parsed)
            ? parsed
            : JobRunStatus.Failed;
        jobRun.Status = status;
        jobRun.StartedAt ??= jobRun.CreatedAt;
        jobRun.FinishedAt = DateTimeOffset.UtcNow;
        jobRun.ExitCode = finished.HasExitCode ? finished.ExitCode : null;
        // Steps the agent never reported (e.g. cancelled early) are marked skipped.
        foreach (var step in jobRun.Steps.Where(s => s.Status is JobRunStatus.Pending or JobRunStatus.Running))
        {
            step.Status = status == JobRunStatus.Cancelled ? JobRunStatus.Cancelled : JobRunStatus.Skipped;
            step.FinishedAt = DateTimeOffset.UtcNow;
        }

        await repo.SaveJobRunTransitionAsync(jobRun);
        await logStore.AppendAndPublishAsync(events, jobRun.RunId, jobRun.JobKey, 0, $"[server] job finished: {status}.");
        await events.PublishJobRunUpdatedAsync(jobRun);
        logger.LogInformation("Remote job run {JobRunId} finished: {Status}", jobRun.Id, status);

        // A remote job finishing may release dependents or finalize the run.
        await aggregator.OnJobRunTerminalAsync(jobRun);
    }

    /// <summary>Requeues job runs that were running on an agent that went offline.</summary>
    public async Task RequeueJobRunsAsync(IEnumerable<long> jobRunIds)
    {
        foreach (var jobRunId in jobRunIds)
            await RequeueOneAsync(jobRunId);
    }

    private async Task RequeueOneAsync(long jobRunId)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();
        var jobRun = await repo.GetJobRunAsync(jobRunId);
        if (jobRun is not { Status: JobRunStatus.Running })
            return;

        jobRun.Status = JobRunStatus.Queued;
        jobRun.StartedAt = null;
        jobRun.ExitCode = null;
        foreach (var step in jobRun.Steps)
        {
            step.Status = JobRunStatus.Pending;
            step.ExitCode = null;
            step.StartedAt = null;
            step.FinishedAt = null;
            step.StartLine = 0;
            step.EndLine = 0;
        }
        await repo.SaveJobRunTransitionAsync(jobRun);
        await logStore.AppendAndPublishAsync(events, jobRun.RunId, jobRun.JobKey, 0, "[server] agent disconnected; requeued the job.");
        await events.PublishJobRunUpdatedAsync(jobRun);
        registry.EnqueuePending(PendingFromRunsOn(jobRun.RunsOn, jobRunId));
    }

    public static PendingJobRun PendingFromRunsOn(string runsOn, long jobRunId)
    {
        var requiredLabel = runsOn.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) && runsOn.Length > "agent:".Length
            ? runsOn["agent:".Length..].Trim()
            : null;
        return new PendingJobRun(jobRunId, requiredLabel);
    }
}
