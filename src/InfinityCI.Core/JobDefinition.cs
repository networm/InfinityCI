namespace InfinityCI.Core;

/// <summary>A CI job definition, typically parsed from a YAML file.</summary>
public sealed class JobDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public required IReadOnlyList<JobStep> Steps { get; init; }
}

public sealed class JobStep
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string? Shell { get; init; }
    public bool ContinueOnError { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
