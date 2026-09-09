using InfinityCI.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InfinityCI.Server.Storage;

public sealed class CiDbContext(DbContextOptions<CiDbContext> options) : DbContext(options)
{
    public DbSet<Build> Builds => Set<Build>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var build = modelBuilder.Entity<Build>();
        build.HasKey(x => x.Id);
        build.Property(x => x.Id).ValueGeneratedOnAdd();
        build.Property(x => x.JobName).IsRequired();
        build.Property(x => x.StepsJson).IsRequired();
        // Version is a client-facing mutation counter (ordering for real-time UI),
        // not a DB concurrency token — writes for one build are serialized in the runner.
        build.HasIndex(x => new { x.JobName, x.Id });
        build.Ignore(x => x.Steps);

        // SQLite has no native DateTimeOffset column type; store as unix milliseconds.
        build.Property(x => x.CreatedAt).HasConversion(
            new ValueConverter<DateTimeOffset, long>(
                v => v.ToUnixTimeMilliseconds(),
                v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
        build.Property(x => x.StartedAt).HasConversion(
            new ValueConverter<DateTimeOffset?, long?>(
                v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
                v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
        build.Property(x => x.FinishedAt).HasConversion(
            new ValueConverter<DateTimeOffset?, long?>(
                v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
                v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
    }
}
