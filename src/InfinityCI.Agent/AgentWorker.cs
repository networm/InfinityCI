using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using InfinityCI.Grpc;
using Grpc.Core;
using Grpc.Net.Client;

namespace InfinityCI.Agent;

/// <summary>
/// Maintains the single outbound bidi stream to the master: register, heartbeat
/// lease, pull-based job requests, and dispatching assignments to
/// <see cref="RemoteBuildRunner"/>. gRPC request streams allow one writer at a
/// time, so all outgoing messages flow through a per-connection channel drained
/// by exactly one writer task. Reconnects with exponential backoff, then
/// re-registers and pulls again.
/// </summary>
public sealed class AgentWorker(
    AgentOptions options,
    RemoteBuildRunner runner,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _cancellations = new();
    private volatile ChannelWriter<AgentToMaster> _outgoing = Channel.CreateUnbounded<AgentToMaster>().Writer;
    private int _runningBuilds;
    private volatile bool _registered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.DataDir);
        var agentId = LoadOrCreateAgentId();
        logger.LogInformation("Agent {Name} ({Id}) starting, master {Url}", options.AgentName, agentId, options.MasterUrl);

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(agentId, stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Connection to master at {Url} lost; retrying in {Backoff}", options.MasterUrl, backoff);
            }

            _registered = false;
            try
            {
                await Task.Delay(backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private async Task ConnectOnceAsync(string agentId, CancellationToken stoppingToken)
    {
        using var channel = GrpcChannel.ForAddress(options.MasterUrl);
        var client = new AgentHub.AgentHubClient(channel);
        var duplex = client.Connect(cancellationToken: stoppingToken);

        // All request-stream writes funnel through this channel and one writer.
        var outgoing = Channel.CreateUnbounded<AgentToMaster>(new UnboundedChannelOptions { SingleReader = true });
        _outgoing = outgoing.Writer;

        var writer = Task.Run(async () =>
        {
            await foreach (var message in outgoing.Reader.ReadAllAsync(stoppingToken))
                await duplex.RequestStream.WriteAsync(message);
        }, stoppingToken);

        await outgoing.Writer.WriteAsync(new AgentToMaster
        {
            Register = new AgentRegistration
            {
                AgentId = agentId,
                AgentName = options.AgentName,
                Version = options.Version,
                Labels = { options.Labels },
                MaxConcurrentBuilds = options.MaxConcurrentBuilds,
            },
        });

        var reader = Task.Run(async () =>
        {
            await foreach (var message in duplex.ResponseStream.ReadAllAsync(stoppingToken))
            {
                switch (message.PayloadCase)
                {
                    case MasterToAgent.PayloadOneofCase.RegisterAck:
                        _registered = true;
                        logger.LogInformation("Registered with master as {Name} ({Id})", options.AgentName, agentId);
                        break;

                    case MasterToAgent.PayloadOneofCase.Assignment:
                        var assignment = message.Assignment;
                        Interlocked.Increment(ref _runningBuilds);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await runner.RunAsync(assignment);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref _runningBuilds);
                            }
                        }, CancellationToken.None);
                        break;

                    case MasterToAgent.PayloadOneofCase.CancelBuild:
                        logger.LogInformation("Master requested cancellation of build {BuildId}", message.CancelBuild);
                        runner.TryCancel(message.CancelBuild);
                        break;
                }
            }
        }, stoppingToken);

        var heartbeats = Task.Run(() => HeartbeatLoop(outgoing.Writer, stoppingToken), stoppingToken);

        // Whichever stream side ends first terminates the connection attempt.
        await Task.WhenAny(writer, reader, heartbeats);

        try
        {
            duplex.RequestStream.CompleteAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // stream already broken
        }
    }

    private async Task HeartbeatLoop(ChannelWriter<AgentToMaster> outgoing, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds));
        var process = Process.GetCurrentProcess();
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            process.Refresh();
            var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var bootElapsed = Environment.TickCount64;
            var cpuPercent = bootElapsed > 0
                ? Math.Round(process.TotalProcessorTime.TotalMilliseconds / bootElapsed * 100 / Environment.ProcessorCount, 1)
                : 0;

            await outgoing.WriteAsync(new AgentToMaster
            {
                Stats = new AgentStats
                {
                    CpuPercent = cpuPercent,
                    MemoryPercent = totalMemory > 0 ? Math.Round(process.WorkingSet64 * 100.0 / totalMemory, 1) : 0,
                    FreeDiskBytes = GetFreeDiskBytes(),
                },
            });

            if (_registered)
            {
                // Pull: declare each free slot so the master can assign pending work.
                var freeSlots = options.MaxConcurrentBuilds - Volatile.Read(ref _runningBuilds);
                for (var i = 0; i < Math.Max(0, freeSlots); i++)
                    await outgoing.WriteAsync(new AgentToMaster { RequestJob = true });
            }
        }
    }

    private long GetFreeDiskBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(options.WorkspacesDir));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    private string LoadOrCreateAgentId()
    {
        try
        {
            if (File.Exists(options.AgentIdPath))
                return File.ReadAllText(options.AgentIdPath).Trim();
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(options.AgentIdPath, id);
            return id;
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
