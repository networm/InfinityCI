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

    /// <summary>Run number, unique per workflow and incrementing from 1 (the
    /// public identity used in URLs); the internal Id keys JobRuns.</summary>
    public int RunNumber { get; set; }
    public required string WorkflowName { get; set; }
    public required string Project { get; set; }
    public string TriggeredBy { get; set; } = "";
    public RunStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Monotonic mutation counter; real-time clients drop stale updates.</summary>
    public long Version { get; set; }

    /// <summary>Parameter values used for this run (name -> value), persisted as JSON.</summary>
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>EF-mapped JSON mirror of <see cref="Params"/> (not part of the public API).</summary>
    [JsonIgnore]
    public string ParamsJson { get; set; } = "{}";

    /// <summary>Branch override for event-triggered runs (e.g. a pull request's source
    /// branch); when set it takes precedence over the scm branch/ref for checkout.</summary>
    public string? SourceBranch { get; set; }

    /// <summary>EF-mapped JSON mirror of <see cref="TriggerContext"/>.</summary>
    [JsonIgnore]
    public string TriggerContextJson { get; set; } = "{}";

    /// <summary>Normalized SCM event context for webhook-triggered runs; null on manual/cron runs.</summary>
    [JsonIgnore]
    public TriggerContext? TriggerContext { get; set; }

    public bool IsTerminal => Status is RunStatus.Success or RunStatus.Failed or RunStatus.Cancelled;
}

/// <summary>Normalized webhook event context attached to event-triggered runs.</summary>
public sealed record TriggerContext(
    string Provider,          // github | gitlab | gitea | generic
    string Event,             // push | pull_request
    long? PrNumber = null,
    string? PrAction = null,
    string? PrTitle = null,
    string? PrSourceBranch = null,
    string? PrTargetBranch = null);

/// <summary>One job of a run. Job runs of the same run execute in parallel.</summary>
public sealed class JobRun
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public required string JobKey { get; set; }
    public required string RunsOn { get; set; }     // "local" | "agent" | "agent:<label>"

    /// <summary>Project of the owning run (denormalized for visibility filtering
    /// and event fan-out without joining Runs).</summary>
    public string Project { get; set; } = "";
    public string? AgentId { get; set; }

    /// <summary>Source branch checked out by the SCM step (null when the workflow has no scm block).</summary>
    public string? SourceBranch { get; set; }

    /// <summary>Exact commit checked out by the SCM step.</summary>
    public string? CommitSha { get; set; }

    /// <summary>Job keys this job waits for (GitHub `needs`); persisted as JSON in <see cref="NeedsJson"/>.</summary>
    public List<string> Needs { get; set; } = [];
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

    /// <summary>EF-mapped JSON mirror of <see cref="Needs"/> (not part of the public API).</summary>
    [JsonIgnore]
    public string NeedsJson { get; set; } = "[]";

    public bool IsTerminal => Status is JobRunStatus.Success or JobRunStatus.Failed or JobRunStatus.Cancelled or JobRunStatus.Skipped;
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
