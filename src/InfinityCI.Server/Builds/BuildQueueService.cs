using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Storage;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Builds;

/// <summary>
/// Queues triggered builds, runs job steps as child processes, streams output
/// into the offset-based log store, and publishes every state transition.
/// Jobs with runs_on: agent are routed to the agent pending queue instead of
/// the local executor.
/// </summary>
public sealed class BuildQueueService(
    IOptions<CiServerOptions> optionsAccessor,
    JobStore jobStore,
    BuildLogStore logStore,
    BuildEvents events,
    AgentRegistry agentRegistry,
    IServiceScopeFactory scopeFactory,
    ILogger<BuildQueueService> logger) : BackgroundService
{
    private readonly CiServerOptions _options = optionsAccessor.Value;
    private readonly Channel<Build> _queue = Channel.CreateUnbounded<Build>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();
    private readonly TaskCompletionSource _recoveryCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once startup recovery has finished; triggers before this may race recovery.</summary>
    public Task Ready => _recoveryCompleted.Task;

    public async Task<Build> TriggerAsync(string jobName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        var job = jobStore.TryGet(jobName) ?? throw new InvalidOperationException($"Unknown job '{jobName}'.");

        var build = new Build
        {
            JobName = job.Name,
            Status = BuildStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using (var scope = scopeFactory.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BuildRepository>().AddAsync(build, ct);
        }

        if (job.RunsOnAgent)
        {
            // Pull dispatch: a connected agent with a free slot picks it up.
            agentRegistry.EnqueuePending(build.Id);
        }
        else
        {
            await _queue.Writer.WriteAsync(build, ct);
        }
        await events.PublishBuildUpdatedAsync(build);
        logger.LogInformation("Queued build {BuildId} for job {Job} ({Target})", build.Id, job.Name,
            job.RunsOnAgent ? "agent" : "local");
        return build;
    }

    /// <summary>Cancels a running build (kills the process tree) or a queued build. Never leaves one stuck.</summary>
    public async Task<bool> TryCancelAsync(long buildId, CancellationToken ct = default)
    {
        if (_active.TryGetValue(buildId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        // Builds running on a remote agent are cancelled through its stream.
        if (await agentRegistry.TrySendCancelAsync(buildId))
            return true;

        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<BuildRepository>();
        var build = await repo.GetAsync(buildId, ct);
        if (build is not { Status: BuildStatus.Queued })
            return false;

        build.Status = BuildStatus.Cancelled;
        build.FinishedAt = DateTimeOffset.UtcNow;
        await repo.SaveTransitionAsync(build, ct);
        await events.PublishBuildUpdatedAsync(build);
        logger.LogInformation("Cancelled queued build {BuildId}", buildId);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverInterruptedBuildsAsync(stoppingToken);
        }
        finally
        {
            _recoveryCompleted.TrySetResult();
        }

        var workers = Enumerable.Range(0, Math.Max(1, _options.MaxConcurrentBuilds))
            .Select(_ => WorkerLoopAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var channelBuild in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // The channel copy can be stale (e.g. cancelled while queued, or a
            // duplicate from recovery racing a trigger) — the DB is authoritative.
            Build? build;
            using (var scope = scopeFactory.CreateScope())
            {
                build = await scope.ServiceProvider.GetRequiredService<BuildRepository>()
                    .GetAsync(channelBuild.Id, stoppingToken);
            }

            if (build is not { Status: BuildStatus.Queued })
                continue;

            try
            {
                await RunBuildAsync(build, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Build {BuildId} crashed the worker loop", build.Id);
            }
        }
    }

    /// <summary>Builds left unfinished by a previous server run: re-enqueue queued, fail running.</summary>
    private async Task RecoverInterruptedBuildsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<BuildRepository>();
        var unfinished = await repo.ListUnfinishedAsync(ct);

        foreach (var build in unfinished)
        {
            if (build.Status == BuildStatus.Queued)
            {
                if (jobStore.TryGet(build.JobName)?.RunsOnAgent == true)
                    agentRegistry.EnqueuePending(build.Id);
                else
                    await _queue.Writer.WriteAsync(build, ct);
                logger.LogInformation("Re-enqueued build {BuildId} left queued by a previous run", build.Id);
            }
            else
            {
                build.Status = BuildStatus.Failed;
                build.FinishedAt = DateTimeOffset.UtcNow;
                await repo.SaveTransitionAsync(build, ct);
                await logStore.AppendAsync(build.Id, "[server] server restarted while the build was running; marked as failed.", ct);
                await events.PublishBuildUpdatedAsync(build);
                logger.LogWarning("Marked orphaned running build {BuildId} as failed", build.Id);
            }
        }
    }

    private async Task RunBuildAsync(Build build, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<BuildRepository>();

        var job = jobStore.TryGet(build.JobName);
        if (job is null)
        {
            build.Status = BuildStatus.Failed;
            build.StartedAt = DateTimeOffset.UtcNow;
            build.FinishedAt = DateTimeOffset.UtcNow;
            await repo.SaveTransitionAsync(build, stoppingToken);
            await logStore.AppendAsync(build.Id, $"[server] job '{build.JobName}' no longer exists.", CancellationToken.None);
            await events.PublishBuildUpdatedAsync(build);
            return;
        }

        build.Status = BuildStatus.Running;
        build.StartedAt = DateTimeOffset.UtcNow;
        await repo.SaveTransitionAsync(build, stoppingToken);
        await events.PublishBuildUpdatedAsync(build);

        var workspace = Path.Combine(_options.WorkspacesDir, build.Id.ToString());
        Directory.CreateDirectory(workspace);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _active[build.Id] = cts;
        var overall = BuildStatus.Success;
        try
        {
            await logStore.AppendAsync(build.Id, $"[server] build {build.Id} for job '{job.Name}' started on {Environment.MachineName}.", CancellationToken.None);

            for (var i = 0; i < job.Steps.Count; i++)
            {
                var step = job.Steps[i];
                var result = new BuildStepResult
                {
                    Name = step.Name,
                    Status = BuildStepStatus.Running,
                    StartedAt = DateTimeOffset.UtcNow,
                    StartOffset = await logStore.GetEndOffsetAsync(build.Id),
                };
                build.Steps.Add(result);
                await repo.SaveTransitionAsync(build, cts.Token);
                await events.PublishBuildUpdatedAsync(build);

                var exitCode = await RunStepAsync(build, job, step, workspace, cts.Token);

                result.ExitCode = exitCode;
                result.FinishedAt = DateTimeOffset.UtcNow;
                result.EndOffset = await logStore.GetEndOffsetAsync(build.Id);

                if (cts.IsCancellationRequested)
                {
                    result.Status = BuildStepStatus.Cancelled;
                    overall = BuildStatus.Cancelled;
                }
                else if (exitCode == 0)
                {
                    result.Status = BuildStepStatus.Success;
                }
                else if (step.ContinueOnError)
                {
                    result.Status = BuildStepStatus.Failed;
                    await logStore.AppendAsync(build.Id, $"[server] step '{step.Name}' failed with exit code {exitCode}; continuing (continue_on_error).", CancellationToken.None);
                }
                else
                {
                    result.Status = BuildStepStatus.Failed;
                    overall = BuildStatus.Failed;
                    for (var j = i + 1; j < job.Steps.Count; j++)
                        build.Steps.Add(new BuildStepResult { Name = job.Steps[j].Name, Status = BuildStepStatus.Skipped });
                    await logStore.AppendAsync(build.Id, $"[server] step '{step.Name}' failed with exit code {exitCode}; skipping remaining steps.", CancellationToken.None);
                }

                await repo.SaveTransitionAsync(build, cts.Token);
                await events.PublishBuildUpdatedAsync(build);

                if (overall is BuildStatus.Failed or BuildStatus.Cancelled)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            overall = BuildStatus.Cancelled;
            var running = build.Steps.LastOrDefault(s => s.Status == BuildStepStatus.Running);
            if (running is not null)
            {
                running.Status = BuildStepStatus.Cancelled;
                running.FinishedAt = DateTimeOffset.UtcNow;
                running.EndOffset = await logStore.GetEndOffsetAsync(build.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Build {BuildId} failed unexpectedly", build.Id);
            overall = BuildStatus.Failed;
            await logStore.AppendAsync(build.Id, $"[server] internal error: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            _active.TryRemove(build.Id, out _);
            cts.Dispose();
            build.Status = overall;
            build.FinishedAt = DateTimeOffset.UtcNow;
            build.ExitCode = overall switch
            {
                BuildStatus.Success => 0,
                BuildStatus.Failed => build.Steps.LastOrDefault(s => s.Status == BuildStepStatus.Failed && s.ExitCode is not null)?.ExitCode,
                _ => null,
            };

            try
            {
                await repo.SaveTransitionAsync(build, CancellationToken.None);
                await logStore.AppendAsync(build.Id, $"[server] build finished: {overall}.", CancellationToken.None);
                await events.PublishBuildUpdatedAsync(build);
                logger.LogInformation("Build {BuildId} for job {Job} finished: {Status}", build.Id, build.JobName, overall);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to finalize build {BuildId}", build.Id);
            }
        }
    }

    private async Task<int> RunStepAsync(Build build, JobDefinition job, JobStep step, string workspace, CancellationToken ct)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in job.Environment)
            env[key] = value;
        foreach (var (key, value) in step.Environment)
            env[key] = value;
        env["CI"] = "true";
        env["INFINITY_BUILD_ID"] = build.Id.ToString();
        env["INFINITY_JOB_NAME"] = job.Name;

        var psi = ShellResolver.CreateStartInfo(step.Command, step.Shell, workspace, env);
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close(); // children see EOF on stdin instead of inheriting the server's console

        var stdout = PumpOutputAsync(process.StandardOutput, build.Id);
        var stderr = PumpOutputAsync(process.StandardError, build.Id);

        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill process tree for build {BuildId}", build.Id);
            }
        });

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation killed the process; drain remaining output, then propagate.
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
                // already dead
            }
            await Task.WhenAll(stdout, stderr);
            throw;
        }

        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private async Task PumpOutputAsync(StreamReader reader, long buildId)
    {
        // No cancellation token: the stream ends when the (possibly killed) process closes it.
        while (await reader.ReadLineAsync() is { } line)
            await logStore.AppendAsync(buildId, line);
    }
}
