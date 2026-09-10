using System.Diagnostics;
using InfinityCI.Core;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Storage;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Runs;

/// <summary>
/// Executes one job run locally: runs its steps as child processes, appends
/// timestamped lines into the job's JSONL log, and reports every step and
/// terminal transition. Line indexes are the log cursor used by clients.
/// </summary>
public sealed class JobRunExecutor(
    IOptions<CiServerOptions> optionsAccessor,
    JobLogStore logStore,
    RunEvents events,
    CredentialStore credentialStore,
    IServiceScopeFactory scopeFactory,
    ILogger<JobRunExecutor> logger)
{
    private readonly CiServerOptions _options = optionsAccessor.Value;

    public async Task<JobRunStatus> ExecuteAsync(JobRun jobRun, WorkflowJob job, ScmConfig? workflowScm, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<RunRepository>();

        // Fresh in-memory copy tracked by this executor (persisted on transitions).
        jobRun.Status = JobRunStatus.Running;
        jobRun.StartedAt = DateTimeOffset.UtcNow;
        await repo.SaveJobRunTransitionAsync(jobRun);
        await events.PublishJobRunUpdatedAsync(jobRun);

        var workspace = Path.Combine(_options.WorkspacesDir, $"{jobRun.RunId}-{JobLogStore.SanitizeJobKey(jobRun.JobKey)}");
        Directory.CreateDirectory(workspace);

        // SCM checkout: steps run inside the working copy when the workflow
        // declares an scm block. Failures fail the job like a failed step.
        if (workflowScm is { } scm)
        {
            try
            {
                await Append(jobRun, 0, $"[server] checking out {scm.Url}...");
                var credential = credentialStore.Resolve(scm.Credentials);
                var checkout = GitSourceFetcher.Fetch(scm, workspace, credential, line => Append(jobRun, 0, line).GetAwaiter().GetResult());
                jobRun.SourceBranch = checkout.Branch;
                jobRun.CommitSha = checkout.CommitSha;
                await repo.SaveJobRunTransitionAsync(jobRun, CancellationToken.None);
                await events.PublishJobRunUpdatedAsync(jobRun);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await Append(jobRun, 0, $"[server] checkout failed: {ex.Message}");
                jobRun.Status = JobRunStatus.Failed;
                jobRun.FinishedAt = DateTimeOffset.UtcNow;
                await PersistAsync(repo, jobRun, CancellationToken.None);
                return JobRunStatus.Failed;
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var overall = JobRunStatus.Success;
        try
        {
            await Append(jobRun, 0, $"[server] job '{jobRun.JobKey}' of run {jobRun.RunId} started on {Environment.MachineName}.");

            for (var i = 0; i < job.Steps.Count; i++)
            {
                var step = job.Steps[i];
                // Steps were pre-created (Pending) at trigger time — update in place.
                var result = jobRun.Steps[i];
                result.Status = JobRunStatus.Running;
                result.StartedAt = DateTimeOffset.UtcNow;
                result.StartLine = await logStore.GetEndLineAsync(jobRun.RunId, jobRun.JobKey);
                await PersistAsync(repo, jobRun, cts.Token);

                var exitCode = await RunStepAsync(jobRun, job, step, workspace, i, cts.Token);

                result.ExitCode = exitCode;
                result.FinishedAt = DateTimeOffset.UtcNow;
                result.EndLine = await logStore.GetEndLineAsync(jobRun.RunId, jobRun.JobKey);

                if (cts.IsCancellationRequested)
                {
                    result.Status = JobRunStatus.Cancelled;
                    overall = JobRunStatus.Cancelled;
                }
                else if (exitCode == 0)
                {
                    result.Status = JobRunStatus.Success;
                }
                else if (step.ContinueOnError)
                {
                    result.Status = JobRunStatus.Failed;
                    await Append(jobRun, i, $"[server] step '{step.Name}' failed with exit code {exitCode}; continuing (continue_on_error).");
                }
                else
                {
                    result.Status = JobRunStatus.Failed;
                    overall = JobRunStatus.Failed;
                    for (var j = i + 1; j < job.Steps.Count; j++)
                        jobRun.Steps[j].Status = JobRunStatus.Skipped;
                    await Append(jobRun, i, $"[server] step '{step.Name}' failed with exit code {exitCode}; skipping remaining steps.");
                }

                await PersistAsync(repo, jobRun, cts.Token);
                if (overall is JobRunStatus.Failed or JobRunStatus.Cancelled)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            overall = JobRunStatus.Cancelled;
            var running = jobRun.Steps.LastOrDefault(s => s.Status == JobRunStatus.Running);
            if (running is not null)
            {
                running.Status = JobRunStatus.Cancelled;
                running.FinishedAt = DateTimeOffset.UtcNow;
                running.EndLine = await logStore.GetEndLineAsync(jobRun.RunId, jobRun.JobKey);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job run {JobRunId} failed unexpectedly", jobRun.Id);
            overall = JobRunStatus.Failed;
            await Append(jobRun, 0, $"[server] internal error: {ex.Message}");
        }

        jobRun.Status = overall;
        jobRun.FinishedAt = DateTimeOffset.UtcNow;
        jobRun.ExitCode = overall switch
        {
            JobRunStatus.Success => 0,
            JobRunStatus.Failed => jobRun.Steps.LastOrDefault(s => s.Status == JobRunStatus.Failed && s.ExitCode is not null)?.ExitCode,
            _ => null,
        };
        await PersistAsync(repo, jobRun, CancellationToken.None);
        await Append(jobRun, 0, $"[server] job finished: {overall}.");
        await events.PublishJobRunUpdatedAsync(jobRun);
        return overall;

        async Task PersistAsync(RunRepository repository, JobRun jr, CancellationToken ct)
        {
            await repository.SaveJobRunTransitionAsync(jr, ct);
            await events.PublishJobRunUpdatedAsync(jr);
        }
    }

    private async Task Append(JobRun jobRun, int stepIndex, string text)
    {
        var line = await logStore.AppendAsync(jobRun.RunId, jobRun.JobKey, stepIndex, text);
        await events.PublishLogAppendedAsync(new LogAppendedEventArgs(jobRun.RunId, jobRun.JobKey, line));
    }

    private async Task<int> RunStepAsync(JobRun jobRun, WorkflowJob job, JobStep step, string workspace, int stepIndex, CancellationToken ct)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in job.Environment)
            env[key] = value;
        foreach (var (key, value) in step.Environment)
            env[key] = value;
        env["CI"] = "true";
        env["INFINITY_RUN_ID"] = jobRun.RunId.ToString();
        env["INFINITY_JOB_KEY"] = jobRun.JobKey;

        var psi = ShellResolver.CreateStartInfo(step.Command, step.Shell, workspace, env);
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close(); // children see EOF on stdin instead of the server's console

        var stdout = PumpOutputAsync(process.StandardOutput, jobRun, stepIndex);
        var stderr = PumpOutputAsync(process.StandardError, jobRun, stepIndex);

        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill process tree for job run {JobRunId}", jobRun.Id);
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

    private async Task PumpOutputAsync(StreamReader reader, JobRun jobRun, int stepIndex)
    {
        // No cancellation token: the stream ends when the (possibly killed) process closes it.
        while (await reader.ReadLineAsync() is { } line)
            await Append(jobRun, stepIndex, line);
    }
}
