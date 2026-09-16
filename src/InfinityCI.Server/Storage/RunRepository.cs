using System.Text.Json;
using System.Text.Json.Serialization;
using InfinityCI.Core;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Storage;

/// <summary>Persistence for runs, job runs, users, projects and agent records.</summary>
public sealed class RunRepository(CiDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // -- runs --

    /// <summary>
    /// Inserts the run with its per-workflow sequence number (MAX+1 inside a
    /// serializable transaction; the (WorkflowName, RunNumber) unique index
    /// guarantees no duplicates even under concurrent triggers).
    /// </summary>
    public async Task<Run> AddRunAsync(Run run, CancellationToken ct = default)
    {
        run.Version = 1;
        run.ParamsJson = JsonSerializer.Serialize(run.Params);
        run.TriggerContextJson = JsonSerializer.Serialize(run.TriggerContext);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var max = await db.Runs.AsNoTracking()
            .Where(r => r.WorkflowName == run.WorkflowName)
            .Select(r => (int?)r.RunNumber)
            .MaxAsync(ct) ?? 0;
        run.RunNumber = max + 1;
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return run;
    }

    public async Task SaveRunTransitionAsync(Run run, CancellationToken ct = default)
    {
        run.Version++;
        db.Runs.Update(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Run?> GetRunAsync(long id, CancellationToken ct = default)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (run is not null)
            RestoreRun(run);
        return run;
    }

    /// <summary>Looks a run up by its public identity: workflow + per-workflow number.</summary>
    public async Task<Run?> GetRunAsync(string workflowName, int runNumber, CancellationToken ct = default)
    {
        var run = await db.Runs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowName == workflowName && x.RunNumber == runNumber, ct);
        if (run is not null)
            RestoreRun(run);
        return run;
    }

    private static void RestoreRun(Run run)
    {
        run.Params = JsonSerializer.Deserialize<Dictionary<string, string>>(run.ParamsJson) ?? new(StringComparer.OrdinalIgnoreCase);
        run.TriggerContext = run.TriggerContextJson is "{}" or ""
            ? null
            : JsonSerializer.Deserialize<TriggerContext>(run.TriggerContextJson);
    }

    public async Task<List<Run>> ListRunsAsync(int skip, int take, string? workflowName = null, CancellationToken ct = default)
    {
        var query = db.Runs.AsNoTracking().OrderByDescending(x => x.Id).AsQueryable();
        if (workflowName is not null)
            query = query.Where(x => x.WorkflowName == workflowName);
        return await query.Skip(skip).Take(take).ToListAsync(ct);
    }

    /// <summary>Total run count for pagination, optionally scoped to one workflow.</summary>
    public async Task<int> CountRunsAsync(string? workflowName = null, CancellationToken ct = default)
    {
        var query = db.Runs.AsNoTracking().AsQueryable();
        if (workflowName is not null)
            query = query.Where(x => x.WorkflowName == workflowName);
        return await query.CountAsync(ct);
    }

    public async Task<Dictionary<long, List<JobRun>>> GetJobRunsForAsync(IEnumerable<long> runIds, CancellationToken ct = default)
    {
        var ids = runIds.ToArray();
        var jobRuns = await db.JobRuns.AsNoTracking().Where(j => ids.Contains(j.RunId)).OrderBy(j => j.Id).ToListAsync(ct);
        foreach (var jobRun in jobRuns)
            Restore(jobRun);
        return jobRuns.GroupBy(j => j.RunId).ToDictionary(g => g.Key, g => g.ToList());
    }

    // -- job runs --

    public async Task<JobRun> AddJobRunAsync(JobRun jobRun, CancellationToken ct = default)
    {
        jobRun.Version = 1;
        jobRun.StepsJson = JsonSerializer.Serialize(jobRun.Steps, JsonOptions);
        jobRun.NeedsJson = JsonSerializer.Serialize(jobRun.Needs, JsonOptions);
        db.JobRuns.Add(jobRun);
        await db.SaveChangesAsync(ct);
        return jobRun;
    }

    public async Task SaveJobRunTransitionAsync(JobRun jobRun, CancellationToken ct = default)
    {
        jobRun.Version++;
        jobRun.StepsJson = JsonSerializer.Serialize(jobRun.Steps, JsonOptions);
        db.JobRuns.Update(jobRun);
        await db.SaveChangesAsync(ct);
    }

    public async Task<JobRun?> GetJobRunAsync(long id, CancellationToken ct = default)
    {
        var jobRun = await db.JobRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (jobRun is not null)
            Restore(jobRun);
        return jobRun;
    }

    private void Restore(JobRun jobRun)
    {
        jobRun.Steps = DeserializeSteps(jobRun.StepsJson);
        jobRun.Needs = JsonSerializer.Deserialize<List<string>>(jobRun.NeedsJson, JsonOptions) ?? [];
    }

    public async Task<List<JobRun>> GetJobRunsAsync(long runId, CancellationToken ct = default)
    {
        var jobRuns = await db.JobRuns.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id).ToListAsync(ct);
        foreach (var jobRun in jobRuns)
            Restore(jobRun);
        return jobRuns;
    }

    public async Task<List<JobRun>> ListUnfinishedJobRunsAsync(CancellationToken ct = default)
    {
        var jobRuns = await db.JobRuns.AsNoTracking()
            .Where(x => x.Status == JobRunStatus.Queued || x.Status == JobRunStatus.Running)
            .ToListAsync(ct);
        foreach (var jobRun in jobRuns)
            Restore(jobRun);
        return jobRuns;
    }

    /// <summary>Runs whose state is inconsistent with their job runs (e.g. after a crash).</summary>
    public async Task<List<Run>> ListNonTerminalRunsWithTerminalJobsAsync(CancellationToken ct = default)
    {
        return await db.Runs.AsNoTracking()
            .Where(r => r.Status != RunStatus.Success && r.Status != RunStatus.Failed && r.Status != RunStatus.Cancelled)
            .ToListAsync(ct);
    }

    private List<JobStepResult> DeserializeSteps(string json) =>
        JsonSerializer.Deserialize<List<JobStepResult>>(json, JsonOptions) ?? [];
}
