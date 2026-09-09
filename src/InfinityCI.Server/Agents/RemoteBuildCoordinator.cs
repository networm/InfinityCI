using InfinityCI.Core;
using InfinityCI.Grpc;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Storage;

namespace InfinityCI.Server.Agents;

/// <summary>
/// Build-side operations for remote (agent-executed) builds: claiming pending
/// builds, applying agent-reported log/step/finish updates, and requeueing
/// builds orphaned by agent disconnects.
/// </summary>
public sealed class RemoteBuildCoordinator(
    IServiceScopeFactory scopeFactory,
    JobStore jobStore,
    BuildLogStore logStore,
    BuildEvents events,
    AgentRegistry registry,
    ILogger<RemoteBuildCoordinator> logger)
{
    public Task PublishAgentsChangedAsync() => registry.PublishChangedAsync();

    /// <summary>Pull dispatch: claims the oldest still-queued pending build for the agent.</summary>
    public async Task<bool> TryClaimNextPendingAsync(AgentConnection agent)
    {
        while (registry.TryDequeuePending(out var buildId))
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<BuildRepository>();
            var build = await repo.GetAsync(buildId);
            if (build is not { Status: BuildStatus.Queued })
                continue; // cancelled or finalized while waiting in the queue

            var job = jobStore.TryGet(build.JobName);
            var rawYaml = jobStore.TryGetRawYaml(build.JobName);
            if (job is null || rawYaml is null)
            {
                build.Status = BuildStatus.Failed;
                build.StartedAt = DateTimeOffset.UtcNow;
                build.FinishedAt = DateTimeOffset.UtcNow;
                await repo.SaveTransitionAsync(build);
                await logStore.AppendAsync(build.Id, "[server] job definition disappeared; marking failed.");
                await events.PublishBuildUpdatedAsync(build);
                continue;
            }

            // Pre-create pending steps so the UI and the agent agree on indexes.
            build.Steps = job.Steps.Select(s => new BuildStepResult { Name = s.Name, Status = BuildStepStatus.Pending }).ToList();
            await repo.SaveTransitionAsync(build);
            await events.PublishBuildUpdatedAsync(build);

            registry.Assign(build.Id, agent.Id);
            var sent = await registry.TrySendAsync(agent.Id, new MasterToAgent
            {
                Assignment = new JobAssignment
                {
                    BuildId = build.Id,
                    JobName = build.JobName,
                    JobYaml = rawYaml,
                },
            });
            if (!sent)
            {
                // Connection died between reserve and send — drop the claim; the
                // stream-end handler requeues everything assigned to this agent.
                registry.ClearAssignment(build.Id);
                registry.RequeuePending(build.Id);
                return false;
            }

            logger.LogInformation("Assigned build {BuildId} ({Job}) to agent {Agent}", build.Id, build.JobName, agent.Name);
            return true;
        }
        return false;
    }

    public async Task AppendLogAsync(LogChunk chunk)
    {
        var expected = await logStore.GetEndOffsetAsync(chunk.BuildId);
        if (chunk.Offset != expected)
        {
            logger.LogWarning("Log offset mismatch for build {BuildId}: agent sent {Sent}, master expected {Expected} (appending anyway)",
                chunk.BuildId, chunk.Offset, expected);
        }
        // The master-side log file is the single source of truth for offsets.
        await logStore.AppendAsync(chunk.BuildId, chunk.Text);
    }

    public async Task ApplyStepUpdateAsync(StepUpdate update)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<BuildRepository>();
        var build = await repo.GetAsync(update.BuildId);
        if (build is null || update.StepIndex < 0 || update.StepIndex >= build.Steps.Count)
        {
            logger.LogWarning("Ignoring step update for unknown build {BuildId} / step {Index}", update.BuildId, update.StepIndex);
            return;
        }

        var step = build.Steps[update.StepIndex];
        if (Enum.TryParse<BuildStepStatus>(update.Status, ignoreCase: true, out var status))
            step.Status = status;
        step.ExitCode = update.HasExitCode ? update.ExitCode : null;
        step.StartOffset = update.StartOffset;
        step.EndOffset = update.EndOffset;
        if (status == BuildStepStatus.Running && step.StartedAt is null)
            step.StartedAt = DateTimeOffset.UtcNow;
        if (status is BuildStepStatus.Success or BuildStepStatus.Failed or BuildStepStatus.Cancelled)
            step.FinishedAt = DateTimeOffset.UtcNow;

        await repo.SaveTransitionAsync(build);
        await events.PublishBuildUpdatedAsync(build);
    }

    public async Task FinalizeAsync(BuildFinished finished)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<BuildRepository>();
        var build = await repo.GetAsync(finished.BuildId);
        if (build is null)
            return;

        var status = Enum.TryParse<BuildStatus>(finished.Status, ignoreCase: true, out var parsed)
            ? parsed
            : BuildStatus.Failed;
        build.Status = status;
        build.StartedAt ??= build.CreatedAt;
        build.FinishedAt = DateTimeOffset.UtcNow;
        build.ExitCode = finished.HasExitCode ? finished.ExitCode : null;
        // Steps the agent never reported (e.g. cancelled early) are marked skipped.
        foreach (var step in build.Steps.Where(s => s.Status is BuildStepStatus.Pending or BuildStepStatus.Running))
        {
            step.Status = status == BuildStatus.Cancelled ? BuildStepStatus.Cancelled : BuildStepStatus.Skipped;
            step.FinishedAt = DateTimeOffset.UtcNow;
        }

        await repo.SaveTransitionAsync(build);
        await logStore.AppendAsync(build.Id, $"[server] build finished: {status}.");
        await events.PublishBuildUpdatedAsync(build);
        logger.LogInformation("Remote build {BuildId} finished: {Status}", build.Id, status);
    }

    /// <summary>Requeues builds that were running on an agent that went offline.</summary>
    public async Task RequeueBuildsAsync(IEnumerable<long> buildIds)
    {
        foreach (var buildId in buildIds)
            await RequeueOneAsync(buildId);
    }

    private async Task RequeueOneAsync(long buildId)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<BuildRepository>();
        var build = await repo.GetAsync(buildId);
        if (build is not { Status: BuildStatus.Running })
            return;

        build.Status = BuildStatus.Queued;
        build.StartedAt = null;
        build.FinishedAt = null;
        build.ExitCode = null;
        foreach (var step in build.Steps)
        {
            step.Status = BuildStepStatus.Pending;
            step.ExitCode = null;
            step.StartedAt = null;
            step.FinishedAt = null;
            step.StartOffset = 0;
            step.EndOffset = 0;
        }
        await repo.SaveTransitionAsync(build);
        await logStore.AppendAsync(buildId, "[server] agent disconnected; requeued the build.");
        await events.PublishBuildUpdatedAsync(build);
        registry.EnqueuePending(buildId);
    }
}
