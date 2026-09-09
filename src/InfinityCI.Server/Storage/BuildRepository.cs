using System.Text.Json;
using System.Text.Json.Serialization;
using InfinityCI.Core;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Storage;

public sealed class BuildRepository(CiDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<Build> AddAsync(Build build, CancellationToken ct = default)
    {
        build.Version = 1;
        build.StepsJson = JsonSerializer.Serialize(build.Steps, JsonOptions);
        db.Builds.Add(build);
        await db.SaveChangesAsync(ct);
        return build;
    }

    /// <summary>Persists a state transition and bumps <see cref="Build.Version"/>.</summary>
    public async Task SaveTransitionAsync(Build build, CancellationToken ct = default)
    {
        build.Version++;
        build.StepsJson = JsonSerializer.Serialize(build.Steps, JsonOptions);
        db.Builds.Update(build);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Build?> GetAsync(long id, CancellationToken ct = default)
    {
        var build = await db.Builds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (build is not null)
            build.Steps = DeserializeSteps(build.StepsJson);
        return build;
    }

    public async Task<List<Build>> ListAsync(string? jobName, int skip, int take, CancellationToken ct = default)
    {
        var query = db.Builds.AsNoTracking().OrderByDescending(x => x.Id).AsQueryable();
        if (jobName is not null)
            query = query.Where(x => x.JobName == jobName);
        var builds = await query.Skip(skip).Take(take).ToListAsync(ct);
        foreach (var build in builds)
            build.Steps = DeserializeSteps(build.StepsJson);
        return builds;
    }

    /// <summary>Builds left Queued/Running by a previous server run.</summary>
    public async Task<List<Build>> ListUnfinishedAsync(CancellationToken ct = default)
    {
        var builds = await db.Builds
            .AsNoTracking()
            .Where(x => x.Status == BuildStatus.Queued || x.Status == BuildStatus.Running)
            .ToListAsync(ct);
        foreach (var build in builds)
            build.Steps = DeserializeSteps(build.StepsJson);
        return builds;
    }

    private List<BuildStepResult> DeserializeSteps(string json) =>
        JsonSerializer.Deserialize<List<BuildStepResult>>(json, JsonOptions) ?? [];
}
