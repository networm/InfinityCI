using InfinityCI.Grpc;
using Grpc.Core;

namespace InfinityCI.Server.Agents;

/// <summary>
/// The single bidi-stream RPC each agent process holds open. Register comes
/// first; heartbeats keep the lease alive; request_job pulls work; log/step/
/// finish messages flow build state back through the same pipeline as local
/// builds (log store + events), so the UI cannot tell them apart.
/// </summary>
public sealed class AgentHubService(
    AgentRegistry registry,
    RemoteBuildCoordinator coordinator,
    ILogger<AgentHubService> logger) : AgentHub.AgentHubBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentToMaster> requestStream,
        IServerStreamWriter<MasterToAgent> responseStream,
        ServerCallContext context)
    {
        var cancellationToken = context.CancellationToken;
        AgentConnection? agent = null;

        try
        {
            await foreach (var message in requestStream.ReadAllAsync(cancellationToken))
            {
                if (agent is not null)
                    registry.Touch(agent.Id);

                switch (message.PayloadCase)
                {
                    case AgentToMaster.PayloadOneofCase.Register:
                        agent = registry.Register(message.Register, responseStream);
                        await registry.TrySendAsync(agent.Id, new MasterToAgent { RegisterAck = true });
                        await coordinator.PublishAgentsChangedAsync();
                        break;

                    case AgentToMaster.PayloadOneofCase.Stats when agent is not null:
                        registry.UpdateStats(agent.Id, message.Stats);
                        await coordinator.PublishAgentsChangedAsync();
                        break;

                    case AgentToMaster.PayloadOneofCase.RequestJob when agent is not null:
                        if (registry.TryReserveSlot(agent.Id))
                        {
                            var claimed = await coordinator.TryClaimNextPendingAsync(agent);
                            if (!claimed)
                                registry.ReleaseSlot(agent.Id);
                        }
                        break;

                    case AgentToMaster.PayloadOneofCase.LogChunk when agent is not null:
                        await coordinator.AppendLogAsync(message.LogChunk);
                        break;

                    case AgentToMaster.PayloadOneofCase.StepUpdate when agent is not null:
                        await coordinator.ApplyStepUpdateAsync(message.StepUpdate);
                        break;

                    case AgentToMaster.PayloadOneofCase.BuildFinished when agent is not null:
                        await coordinator.FinalizeAsync(message.BuildFinished);
                        registry.ClearAssignment(message.BuildFinished.BuildId);
                        registry.ReleaseSlot(agent.Id);
                        await coordinator.PublishAgentsChangedAsync();
                        break;

                    case AgentToMaster.PayloadOneofCase.None:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // client disconnected
        }

        if (agent is not null)
        {
            var orphaned = registry.MarkOffline(agent.Id);
            await coordinator.RequeueBuildsAsync(orphaned);
            await coordinator.PublishAgentsChangedAsync();
        }
    }
}
