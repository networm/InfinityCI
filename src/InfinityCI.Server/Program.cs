using System.Text.Json.Serialization;
using InfinityCI.Server;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Api;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Hubs;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Realtime;
using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// gRPC requires HTTP/2; Kestrel plaintext endpoints default to HTTP/1.1, so the
// agent gRPC service gets a dedicated HTTP/2 (h2c) port while the web app keeps
// HTTP/1.1 for browsers on the main port. Note: any explicit Listen* call makes
// Kestrel ignore applicationUrl/UseUrls, so both endpoints are declared here.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5000);
    options.ListenLocalhost(5001, listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext());

builder.Services.Configure<CiServerOptions>(builder.Configuration.GetSection(CiServerOptions.SectionName));
// Also expose the bound instance directly for consumers taking CiServerOptions.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CiServerOptions>>().Value);

builder.Services.AddDbContext<CiDbContext>((sp, db) =>
{
    var options = sp.GetRequiredService<IOptions<CiServerOptions>>().Value;
    Directory.CreateDirectory(options.DataDir);
    db.UseSqlite(options.DbConnectionString);
});

// Enums as strings everywhere (REST and SignalR) for readable payloads.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<BuildEvents>();
builder.Services.AddSingleton<BuildLogStore>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddScoped<BuildRepository>();

// Agent infrastructure (gRPC hub, pull dispatch, lease watchdog).
builder.Services.AddGrpc();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<RemoteBuildCoordinator>();
builder.Services.AddHostedService<AgentLeaseMonitor>();

// Start order matters: jobs load and recovery finishes before triggers are accepted.
builder.Services.AddHostedService<CiBroadcaster>();
// Register concrete types so the hosted instance is the SAME one consumers resolve.
builder.Services.AddSingleton<JobStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobStore>());
builder.Services.AddSingleton<BuildQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BuildQueueService>());

var app = builder.Build();

// Schema-first for now; EF migrations arrive later in the project.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
    db.Database.EnsureCreated();
}

app.UseSerilogRequestLogging();

// Serve the built SPA when present (production/published layout uses wwwroot).
var ciOptions = app.Services.GetRequiredService<CiServerOptions>();
var distDir = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, ciOptions.WebDistDir));
if (Directory.Exists(distDir))
{
    var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fileProvider });
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));
app.MapHub<CiHub>("/hubs/ci");
app.MapGrpcService<AgentHubService>();
app.MapCiApi();

app.Run();

// Expose the entry point to WebApplicationFactory-based integration tests.
public partial class Program;
