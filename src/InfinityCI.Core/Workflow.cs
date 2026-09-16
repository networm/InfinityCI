namespace InfinityCI.Core;

/// <summary>A workflow definition parsed from YAML: named jobs that run in parallel,
/// optionally gated by <c>needs</c> dependencies (a DAG).</summary>
public sealed class Workflow
{
    public required string Name { get; init; }
    public string Project { get; init; } = "Default";

    /// <summary>Optional Git source checkout; steps run inside the working copy.</summary>
    public ScmConfig? Scm { get; init; }

    /// <summary>Build parameters; every value is exported to steps as an environment variable.</summary>
    public IReadOnlyList<WorkflowParam> Params { get; init; } = [];

    /// <summary>Cron expressions (5-field, server-local time) that trigger this workflow periodically.</summary>
    public IReadOnlyList<string> Schedules { get; init; } = [];

    public required IReadOnlyDictionary<string, WorkflowJob> Jobs { get; init; }
}

public sealed class WorkflowParam
{
    public required string Name { get; init; }
    public string Default { get; init; } = "";
    public bool Required { get; init; }
    public string? Description { get; init; }
}

public sealed class WorkflowJob
{
    /// <summary>"local" (default) | "agent" | "agent:&lt;label&gt;"</summary>
    public string RunsOn { get; init; } = "local";
    public IReadOnlyList<string> Needs { get; init; } = [];
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public required IReadOnlyList<JobStep> Steps { get; init; }

    /// <summary>Condition expression; the only supported value is "always()" —
    /// run even when `needs` failed or were skipped.</summary>
    public string? If { get; init; }

    /// <summary>Whole-job timeout applied from the first step's start; null = no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    public bool RunsOnAgent => RunsOn.Equals("agent", StringComparison.OrdinalIgnoreCase) || RunsOn.StartsWith("agent:", StringComparison.OrdinalIgnoreCase);

    /// <summary>For runs_on: agent:&lt;label&gt;, the required agent label; otherwise null.</summary>
    public string? RequiredAgentLabel =>
        RunsOn.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) && RunsOn.Length > "agent:".Length
            ? RunsOn["agent:".Length..].Trim()
            : null;

    /// <summary>True when the job declared `if: always()` — it runs even when its needs failed.</summary>
    public bool RunAlways =>
        If is { } condition && NormalizeIf(condition) == "always";

    /// <summary>Case/whitespace/parens-insensitive normalization: "Always()" → "always".</summary>
    public static string NormalizeIf(string condition)
    {
        var normalized = condition.Trim().Replace(" ", "").ToLowerInvariant();
        return normalized.EndsWith("()") ? normalized[..^2] : normalized;
    }
}

public sealed class JobStep
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string? Shell { get; init; }
    public bool ContinueOnError { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Retry the whole step up to this many extra times after a failure (default 0 = no retry).</summary>
    public int Retry { get; init; }

    /// <summary>Per-step timeout; overrides the job-level timeout when set. null = inherit/no limit.</summary>
    public TimeSpan? Timeout { get; init; }
}
