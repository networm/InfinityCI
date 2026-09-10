using InfinityCI.Core;

namespace InfinityCI.Server.Runs;

public sealed record LogAppendedEventArgs(long RunId, string JobKey, LogLine Line);

/// <summary>In-process pub/sub from the run engine to real-time sinks (SignalR).</summary>
public sealed class RunEvents(ILogger<RunEvents> logger)
{
    public event Func<Run, Task>? RunUpdated;
    public event Func<JobRun, Task>? JobRunUpdated;
    public event Func<LogAppendedEventArgs, Task>? LogAppended;

    public async Task PublishRunUpdatedAsync(Run run) =>
        await PublishAsync(RunUpdated, run, "RunUpdated");

    public async Task PublishJobRunUpdatedAsync(JobRun jobRun) =>
        await PublishAsync(JobRunUpdated, jobRun, "JobRunUpdated");

    public async Task PublishLogAppendedAsync(LogAppendedEventArgs args) =>
        await PublishAsync(LogAppended, args, "LogAppended");

    private async Task PublishAsync<T>(Func<T, Task>? handler, T args, string eventName)
    {
        if (handler is null)
            return;
        foreach (var subscriber in handler.GetInvocationList().Cast<Func<T, Task>>())
        {
            try
            {
                await subscriber(args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A {Event} subscriber failed", eventName);
            }
        }
    }
}
