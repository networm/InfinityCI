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

    public async Task<Run> AddRunAsync(Run run, CancellationToken ct = default)
    {
        run.Version = 1;
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
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
        return run;
    }

    public async Task<List<Run>> ListRunsAsync(int skip, int take, CancellationToken ct = default) =>
        await db.Runs.AsNoTracking().OrderByDescending(x => x.Id).Skip(skip).Take(take).ToListAsync(ct);

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
