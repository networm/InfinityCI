using System.Collections.Concurrent;
using Grpc.Core;
using InfinityCI.Grpc;

namespace InfinityCI.Server.Agents;

public sealed record AgentSnapshot(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> Labels,
    bool Online,
    int MaxConcurrentBuilds,
    int RunningBuilds,
    double CpuPercent,
    double MemoryPercent,
    long FreeDiskBytes,
    DateTimeOffset LastSeenUtc);

public readonly record struct PendingJobRun(long JobRunId, string? RequiredLabel);

public sealed class AgentConnection
{
    public required string Id { get; init; }
    public required string Name { get; set; }   // mutable: live agent-config updates
    public string Version { get; set; } = "";
    public IReadOnlyList<string> Labels { get; set; } = [];
    public int MaxConcurrentBuilds { get; set; } = 1;
    public int RunningBuilds; // Interlocked-managed
    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public long FreeDiskBytes { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Online { get; set; } = true;
    public IServerStreamWriter<MasterToAgent>? ResponseStream { get; set; }
}

/// <summary>
/// In-memory state of connected agents plus the pending-job-run queue for
/// pull-based dispatch. Persistence and build mutations live in
/// <see cref="RemoteBuildCoordinator"/>.
/// </summary>
public sealed class AgentRegistry(ILogger<AgentRegistry> logger)
{
    private readonly ConcurrentDictionary<string, AgentConnection> _agents = new();
    private readonly ConcurrentQueue<PendingJobRun> _pending = new();
    private readonly ConcurrentDictionary<long, byte> _pendingIds = new();
    private readonly ConcurrentDictionary<long, string> _assignments = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    /// <summary>Fired whenever the agent list or its stats change.</summary>
    public event Func<IReadOnlyList<AgentSnapshot>, Task>? AgentsChanged;

    public AgentConnection? Get(string agentId) => _agents.GetValueOrDefault(agentId);
    public IReadOnlyCollection<AgentConnection> All => _agents.Values.ToList();

    public AgentConnection Register(AgentRegistration registration, IServerStreamWriter<MasterToAgent> responseStream)
    {
        var connection = new AgentConnection
        {
            Id = registration.AgentId,
            Name = string.IsNullOrWhiteSpace(registration.AgentName) ? registration.AgentId : registration.AgentName,
            Version = registration.Version,
            Labels = registration.Labels.ToArray(),
            MaxConcurrentBuilds = Math.Max(1, registration.MaxConcurrentBuilds),
            ResponseStream = responseStream,
            Online = true,
            LastSeenUtc = DateTimeOffset.UtcNow,
        };
        _writeLocks.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        // Re-registration replaces a stale entry from a dropped connection.
        _agents[connection.Id] = connection;
        logger.LogInformation("Agent {Name} ({Id}) registered, labels=[{Labels}], max {Max} concurrent",
            connection.Name, connection.Id, string.Join(",", connection.Labels), connection.MaxConcurrentBuilds);
        return connection;
    }

    public void Touch(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
            agent.LastSeenUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Pushes an edited agent config onto a connected agent immediately;
    /// offline agents pick it up at their next registration.</summary>
    public void ApplyConfig(string agentId, string name, string[] labels, int maxConcurrentBuilds)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            return;
        agent.Name = name;
        agent.Labels = labels;
        agent.MaxConcurrentBuilds = Math.Max(1, maxConcurrentBuilds);
    }

    public void UpdateStats(string agentId, AgentStats stats)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            return;
        agent.CpuPercent = stats.CpuPercent;
        agent.MemoryPercent = stats.MemoryPercent;
        agent.FreeDiskBytes = stats.FreeDiskBytes;
        agent.LastSeenUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Marks the agent offline and returns job run ids assigned to it.</summary>
    public List<long> MarkOffline(string agentId)
    {
        var orphaned = new List<long>();
        if (!_agents.TryGetValue(agentId, out var agent) || !agent.Online)
            return orphaned;

        TakeOffline(agent);
        orphaned.AddRange(CollectAssignments(agentId));
        logger.LogWarning("Agent {Name} ({Id}) went offline; {Count} job run(s) orphaned",
            agent.Name, agent.Id, orphaned.Count);
        return orphaned;
    }

