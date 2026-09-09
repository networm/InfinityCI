using InfinityCI.Server;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext());

builder.Services.Configure<CiServerOptions>(builder.Configuration.GetSection(CiServerOptions.SectionName));

builder.Services.AddDbContext<CiDbContext>((sp, db) =>
{
    var options = sp.GetRequiredService<IOptions<CiServerOptions>>().Value;
    Directory.CreateDirectory(options.DataDir);
    db.UseSqlite(options.DbConnectionString);
});

builder.Services.AddSingleton<BuildEvents>();
builder.Services.AddSingleton<BuildLogStore>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddScoped<BuildRepository>();

// Start order matters: jobs load before the queue consumes.
builder.Services.AddHostedService<JobStore>();
builder.Services.AddHostedService<BuildQueueService>();

var app = builder.Build();

// Schema-first for now; EF migrations arrive later in the project.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
    db.Database.EnsureCreated();
}

app.UseSerilogRequestLogging();

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.Run();
