using System.Text.Json.Serialization;

namespace InfinityCI.Core;

public enum RunStatus
{
    Queued = 0,
    Running = 1,
    Success = 2,
    Failed = 3,
    Cancelled = 4,
}

public enum JobRunStatus
{
    // Step-level states (a job run itself never sits in Pending/Skipped).
    Pending = 5,
    Skipped = 6,

    Queued = 0,
    Running = 1,
    Success = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>One trigger of a workflow; its jobs execute in parallel.</summary>
public sealed class Run
{
    public long Id { get; set; }
    public required string WorkflowName { get; set; }
    public required string Project { get; set; }
    public string TriggeredBy { get; set; } = "";
    public RunStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Monotonic mutation counter; real-time clients drop stale updates.</summary>
    public long Version { get; set; }

    public bool IsTerminal => Status is RunStatus.Success or RunStatus.Failed or RunStatus.Cancelled;
}

/// <summary>One job of a run. Job runs of the same run execute in parallel.</summary>
public sealed class JobRun
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public required string JobKey { get; set; }
    public required string RunsOn { get; set; }     // "local" | "agent" | "agent:<label>"
    public string? AgentId { get; set; }
    public JobRunStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>Monotonic mutation counter for optimistic client ordering.</summary>
    public long Version { get; set; }

    /// <summary>Per-step outcomes; persisted as JSON in <see cref="StepsJson"/>.</summary>
    public List<JobStepResult> Steps { get; set; } = [];

    /// <summary>EF-mapped JSON mirror of <see cref="Steps"/> (not part of the public API).</summary>
    [JsonIgnore]
    public string StepsJson { get; set; } = "[]";

    public bool IsTerminal => Status is JobRunStatus.Success or JobRunStatus.Failed or JobRunStatus.Cancelled;
}

public sealed class JobStepResult
{
    public required string Name { get; set; }
    public JobRunStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>Line index in the job's log where this step's output starts (inclusive).</summary>
    public long StartLine { get; set; }

    /// <summary>Line index where this step's output ends (exclusive).</summary>
    public long EndLine { get; set; }
}

/// <summary>One console line of a job log, stamped by the master when it was appended.</summary>
public readonly record struct LogLine(long Line, string TimestampUtc, int StepIndex, string Text);
