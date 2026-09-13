using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using InfinityCI.Core;

namespace InfinityCI.Server.Runs;

/// <summary>
/// Shared queue of job runs waiting for a local executor slot. A standalone
/// singleton so both the trigger path and the needs-gating aggregator can
/// dispatch into it without circular dependencies. Duplicate writes for a job
/// run that is already waiting are dropped — waiting jobs keep Status=Queued,
/// so repeated needs-evaluations must not stack the same dispatch.
/// </summary>
public sealed class LocalJobRunQueue
{
    private readonly Channel<JobRun> _channel = Channel.CreateUnbounded<JobRun>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });
    private readonly ConcurrentDictionary<long, byte> _queuedIds = new();

    /// <summary>True when newly queued; false when this job run is already waiting.</summary>
    public bool TryWrite(JobRun jobRun)
    {
        if (!_queuedIds.TryAdd(jobRun.Id, 0))
            return false;
        if (_channel.Writer.TryWrite(jobRun))
            return true;
        _queuedIds.TryRemove(jobRun.Id, out _);
        return false;
    }

    public async IAsyncEnumerable<JobRun> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var jobRun in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _queuedIds.TryRemove(jobRun.Id, out _);
            yield return jobRun;
        }
    }
}
