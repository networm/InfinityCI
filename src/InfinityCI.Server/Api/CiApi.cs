using System.Text;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Storage;

namespace InfinityCI.Server.Api;

public sealed record LogPage(long BuildId, long NextOffset, IReadOnlyList<LogLine> Lines);

/// <summary>REST API: read-side queries plus trigger/cancel operations.</summary>
public static class CiApi
{
    public static IEndpointRouteBuilder MapCiApi(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/jobs");
        jobs.MapGet("/", (JobStore store) => Results.Ok(store.Jobs));
        jobs.MapGet("/{name}", (string name, JobStore store) =>
            store.TryGet(name) is { } job ? Results.Ok(job) : Results.NotFound());
        jobs.MapPost("/{name}/trigger", async (string name, BuildQueueService queue, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await queue.TriggerAsync(name, ct));
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(new { message = $"Unknown job '{name}'." });
            }
        });

        var builds = app.MapGroup("/api/builds");
        builds.MapGet("/", async (BuildRepository repo, string? job, int skip = 0, int take = 50, CancellationToken ct = default) =>
            Results.Ok(await repo.ListAsync(job, Math.Max(0, skip), take is < 1 or > 200 ? 50 : take, ct)));
        builds.MapGet("/{id:long}", async (long id, BuildRepository repo, CancellationToken ct = default) =>
            await repo.GetAsync(id, ct) is { } build ? Results.Ok(build) : Results.NotFound());
        builds.MapPost("/{id:long}/cancel", async (long id, BuildQueueService queue, CancellationToken ct = default) =>
            Results.Ok(new { cancelled = await queue.TryCancelAsync(id, ct) }));
        builds.MapGet("/{id:long}/logs", async (long id, BuildLogStore logs, BuildRepository repo, long afterOffset = 0, int maxLines = 10_000, CancellationToken ct = default) =>
        {
            if (await repo.GetAsync(id, ct) is null)
                return Results.NotFound();

            var lines = await logs.ReadAfterAsync(id, afterOffset, maxLines is < 1 or > 50_000 ? 10_000 : maxLines, ct);
            var nextOffset = lines.Count > 0 ? lines[^1].Offset + Encoding.UTF8.GetByteCount(lines[^1].Text) + 1 : afterOffset;
            return Results.Ok(new LogPage(id, nextOffset, lines));
        });

        return app;
    }
}
