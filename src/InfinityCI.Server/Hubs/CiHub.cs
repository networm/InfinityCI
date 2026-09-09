using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.SignalR;

namespace InfinityCI.Server.Hubs;

public sealed record BuildSubscription(Build Build, IReadOnlyList<LogLine> Lines);

/// <summary>
/// Real-time hub. Each browser tab opens its own connection with its own
/// ConnectionId; subscriptions are per-connection and SignalR removes group
/// membership automatically on disconnect, so tabs never affect each other.
/// </summary>
public sealed class CiHub(BuildLogStore logStore, BuildRepository repository, AgentRegistry agentRegistry) : Hub
{
    /// <summary>
    /// Joins the build's group and returns a snapshot plus log lines after the
    /// caller's byte cursor. Live events may duplicate backfill lines; clients
    /// drop any line whose offset is below their cursor.
    /// </summary>
    public async Task<BuildSubscription> SubscribeBuild(long buildId, long afterOffset)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Build(buildId));

        var build = await repository.GetAsync(buildId)
            ?? throw new HubException($"Build {buildId} not found.");

        // Join first, then backfill: a line appended in between is delivered
        // twice (live + backfill) and deduplicated client-side by offset.
        var lines = await logStore.ReadAfterAsync(buildId, afterOffset);
        return new BuildSubscription(build, lines);
    }

    public Task UnsubscribeBuild(long buildId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Build(buildId));

    public async Task<IReadOnlyList<Build>> SubscribeDashboard()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Dashboard);
        return await repository.ListAsync(jobName: null, skip: 0, take: 50);
    }

    public Task UnsubscribeDashboard() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Dashboard);

    public async Task<IReadOnlyList<AgentSnapshot>> SubscribeAgents()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, CiGroups.Agents);
        return agentRegistry.Snapshot();
    }

    public Task UnsubscribeAgents() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CiGroups.Agents);
}
