using System.Text.Json.Serialization;

namespace InfinityCI.Core;

public enum BuildStatus
{
    Queued = 0,
    Running = 1,
    Success = 2,
    Failed = 3,
    Cancelled = 4,
}

public enum BuildStepStatus
{
    Pending = 0,
    Running = 1,
    Success = 2,
    Failed = 3,
    Skipped = 4,
    Cancelled = 5,
}

/// <summary>
/// One execution of a job. Status changes bump <see cref="Version"/> so that
/// real-time clients can drop out-of-order updates.
/// </summary>
public sealed class Build
{
    public long Id { get; set; }
    public required string JobName { get; set; }
    public BuildStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>Monotonic mutation counter for optimistic ordering on the client.</summary>
    public long Version { get; set; }

    /// <summary>Per-step outcomes; persisted as JSON in <see cref="StepsJson"/>.</summary>
    [JsonIgnore]
    public List<BuildStepResult> Steps { get; set; } = [];

    /// <summary>EF-mapped JSON mirror of <see cref="Steps"/>.</summary>
    public string StepsJson { get; set; } = "[]";

    public bool IsTerminal => Status is BuildStatus.Success or BuildStatus.Failed or BuildStatus.Cancelled;
}

public sealed class BuildStepResult
{
    public required string Name { get; set; }
    public BuildStepStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>Byte offset in the build log where this step's output starts (inclusive).</summary>
    public long StartOffset { get; set; }

    /// <summary>Byte offset where this step's output ends (exclusive).</summary>
    public long EndOffset { get; set; }
}
