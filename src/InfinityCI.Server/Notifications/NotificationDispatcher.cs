using InfinityCI.Core;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Runs;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Notifications;

/// <summary>
/// Fans terminal run states out to the workflow's configured notification
/// channels. Channel failures are logged and never affect the run itself.
/// </summary>
public sealed class NotificationDispatcher(
    WorkflowControlService control,
    IEnumerable<INotifier> notifiers,
    IOptions<CiServerOptions> optionsAccessor,
    ILogger<NotificationDispatcher> logger)
{
    /// <summary>Subscribes to run events; call from startup before accepting triggers.</summary>
    public void Subscribe(RunEvents events)
    {
        events.RunUpdated += async run =>
        {
            if (!run.IsTerminal)
                return;
            try
            {
                await DispatchAsync(run);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification dispatch failed for run {RunId}", run.Id);
            }
        };
    }

    public async Task DispatchAsync(Run run)
    {
        var channels = await control.GetNotifyChannelsAsync(run.WorkflowName);
        if (channels.Count == 0)
            return;

        var message = NotificationFormatter.Build(run, optionsAccessor.Value.PublicOrigin);
        foreach (var channel in channels)
        {
            if (channel.Events.Equals("failure", StringComparison.OrdinalIgnoreCase)
                && run.Status != RunStatus.Failed)
                continue;

            var notifier = notifiers.FirstOrDefault(n => n.Type.Equals(channel.Type, StringComparison.OrdinalIgnoreCase));
            if (notifier is null)
            {
                logger.LogWarning("Unknown notification channel type '{Type}' for workflow {Workflow}",
                    channel.Type, run.WorkflowName);
                continue;
            }

            try
            {
                await notifier.SendAsync(message, channel.Target);
                logger.LogInformation("{Type} notification sent for run {RunId} ({Workflow})",
                    channel.Type, run.Id, run.WorkflowName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Type} notification failed for run {RunId} ({Workflow})",
                    channel.Type, run.Id, run.WorkflowName);
            }
        }
    }
}
