namespace InfinityCI.Server.Realtime;

public enum WorkflowChangeKind { Added, Updated, Deleted }

public enum AdminChangeKind { Projects, Users, Credentials, Enrollments, Agents }

public sealed record WorkflowChangedEventArgs(string Name, WorkflowChangeKind Kind, string Project);

public sealed record AdminChangedEventArgs(AdminChangeKind Kind);

/// <summary>
/// In-process pub/sub for configuration changes (workflow definitions, projects,
/// users, credentials, enrollments, agent records) to real-time sinks (SignalR),
/// mirroring <see cref="RunEvents"/> for the run engine.
/// </summary>
public sealed class ChangeEvents(ILogger<ChangeEvents> logger)
{
    public event Func<WorkflowChangedEventArgs, Task>? WorkflowChanged;
    public event Func<AdminChangedEventArgs, Task>? AdminChanged;

    public Task PublishWorkflowChangedAsync(string name, WorkflowChangeKind kind, string project = "") =>
        PublishAsync(WorkflowChanged, new WorkflowChangedEventArgs(name, kind, project), "WorkflowChanged");

    public Task PublishAdminChangedAsync(AdminChangeKind kind) =>
        PublishAsync(AdminChanged, new AdminChangedEventArgs(kind), "AdminChanged");

    private async Task PublishAsync<T>(Func<T, Task>? handler, T args, string eventName)
    {
        if (handler is null)
            return;
        foreach (var subscriber in handler.GetInvocationList().Cast<Func<T, Task>>())
        {
            try
            {
                await subscriber(args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A {Event} subscriber failed", eventName);
            }
        }
    }
}
