using System.Threading.Channels;
using InfinityCI.Grpc;

namespace InfinityCI.Agent;

/// <summary>
/// Holds the current connection's outgoing writer. DI-singleton so the runner
/// can hand messages to whatever connection is live; swapped on each reconnect.
/// </summary>
public sealed class AgentOutgoing
{
    private volatile ChannelWriter<AgentToMaster> _writer = Channel.CreateUnbounded<AgentToMaster>().Writer;

    public void Set(ChannelWriter<AgentToMaster> writer) => _writer = writer;

    public ChannelWriter<AgentToMaster> Writer => _writer;

    public Task SendAsync(AgentToMaster message) => _writer.WriteAsync(message).AsTask();
}
