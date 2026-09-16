using InfinityCI.Core;

namespace InfinityCI.Server.Jobs;

public sealed record WorkflowParamSummary(string Name, string? Default, bool Required, string? Description);

public sealed record WorkflowJobSummary(string Key, string RunsOn, IReadOnlyList<string> Needs, int Steps);

/// <summary>Job-list row: same shape as the GET /api/jobs items, shared by the
/// REST endpoint and the SignalR hub snapshot.</summary>
public sealed record WorkflowSummary(
    string Name,
    string Project,
    bool Enabled,
    IReadOnlyList<WorkflowParamSummary> Params,
    IReadOnlyList<WorkflowJobSummary> Jobs);

/// <summary>Builds the workflow directory listing used by both /api/jobs and the hub.</summary>
public sealed class WorkflowDirectory(WorkflowStore store, WorkflowControlService control)
{
    /// <summary>All workflows, optionally filtered to the visible projects (null = all).</summary>
    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(HashSet<string>? visible = null)
    {
        var states = await control.GetAllAsync();
        return store.Workflows
            .Where(w => visible is null || visible.Contains(w.Project))
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .Select(w => new WorkflowSummary(
                w.Name,
                w.Project,
                states.GetValueOrDefault(w.Name, true),
                w.Params.Select(p => new WorkflowParamSummary(p.Name, p.Default, p.Required, p.Description)).ToList(),
                w.Jobs.Select(j => new WorkflowJobSummary(j.Key, j.Value.RunsOn, j.Value.Needs, j.Value.Steps.Count)).ToList()))
            .ToList();
    }
}
