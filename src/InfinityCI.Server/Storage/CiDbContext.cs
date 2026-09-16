using InfinityCI.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InfinityCI.Server.Storage;

public sealed class CiDbContext(DbContextOptions<CiDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<UserProject> UserProjects => Set<UserProject>();
    public DbSet<UserFavorite> UserFavorites => Set<UserFavorite>();
    public DbSet<StoredCredential> StoredCredentials => Set<StoredCredential>();
    public DbSet<WorkflowState> WorkflowStates => Set<WorkflowState>();
    public DbSet<VersionInfo> VersionInfo => Set<VersionInfo>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<AgentRecord> Agents => Set<AgentRecord>();
    public DbSet<AgentEnrollment> AgentEnrollments => Set<AgentEnrollment>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.Role).IsRequired();
        });

        modelBuilder.Entity<Project>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).IsRequired();
        });

        modelBuilder.Entity<UserProject>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ProjectId });
            e.HasOne(x => x.User).WithMany(u => u.Projects).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Project).WithMany(p => p.Users).HasForeignKey(x => x.ProjectId);
        });

        modelBuilder.Entity<UserFavorite>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.WorkflowName }).IsUnique();
            e.Property(x => x.WorkflowName).IsRequired();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<WorkflowState>(e =>
        {
            e.HasKey(x => x.WorkflowName);
            e.HasIndex(x => x.WebhookToken).IsUnique().HasFilter("[WebhookToken] IS NOT NULL");
            e.Property(x => x.WorkflowName).IsRequired();
        });

        modelBuilder.Entity<StoredCredential>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Username).IsRequired();
            e.Property(x => x.EncryptedSecret).IsRequired();
        });

        modelBuilder.Entity<Run>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.WorkflowName).IsRequired();
            e.Property(x => x.Project).IsRequired();
            e.Property(x => x.ParamsJson).IsRequired();
            e.Ignore(x => x.Params);
            e.HasIndex(x => new { x.Project, x.Id });
            e.HasIndex(x => new { x.WorkflowName, x.RunNumber }).IsUnique();
            ConfigureDates(e.Property(x => x.CreatedAt), e.Property(x => x.StartedAt), e.Property(x => x.FinishedAt));
        });

        modelBuilder.Entity<JobRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.JobKey).IsRequired();
            e.Property(x => x.RunsOn).IsRequired();
            e.Property(x => x.Project).IsRequired();
            e.Property(x => x.StepsJson).IsRequired();
            e.Property(x => x.NeedsJson).IsRequired();
            e.HasIndex(x => new { x.RunId, x.JobKey });
            e.Ignore(x => x.Steps);
            e.Ignore(x => x.Needs);
            ConfigureDates(e.Property(x => x.CreatedAt), e.Property(x => x.StartedAt), e.Property(x => x.FinishedAt));
        });

        modelBuilder.Entity<AgentRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).IsRequired();
            e.Property(x => x.Name).IsRequired();
        });

        modelBuilder.Entity<AgentEnrollment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Token).IsUnique();
            e.Property(x => x.Token).IsRequired();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.CreatedUtc).HasConversion(
                new ValueConverter<DateTimeOffset, long>(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
        });

        modelBuilder.Entity<ApiToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.TokenHash).IsRequired();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.CreatedUtc).HasConversion(
                new ValueConverter<DateTimeOffset, long>(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            e.Property(x => x.LastUsedUtc).HasConversion(
                new ValueConverter<DateTimeOffset?, long?>(v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null, v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
        });
    }

    // SQLite has no native DateTimeOffset column type; store as unix milliseconds.
    private static void ConfigureDates(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<DateTimeOffset> required,
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<DateTimeOffset?> optional1,
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<DateTimeOffset?> optional2)
    {
        required.HasConversion(
            new ValueConverter<DateTimeOffset, long>(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
        optional1.HasConversion(
            new ValueConverter<DateTimeOffset?, long?>(v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null, v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
        optional2.HasConversion(
            new ValueConverter<DateTimeOffset?, long?>(v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null, v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
    }
}
