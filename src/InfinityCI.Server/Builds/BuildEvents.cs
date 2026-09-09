using InfinityCI.Core;

namespace InfinityCI.Server.Builds;

public sealed record LogAppendedEventArgs(long BuildId, long Offset, string Text);

/// <summary>
/// In-process pub/sub from the build engine to real-time sinks.
/// The SignalR hub subscribes once and fans out to per-tab connection groups.
/// </summary>
public sealed class BuildEvents(ILogger<BuildEvents> logger)
{
    public event Func<Build, Task>? BuildUpdated;
    public event Func<LogAppendedEventArgs, Task>? LogAppended;

    public async Task PublishBuildUpdatedAsync(Build build)
    {
        var handler = BuildUpdated;
        if (handler is null)
            return;
        foreach (var subscriber in handler.GetInvocationList().Cast<Func<Build, Task>>())
        {
            try
            {
                await subscriber(build);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A BuildUpdated subscriber failed");
            }
        }
    }

    public async Task PublishLogAppendedAsync(long buildId, long offset, string text)
    {
        var handler = LogAppended;
        if (handler is null)
            return;
        var args = new LogAppendedEventArgs(buildId, offset, text);
        foreach (var subscriber in handler.GetInvocationList().Cast<Func<LogAppendedEventArgs, Task>>())
        {
            try
            {
                await subscriber(args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A LogAppended subscriber failed");
            }
        }
    }
}
