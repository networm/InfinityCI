using InfinityCI.Grpc;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Runs;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace InfinityCI.Server.Agents;

/// <summary>
/// The single bidi-stream RPC each agent process holds open. Register comes
/// first (with a one-time enrollment token for new agents); heartbeats keep the
/// lease alive; request_job pulls work; log/step/finish messages flow build
/// state back through the same pipeline as local runs, so the UI cannot tell
/// them apart.
/// </summary>
[Authorize(Roles = AppRoles.AdminsAndSuperAdmin)]
public sealed class AgentHubService(
    AgentRegistry registry,
    RemoteBuildCoordinator coordinator,
    IServiceScopeFactory scopeFactory,
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
                    case AgentToMaster.PayloadOneofCase.Register when agent is null:
                        agent = await TryRegisterAsync(message.Register, responseStream);
                        if (agent is not null)
                        {
                            await registry.TrySendAsync(agent.Id, new MasterToAgent { RegisterAck = true });
                            await coordinator.PublishAgentsChangedAsync();
                        }
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

                    case AgentToMaster.PayloadOneofCase.JobFinished when agent is not null:
                        await coordinator.FinalizeAsync(message.JobFinished);
                        registry.ClearAssignment(message.JobFinished.JobRunId);
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
            await coordinator.RequeueJobRunsAsync(orphaned);
            await coordinator.PublishAgentsChangedAsync();
        }
    }

    /// <summary>Validates enrollment / enablement, then registers. Returns null when rejected.</summary>
    private async Task<AgentConnection?> TryRegisterAsync(AgentRegistration registration, IServerStreamWriter<MasterToAgent> responseStream)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Storage.CiDbContext>();

        var record = await db.Agents.FindAsync([registration.AgentId]);
        if (record is not null)
        {
            if (!record.Enabled)
            {
                logger.LogWarning("Rejected registration from disabled agent {Id}", registration.AgentId);
                return null;
            }
            record.LastSeenUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return registry.Register(registration, responseStream);
        }

        // New agent: an unused one-time enrollment token is required.
        // (Explicit EF call: System.Linq.Async in the gRPC graph makes the
        // lambda overloads ambiguous.)
        var enrollment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.AgentEnrollments,
            e => e.Token == registration.EnrollToken && e.UsedByAgentId == null);
        if (enrollment is null)
        {
            logger.LogWarning("Rejected unknown agent {Id}: no valid enrollment token", registration.AgentId);
            return null;
        }

        enrollment.UsedByAgentId = registration.AgentId;
        db.Agents.Add(new Storage.AgentRecord
        {
            Id = registration.AgentId,
            Name = string.IsNullOrWhiteSpace(registration.AgentName) ? enrollment.Name : registration.AgentName,
            LabelsJson = System.Text.Json.JsonSerializer.Serialize(registration.Labels.ToArray()),
            MaxConcurrentBuilds = Math.Max(1, registration.MaxConcurrentBuilds),
            Enabled = true,
            EnrolledAt = DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return registry.Register(registration, responseStream);
    }
}
