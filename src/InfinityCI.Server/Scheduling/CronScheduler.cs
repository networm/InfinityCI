using InfinityCI.Core;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Realtime;
using InfinityCI.Server.Runs;

namespace InfinityCI.Server.Scheduling;

/// <summary>
/// Triggers workflows with a `schedule:` block on their cron cadence
/// (server-local time). Next-due times are computed in memory: a server
/// restart re-anchors to "now" and missed occurrences while the server was
/// down are skipped. `queue.Ready` keeps scheduled triggers from racing the
/// startup recovery.
/// </summary>
public sealed class CronScheduler(
    WorkflowStore store,
    WorkflowControlService control,
    RunQueueService queue,
    ChangeEvents events,
    ILogger<CronScheduler> logger) : BackgroundService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<PendingSchedule>> _pending = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PendingSchedule(CronSchedule Schedule, DateTimeOffset Next);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.Ready;
        RebuildAll();

        events.WorkflowChanged += OnWorkflowChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                await FireDueAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            events.WorkflowChanged -= OnWorkflowChanged;
        }
    }

    private Task OnWorkflowChanged(WorkflowChangedEventArgs args)
    {
        Refresh(args.Name);
        return Task.CompletedTask;
    }

    private void RebuildAll()
    {
        lock (_gate)
        {
            _pending.Clear();
            foreach (var workflow in store.Workflows)
                RegisterLocked(workflow.Name, workflow.Schedules);
        }
    }

    private void Refresh(string name)
    {
        lock (_gate)
        {
            _pending.Remove(name);
            var workflow = store.TryGet(name);
            if (workflow is not null)
                RegisterLocked(name, workflow.Schedules);
        }
    }

    private void RegisterLocked(string name, IReadOnlyList<string> schedules)
    {
        if (schedules.Count == 0)
            return;
        var now = DateTimeOffset.Now;
        var entries = new List<PendingSchedule>(schedules.Count);
        foreach (var expression in schedules)
        {
            try
            {
                var schedule = CronSchedule.Parse(expression);
                entries.Add(new PendingSchedule(schedule, schedule.NextOccurrence(now)));
            }
            catch (CronFormatException ex)
            {
                // Parse() validates at save time; a hot-edited broken file lands here.
                logger.LogWarning(ex, "Workflow {Workflow} has an invalid schedule; skipping it", name);
            }
        }
        if (entries.Count > 0)
            _pending[name] = entries;
    }

    private async Task FireDueAsync()
    {
        List<string> dueNames;
        lock (_gate)
        {
            dueNames = [];
            var now = DateTimeOffset.Now;
            foreach (var (name, entries) in _pending)
            {
                var due = entries.Any(e => e.Next <= now);
                if (!due)
                    continue;
                dueNames.Add(name);
                _pending[name] = entries
                    .Select(e => e.Next <= now ? new PendingSchedule(e.Schedule, e.Schedule.NextOccurrence(now)) : e)
                    .ToList();
            }
        }

        foreach (var name in dueNames)
        {
            try
            {
                if (!await control.IsEnabledAsync(name))
                {
                    logger.LogDebug("Scheduled trigger for disabled workflow {Workflow} skipped", name);
                    continue;
                }
                var run = await queue.TriggerAsync(name, "schedule");
                logger.LogInformation("Scheduled trigger fired for {Workflow}: run {RunId}", name, run.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Deleted mid-flight, disabled, parameter errors — never kill the loop.
                logger.LogWarning(ex, "Scheduled trigger for {Workflow} failed", name);
            }
        }
    }
}
