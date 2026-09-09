using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using InfinityCI.Core;
using InfinityCI.Grpc;

namespace InfinityCI.Agent;

/// <summary>
/// Executes one assigned build locally: runs the job's steps as child
/// processes and streams logs/steps/finish back to the master over the
/// agent's outgoing message channel. Byte offsets mirror what the master
/// will append, so step badges and resume logic line up end to end.
/// </summary>
public sealed class RemoteBuildRunner(
    AgentOptions options,
    AgentOutgoing outgoing,
    ConcurrentDictionary<long, CancellationTokenSource> cancellations,
    ILogger<RemoteBuildRunner> logger)
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private sealed class OffsetTracker
    {
        public long Value;
        public long Advance(string line)
        {
            var start = Value;
            Value += Utf8.GetByteCount(line) + 1;
            return start;
        }
    }

    public async Task RunAsync(JobAssignment assignment)
    {
        var buildId = assignment.BuildId;
        var cts = new CancellationTokenSource();
        cancellations[buildId] = cts;
        var offset = new OffsetTracker();
        var lastExitCode = 0;

        try
        {
            var job = JobYaml.Parse(assignment.JobYaml);
            var workspace = Path.Combine(options.WorkspacesDir, buildId.ToString());
            Directory.CreateDirectory(workspace);

            var intro = $"[agent {Environment.MachineName}] build {buildId} started.";
            var introOffset = offset.Advance(intro);
            await SendLog(buildId, introOffset, intro);

            var overall = BuildStatus.Success;
            for (var i = 0; i < job.Steps.Count; i++)
            {
                var step = job.Steps[i];
                var startOffset = offset.Value;
                await SendStep(buildId, i, BuildStepStatus.Running, startOffset, startOffset);

                lastExitCode = await RunStepAsync(buildId, job, step, workspace, cts.Token, offset);

                var failed = lastExitCode != 0;
                var stepStatus = cts.IsCancellationRequested
                    ? BuildStepStatus.Cancelled
                    : failed ? BuildStepStatus.Failed : BuildStepStatus.Success;
                await SendStep(buildId, i, stepStatus, startOffset, offset.Value, lastExitCode);

                if (cts.IsCancellationRequested)
                {
                    overall = BuildStatus.Cancelled;
                    break;
                }
                if (failed && !step.ContinueOnError)
                {
                    overall = BuildStatus.Failed;
                    break;
                }
            }

            if (overall == BuildStatus.Failed)
                logger.LogInformation("Build {BuildId} failed with exit code {Exit}", buildId, lastExitCode);

            await Send(new AgentToMaster
            {
                BuildFinished = new BuildFinished
                {
                    BuildId = buildId,
                    Status = overall.ToString(),
                    ExitCode = overall == BuildStatus.Success ? 0 : lastExitCode,
                },
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await Send(new AgentToMaster
            {
                BuildFinished = new BuildFinished { BuildId = buildId, Status = nameof(BuildStatus.Cancelled) },
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Build {BuildId} crashed the runner", buildId);
            var errorLine = $"[agent] internal error: {ex.Message}";
            await SendLog(buildId, offset.Advance(errorLine), errorLine);
            await Send(new AgentToMaster
            {
                BuildFinished = new BuildFinished { BuildId = buildId, Status = nameof(BuildStatus.Failed), ExitCode = lastExitCode },
            });
        }
        finally
        {
            cancellations.TryRemove(buildId, out _);
            cts.Dispose();
        }
    }

    public bool TryCancel(long buildId)
    {
        if (!cancellations.TryGetValue(buildId, out var cts))
            return false;
        cts.Cancel();
        return true;
    }

    private async Task<int> RunStepAsync(
        long buildId, JobDefinition job, JobStep step, string workspace, CancellationToken ct, OffsetTracker offset)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in job.Environment)
            env[key] = value;
        foreach (var (key, value) in step.Environment)
            env[key] = value;
        env["CI"] = "true";
        env["INFINITY_BUILD_ID"] = buildId.ToString();
        env["INFINITY_JOB_NAME"] = job.Name;

        var psi = ShellResolver.CreateStartInfo(step.Command, step.Shell, workspace, env);
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close(); // children see EOF on stdin instead of the agent's console

        var pumpOut = PumpAsync(process.StandardOutput, buildId, offset);
        var pumpErr = PumpAsync(process.StandardError, buildId, offset);

        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill process tree for build {BuildId}", buildId);
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
            await Task.WhenAll(pumpOut, pumpErr);
            throw;
        }

        await Task.WhenAll(pumpOut, pumpErr);
        return process.ExitCode;
    }

    private async Task PumpAsync(StreamReader reader, long buildId, OffsetTracker offset)
    {
        // No cancellation token: the stream ends when the (possibly killed) process closes it.
        while (await reader.ReadLineAsync() is { } line)
            await SendLog(buildId, offset.Advance(line), line);
    }

    private Task SendLog(long buildId, long offset, string text) =>
        outgoing.SendAsync(new AgentToMaster
        {
            LogChunk = new LogChunk { BuildId = buildId, Offset = offset, Text = text },
        });

    private Task SendStep(long buildId, int index, BuildStepStatus status, long startOffset, long endOffset, int? exitCode = null)
    {
        var update = new StepUpdate
        {
            BuildId = buildId,
            StepIndex = index,
            Status = status.ToString(),
            StartOffset = startOffset,
            EndOffset = endOffset,
        };
        if (exitCode is { } code)
            update.ExitCode = code;
        else
            update.ClearExitCode();
        return outgoing.SendAsync(new AgentToMaster { StepUpdate = update });
    }

    private Task Send(AgentToMaster message) => outgoing.SendAsync(message);
}
