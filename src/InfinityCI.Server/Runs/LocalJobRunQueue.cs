using System.Threading.Channels;
using InfinityCI.Core;

namespace InfinityCI.Server.Runs;

/// <summary>
/// Shared queue of job runs waiting for a local executor slot. A standalone
/// singleton so both the trigger path and the needs-gating aggregator can
/// dispatch into it without circular dependencies.
/// </summary>
public sealed class LocalJobRunQueue
{
    private readonly Channel<JobRun> _channel = Channel.CreateUnbounded<JobRun>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    public void TryWrite(JobRun jobRun) => _channel.Writer.TryWrite(jobRun);

    public IAsyncEnumerable<JobRun> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
