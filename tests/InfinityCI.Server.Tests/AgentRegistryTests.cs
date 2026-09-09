using Grpc.Core;
using InfinityCI.Grpc;
using InfinityCI.Server.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfinityCI.Server.Tests;

public class AgentRegistryTests
{
    private sealed class ThrowingStream : IServerStreamWriter<MasterToAgent>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(MasterToAgent message) => throw new NotSupportedException();
        public Task CompleteAsync() => throw new NotSupportedException();
    }

    private static AgentRegistry CreateRegistryWithAgent(string id, int max, out AgentConnection connection)
    {
        var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        connection = registry.Register(new AgentRegistration
        {
            AgentId = id,
            AgentName = "Agent " + id,
            Version = "test",
            Labels = { "linux", "docker" },
            MaxConcurrentBuilds = max,
        }, new ThrowingStream());
        return registry;
    }

    private static AgentRegistry CreateRegistryWithAgent(out AgentConnection connection) =>
        CreateRegistryWithAgent("agent-1", 2, out connection);

    [Fact]
    public void Register_AgentIsOnlineAndListedInSnapshot()
    {
        var registry = CreateRegistryWithAgent(out var connection);

        var snapshot = Assert.Single(registry.Snapshot());
        Assert.True(snapshot.Online);
        Assert.Equal(connection.Id, snapshot.Id);
        Assert.Equal(2, snapshot.MaxConcurrentBuilds);
        Assert.Contains("linux", snapshot.Labels);
    }

    [Fact]
    public void MarkOffline_RemovesOnlineState_AndOrphansAssignedBuilds()
    {
        var registry = CreateRegistryWithAgent(out _);
        registry.Assign(42, "agent-1");
        registry.Assign(43, "agent-1");
        registry.Assign(44, "other-agent");

        var orphaned = registry.MarkOffline("agent-1");

        Assert.Equal([42, 43], orphaned.Order());
        Assert.DoesNotContain(registry.Snapshot(), s => s.Id == "agent-1" && s.Online);
        // Re-marking is a no-op (already offline).
        Assert.Empty(registry.MarkOffline("agent-1"));
    }

    [Fact]
    public void SlotReservation_RespectsMaxConcurrentBuilds()
    {
        var registry = CreateRegistryWithAgent("agent-1", 2, out _);

        Assert.True(registry.TryReserveSlot("agent-1"));
        Assert.True(registry.TryReserveSlot("agent-1"));
        Assert.False(registry.TryReserveSlot("agent-1")); // capacity reached

        registry.ReleaseSlot("agent-1");
        Assert.True(registry.TryReserveSlot("agent-1"));
    }

    [Fact]
    public void PendingQueue_IsFifo()
    {
        var registry = CreateRegistryWithAgent(out _);

        registry.EnqueuePending(10);
        registry.EnqueuePending(11);
        registry.EnqueuePending(12);

        Assert.True(registry.TryDequeuePending(out var first));
        Assert.Equal(10, first);
        registry.RequeuePending(13);
        Assert.True(registry.TryDequeuePending(out var second));
        Assert.Equal(11, second);
        Assert.True(registry.TryDequeuePending(out var third));
        Assert.Equal(12, third);
        Assert.True(registry.TryDequeuePending(out var fourth));
        Assert.Equal(13, fourth);
    }
}
