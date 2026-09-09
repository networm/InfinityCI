using InfinityCI.Core;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace InfinityCI.Server.Realtime;

/// <summary>
/// Bridges in-process build events to SignalR groups: status changes go to the
/// build's group and the dashboard; log lines go to the owning build's group only.
/// </summary>
public sealed class CiBroadcaster(BuildEvents events, IHubContext<CiHub> hubContext) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        events.BuildUpdated += OnBuildUpdatedAsync;
        events.LogAppended += OnLogAppendedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        events.BuildUpdated -= OnBuildUpdatedAsync;
        events.LogAppended -= OnLogAppendedAsync;
        return Task.CompletedTask;
    }

    private Task OnBuildUpdatedAsync(Build build) =>
        hubContext.Clients
            .Groups(CiGroups.Build(build.Id), CiGroups.Dashboard)
            .SendAsync("buildUpdated", build);

    private Task OnLogAppendedAsync(LogAppendedEventArgs args) =>
        hubContext.Clients
            .Group(CiGroups.Build(args.BuildId))
            .SendAsync("logAppended", args.BuildId, args.Offset, args.Text);
}
