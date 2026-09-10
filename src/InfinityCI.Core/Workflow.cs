namespace InfinityCI.Core;

/// <summary>A workflow definition parsed from YAML: named jobs that run in parallel.</summary>
public sealed class Workflow
{
    public required string Name { get; init; }
    public string Project { get; init; } = "Default";
    public required IReadOnlyDictionary<string, WorkflowJob> Jobs { get; init; }
}

public sealed class WorkflowJob
{
    /// <summary>"local" (default) | "agent" | "agent:&lt;label&gt;"</summary>
    public string RunsOn { get; init; } = "local";
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public required IReadOnlyList<JobStep> Steps { get; init; }

    public bool RunsOnAgent => RunsOn.Equals("agent", StringComparison.OrdinalIgnoreCase) || RunsOn.StartsWith("agent:", StringComparison.OrdinalIgnoreCase);

    /// <summary>For runs_on: agent:&lt;label&gt;, the required agent label; otherwise null.</summary>
    public string? RequiredAgentLabel =>
        RunsOn.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) && RunsOn.Length > "agent:".Length
            ? RunsOn["agent:".Length..].Trim()
            : null;
}

public sealed class JobStep
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string? Shell { get; init; }
    public bool ContinueOnError { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
