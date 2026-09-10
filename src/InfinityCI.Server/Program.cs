using System.Text.Json.Serialization;
using InfinityCI.Server;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Api;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Hubs;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Realtime;
using InfinityCI.Server.Runs;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// gRPC requires HTTP/2; Kestrel plaintext endpoints default to HTTP/1.1, so the
// agent gRPC service gets a dedicated HTTP/2 (h2c) port while the web app keeps
// HTTP/1.1 for browsers on the main port. Note: any explicit Listen* call makes
// Kestrel ignore applicationUrl/UseUrls, so both endpoints are declared here.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5000);
    options.ListenLocalhost(5001, listen => listen.Protocols = HttpProtocols.Http2);
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

// Cookie authentication: login via /api/auth/login, 401 (no redirects) for APIs.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admins", policy => policy.RequireRole(AppRoles.Admin, AppRoles.SuperAdmin));
    options.AddPolicy("SuperAdmin", policy => policy.RequireRole(AppRoles.SuperAdmin));
});

// Enums as strings everywhere (REST and SignalR) for readable payloads.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<RunEvents>();
builder.Services.AddSingleton<JobLogStore>();
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddScoped<RunRepository>();

// Agent infrastructure (gRPC hub, pull dispatch, lease watchdog).
builder.Services.AddGrpc();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<RemoteBuildCoordinator>();
builder.Services.AddHostedService<AgentLeaseMonitor>();

// Run engine.
builder.Services.AddSingleton<RunAggregator>();
builder.Services.AddSingleton<JobRunExecutor>();

// Start order matters: workflows load and recovery finishes before triggers are accepted.
builder.Services.AddHostedService<CiBroadcaster>();
// Register concrete types so the hosted instance is the SAME one consumers resolve.
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowStore>());
builder.Services.AddSingleton<RunQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunQueueService>());

var app = builder.Build();

// Schema-first for now; EF migrations arrive later in the project. Seed the
// first SuperAdmin and the Default project on a fresh database.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
    db.Database.EnsureCreated();

    if (!await db.Users.AnyAsync())
    {
        var defaultProject = new Project { Name = "Default", Description = "Default project" };
        db.Projects.Add(defaultProject);
        db.Users.Add(new User
        {
            Username = "admin",
            PasswordHash = PasswordHasher.Hash("admin"),
            Role = AppRoles.SuperAdmin,
            Projects = { new UserProject { Project = defaultProject } },
        });
        await db.SaveChangesAsync();
        Log.Information("Seeded initial SuperAdmin 'admin' (password 'admin') and project 'Default'");
    }
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

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));
app.MapHub<CiHub>("/hubs/ci");
app.MapGrpcService<AgentHubService>();
app.MapCiApi();

app.Run();

// Expose the entry point to WebApplicationFactory-based integration tests.
public partial class Program;