    /// <summary>Removes a deleted agent entirely so it no longer appears in snapshots;
    /// returns job run ids assigned to it.</summary>
    public List<long> Remove(string agentId)
    {
        var orphaned = new List<long>();
        if (!_agents.TryRemove(agentId, out var agent))
            return orphaned;

        TakeOffline(agent);
        orphaned.AddRange(CollectAssignments(agentId));
        logger.LogInformation("Agent {Name} ({Id}) deleted; {Count} job run(s) orphaned",
            agent.Name, agent.Id, orphaned.Count);
        return orphaned;
    }

    private void TakeOffline(AgentConnection agent)
    {
        agent.Online = false;
        agent.ResponseStream = null;
    }

    private IEnumerable<long> CollectAssignments(string agentId)
    {
        foreach (var (jobRunId, ownerId) in _assignments)
        {
            if (ownerId == agentId && _assignments.TryRemove(new KeyValuePair<long, string>(jobRunId, ownerId)))
                yield return jobRunId;
        }
    }

    // -- pending queue (pull dispatch) --

    /// <summary>True when newly queued; false when this job run is already waiting for an agent.</summary>
    public bool EnqueuePending(PendingJobRun pending)
    {
        if (!_pendingIds.TryAdd(pending.JobRunId, 0))
            return false;
        _pending.Enqueue(pending);
        return true;
    }

    public bool TryDequeuePending(out PendingJobRun pending)
    {
        if (!_pending.TryDequeue(out pending))
            return false;
        _pendingIds.TryRemove(pending.JobRunId, out _);
        return true;
    }

    public void RequeuePending(PendingJobRun pending)
    {
        _pendingIds.TryAdd(pending.JobRunId, 0);
        _pending.Enqueue(pending);
    }

    // -- capacity & assignment bookkeeping --

    public bool TryReserveSlot(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var agent) || !agent.Online)
            return false;
        while (true)
        {
            var current = agent.RunningBuilds;
            if (current >= agent.MaxConcurrentBuilds)
                return false;
            if (Interlocked.CompareExchange(ref agent.RunningBuilds, current + 1, current) == current)
                return true;
        }
    }

    public void ReleaseSlot(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            return;
        while (true)
        {
            var current = agent.RunningBuilds;
            if (current <= 0)
                return;
            if (Interlocked.CompareExchange(ref agent.RunningBuilds, current - 1, current) == current)
                return;
        }
    }

    public void Assign(long jobRunId, string agentId) => _assignments[jobRunId] = agentId;
    public void ClearAssignment(long jobRunId) => _assignments.TryRemove(jobRunId, out _);
    public string? GetAssignedAgent(long jobRunId) => _assignments.GetValueOrDefault(jobRunId);

    public async Task<bool> TrySendAsync(string agentId, MasterToAgent message)
    {
        if (!_agents.TryGetValue(agentId, out var agent) || agent.ResponseStream is null)
            return false;
        var gate = _writeLocks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await agent.ResponseStream.WriteAsync(message);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send {MessageType} to agent {Name}", message.PayloadCase, agent.Name);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> TrySendCancelAsync(long jobRunId)
    {
        if (!_assignments.TryGetValue(jobRunId, out var agentId))
            return false;
        return await TrySendAsync(agentId, new MasterToAgent { CancelJobRun = jobRunId });
    }

    public IReadOnlyList<AgentSnapshot> Snapshot() =>
        _agents.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToSnapshot)
            .ToList();

    public static AgentSnapshot ToSnapshot(AgentConnection agent) => new(
        agent.Id,
        agent.Name,
        agent.Version,
        agent.Labels,
        agent.Online,
        agent.MaxConcurrentBuilds,
        agent.RunningBuilds,
        agent.CpuPercent,
        agent.MemoryPercent,
        agent.FreeDiskBytes,
        agent.LastSeenUtc);

    public async Task PublishChangedAsync()
    {
        var handler = AgentsChanged;
        if (handler is null)
            return;
        var snapshot = Snapshot();
        foreach (var subscriber in handler.GetInvocationList().Cast<Func<IReadOnlyList<AgentSnapshot>, Task>>())
        {
            try
            {
                await subscriber(snapshot);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An AgentsChanged subscriber failed");
            }
        }
    }
}
