using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using InfinityCI.Core;
using InfinityCI.Grpc;

namespace InfinityCI.Agent;

/// <summary>
/// Executes one assigned job run locally: runs the workflow job's steps as
/// child processes and streams logs/steps/finish back to the master over the
/// agent's outgoing message channel. Line offsets are line indexes; the
/// master stamps timestamps and owns the log file.
/// </summary>
public sealed class RemoteBuildRunner(
    AgentOptions options,
    AgentOutgoing outgoing,
    ConcurrentDictionary<long, CancellationTokenSource> cancellations,
    ILogger<RemoteBuildRunner> logger)
{
    public async Task RunAsync(JobAssignment assignment)
    {
        var jobRunId = assignment.JobRunId;
        var cts = new CancellationTokenSource();
        cancellations[jobRunId] = cts;

        try
        {
            var workflow = WorkflowYaml.Parse(assignment.WorkflowYaml);
            var job = workflow.Jobs.GetValueOrDefault(assignment.JobKey)
                ?? throw new InvalidOperationException($"Job '{assignment.JobKey}' not in workflow '{workflow.Name}'.");
            var jobKey = assignment.JobKey;

            // Working-directory override from the workflow's runtime state:
            // absolute paths are used as-is, relative ones resolve under the
            // agent's data directory. Empty = per-run isolated workspace.
            string workspace;
            if (string.IsNullOrWhiteSpace(assignment.Workspace))
            {
                workspace = Path.Combine(options.WorkspacesDir, $"{assignment.RunId}-{Sanitize(jobKey)}");
            }
            else
            {
                workspace = Path.IsPathRooted(assignment.Workspace)
                    ? assignment.Workspace
                    : Path.GetFullPath(Path.Combine(options.DataDir, assignment.Workspace));
            }

            try
            {
                Directory.CreateDirectory(workspace);
            }
            catch (Exception ex)
            {
                await SendLog(assignment.RunId, jobRunId, jobKey, 0, 0,
                    $"[agent {Environment.MachineName}] workspace '{workspace}' is not usable: {ex.Message}");
                throw;
            }

            var intro = $"[agent {Environment.MachineName}] job '{jobKey}' of run {assignment.RunId} started.";
            await SendLog(assignment.RunId, jobRunId, jobKey, 0, 0, intro);
            var cursor = new LineCursor(1); // line 0 = intro
            if (!string.IsNullOrWhiteSpace(assignment.Workspace))
                await SendLog(assignment.RunId, jobRunId, jobKey, 0, cursor.Take(),
                    $"[agent {Environment.MachineName}] working directory override: {workspace}");

            // SCM checkout: mirror the master-side executor behavior.
            if (assignment.ScmUrl is { Length: > 0 } scmUrl)
            {
                var scm = new ScmConfig
                {
                    Url = scmUrl,
                    Branch = NullIfEmpty(assignment.ScmBranch),
                    Ref = NullIfEmpty(assignment.ScmRef),
                };
                var credential = string.IsNullOrEmpty(assignment.ScmUsername)
                    ? null
                    : new GitCredential(assignment.ScmUsername, assignment.ScmPassword);
                var checkout = GitSourceFetcher.Fetch(scm, workspace, credential,
                    line => SendLog(assignment.RunId, jobRunId, jobKey, 0, cursor.Take(), line).GetAwaiter().GetResult());
                await outgoing.SendAsync(new AgentToMaster
                {
                    ScmCheckout = new ScmCheckout
                    {
                        RunId = assignment.RunId,
                        JobRunId = jobRunId,
                        JobKey = jobKey,
                        Branch = checkout.Branch,
                        CommitSha = checkout.CommitSha,
                    },
                });
            }

            var overall = JobRunStatus.Success;
            var lastExitCode = 0;

            // Job-level timeout; user cancellation (cts) stays distinguishable.
            using var timeoutSource = new CancellationTokenSource();
            if (job.Timeout is { } jobTimeout)
                timeoutSource.CancelAfter(jobTimeout);
            using var stepBase = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutSource.Token);

            for (var i = 0; i < job.Steps.Count; i++)
            {
                var step = job.Steps[i];
                var startLine = cursor.Value;
                await SendStep(assignment.RunId, jobRunId, jobKey, i, JobRunStatus.Running, startLine, startLine);

                var exitCode = -1;
                var timedOut = false;
                var maxAttempts = Math.Max(1, step.Retry + 1);
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    if (attempt > 1)
                        await SendLog(assignment.RunId, jobRunId, jobKey, i, cursor.Take(),
                            $"[agent] attempt {attempt - 1}/{maxAttempts} failed; retrying (attempt {attempt}/{maxAttempts}).");

                    // Per-step timeout overrides the job-level one when set.
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(stepBase.Token);
                    if (step.Timeout is { } stepTimeout)
                        stepCts.CancelAfter(stepTimeout);

                    try
                    {
                        exitCode = await RunStepAsync(assignment, job, step, workspace, stepCts.Token, cursor, i);
                        timedOut = false;
                    }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                    {
                        // A timeout fired (step or job level) — not a user cancel.
                        timedOut = true;
                        exitCode = -1;
                    }

                    if (!timedOut && exitCode == 0)
                        break;
                    if (timedOut || attempt == maxAttempts)
                        break;
                    if (cts.IsCancellationRequested || timeoutSource.IsCancellationRequested)
                        break;
                }

                var failed = timedOut || exitCode != 0;
                lastExitCode = exitCode;
                var stepStatus = cts.IsCancellationRequested && !timedOut
                    ? JobRunStatus.Cancelled
                    : failed ? JobRunStatus.Failed : JobRunStatus.Success;
                await SendStep(assignment.RunId, jobRunId, jobKey, i, stepStatus, startLine, cursor.Value, exitCode);

                if (timedOut)
                {
                    await SendLog(assignment.RunId, jobRunId, jobKey, 0, cursor.Take(),
                        $"[agent] step '{step.Name}' timed out; killed.");
                }

                if (cts.IsCancellationRequested && !timedOut)
                {
                    overall = JobRunStatus.Cancelled;
                    break;
                }
                if (failed && !step.ContinueOnError)
                {
                    overall = JobRunStatus.Failed;
                    break;
                }
            }

            await Send(new AgentToMaster
            {
                JobFinished = new JobFinished
                {
                    RunId = assignment.RunId,
                    JobRunId = jobRunId,
                    JobKey = jobKey,
                    Status = overall.ToString(),
                    ExitCode = overall == JobRunStatus.Success ? 0 : lastExitCode,
                },
            });
            logger.LogInformation("Job run {JobRunId} (run {RunId}, {Job}) finished: {Status}",
                jobRunId, assignment.RunId, jobKey, overall);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await Send(new AgentToMaster
            {
                JobFinished = new JobFinished
                {
                    RunId = assignment.RunId,
                    JobRunId = jobRunId,
                    JobKey = assignment.JobKey,
                    Status = nameof(JobRunStatus.Cancelled),
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job run {JobRunId} crashed the runner", jobRunId);
            await Send(new AgentToMaster
            {
                JobFinished = new JobFinished
                {
                    RunId = assignment.RunId,
                    JobRunId = jobRunId,
                    JobKey = assignment.JobKey,
                    Status = nameof(JobRunStatus.Failed),
                },
            });
        }
        finally
        {
            cancellations.TryRemove(jobRunId, out _);
            cts.Dispose();
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    public bool TryCancel(long jobRunId)
    {
        if (!cancellations.TryGetValue(jobRunId, out var cts))
            return false;
        cts.Cancel();
        return true;
    }

    private sealed class LineCursor(long initial)
    {
        private long _value = initial;
        public long Value => Interlocked.Read(ref _value);
        public long Take() => Interlocked.Increment(ref _value) - 1;
    }

    private async Task<int> RunStepAsync(
        JobAssignment assignment, WorkflowJob job, JobStep step, string workspace,
        CancellationToken ct, LineCursor cursor, int stepIndex)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Agent-wide variables first (lowest priority) so workflow env, run
        // parameters and step env can all override them.
        var agentEnv = string.IsNullOrEmpty(assignment.EnvJson)
            ? new Dictionary<string, string>()
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(assignment.EnvJson) ?? [];
        foreach (var (key, value) in agentEnv)
            env[key] = value;
        foreach (var (key, value) in job.Environment)
            env[key] = value;
        var runParams = string.IsNullOrEmpty(assignment.ParamsJson)
            ? new Dictionary<string, string>()
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(assignment.ParamsJson) ?? [];
        foreach (var (key, value) in runParams)
            env[key] = value;
        foreach (var (key, value) in step.Environment)
            env[key] = value;
        env["CI"] = "true";
        env["INFINITY_RUN_ID"] = assignment.RunId.ToString();
        env["INFINITY_JOB_KEY"] = assignment.JobKey;

        var psi = ShellResolver.CreateStartInfo(step.Command, step.Shell, workspace, env);
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close(); // children see EOF on stdin instead of the agent's console

        var pumpOut = PumpAsync(process.StandardOutput, assignment, cursor, stepIndex);
        var pumpErr = PumpAsync(process.StandardError, assignment, cursor, stepIndex);

        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill process tree for job run {JobRunId}", assignment.JobRunId);
            }
        });

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
                // already dead
            }
            await Task.WhenAll(pumpOut, pumpErr);
            throw;
        }

        await Task.WhenAll(pumpOut, pumpErr);
        return process.ExitCode;
    }

    private async Task PumpAsync(
        StreamReader reader, JobAssignment assignment, LineCursor cursor, int stepIndex)
    {
        // No cancellation token: the stream ends when the (possibly killed) process closes it.
        while (await reader.ReadLineAsync() is { } text)
            await SendLog(assignment.RunId, assignment.JobRunId, assignment.JobKey, stepIndex, cursor.Take(), text);
    }

    private static string Sanitize(string jobKey)
    {
        var chars = jobKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    private Task SendLog(long runId, long jobRunId, string jobKey, int stepIndex, long line, string text) =>
        outgoing.SendAsync(new AgentToMaster
        {
            LogChunk = new LogChunk
            {
                RunId = runId,
                JobRunId = jobRunId,
                JobKey = jobKey,
                StepIndex = stepIndex,
                Offset = line,
                Text = text,
            },
        });

    private Task SendStep(long runId, long jobRunId, string jobKey, int index, JobRunStatus status, long startLine, long endLine, int? exitCode = null)
    {
        var update = new StepUpdate
        {
            RunId = runId,
            JobRunId = jobRunId,
            JobKey = jobKey,
            StepIndex = index,
            Status = status.ToString(),
            StartLine = startLine,
            EndLine = endLine,
        };
        if (exitCode is { } code)
            update.ExitCode = code;
        else
            update.ClearExitCode();
        return outgoing.SendAsync(new AgentToMaster { StepUpdate = update });
    }

    private Task Send(AgentToMaster message) => outgoing.SendAsync(message);
}
