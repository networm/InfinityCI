using System.Globalization;
using System.Text.Json.Serialization;
using InfinityCI.Server;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Api;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Hubs;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Notifications;
using InfinityCI.Server.Realtime;
using InfinityCI.Server.Runs;
using InfinityCI.Server.Storage;
using static InfinityCI.Server.Storage.DbMigrator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Background threads (run execution, git fetch) have no Accept-Language header;
// their user-visible messages follow the server default (Chinese).
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("zh");

// gRPC requires HTTP/2; Kestrel plaintext endpoints default to HTTP/1.1, so the
// agent gRPC service gets a dedicated HTTP/2 (h2c) port while the web app keeps
// HTTP/1.1 for browsers on the main port. Note: any explicit Listen* call makes
// Kestrel ignore applicationUrl/UseUrls, so both endpoints are declared here.
// The bind address defaults to loopback for local runs; containers set
// InfinityCI__ListenHost=0.0.0.0 to accept external traffic.
// The bind address defaults to loopback for local runs; containers set
// InfinityCI__ListenHost=0.0.0.0 to accept external traffic.
var listenHost = builder.Configuration["InfinityCI:ListenHost"] ?? "127.0.0.1";
if (!System.Net.IPAddress.TryParse(listenHost, out var listenAddress))
    listenAddress = System.Net.IPAddress.Loopback;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(listenAddress, 5000);
    options.Listen(listenAddress, 5001, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext());

builder.Services.Configure<CiServerOptions>(builder.Configuration.GetSection(CiServerOptions.SectionName));
// Also expose the bound instance directly for consumers taking CiServerOptions.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CiServerOptions>>().Value);
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));

// LDAP directory login (disabled unless InfinityCI:Ldap:Enabled is true).
builder.Services.Configure<LdapOptions>(builder.Configuration.GetSection(LdapOptions.SectionName));
builder.Services.AddSingleton<LdapAuthenticator>();

builder.Services.AddDbContext<CiDbContext>((sp, db) =>
{
    var options = sp.GetRequiredService<IOptions<CiServerOptions>>().Value;
    Directory.CreateDirectory(options.DataDir);
    db.UseSqlite(options.DbConnectionString);
});

// Authentication for both audiences: browsers use the session cookie, CLI and
// machine callers use `Authorization: Bearer <api token>`. The "Smart" policy
// scheme forwards each request to the matching provider.
builder.Services
    .AddAuthentication("Smart")
    .AddPolicyScheme("Smart", "Cookie or API token", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? "ApiToken"
                : CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>("ApiToken", _ => { })
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

builder.Services.AddHttpClient("wecom");
builder.Services.AddSingleton<WorkflowControlService>();
builder.Services.AddSingleton<WeComNotifier>();
builder.Services.AddSingleton<DingTalkNotifier>();
builder.Services.AddSingleton<SlackNotifier>();
builder.Services.AddSingleton<GenericWebhookNotifier>();
builder.Services.AddSingleton<EmailNotifier>();
builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<WeComNotifier>());
builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<DingTalkNotifier>());
builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<SlackNotifier>());
builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<GenericWebhookNotifier>());
builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<EmailNotifier>());
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<InfinityCI.Server.Scm.CommitStatusReporter>();
builder.Services.AddSingleton<RunEvents>();
builder.Services.AddSingleton<ChangeEvents>();
builder.Services.AddSingleton<JobLogStore>();
builder.Services.AddSingleton<WorkflowGitStore>();
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddSingleton<WorkflowDirectory>();
builder.Services.AddSingleton<QueueSnapshot>();
builder.Services.AddScoped<RunRepository>();

// Agent infrastructure (gRPC hub, pull dispatch, lease watchdog).
builder.Services.AddGrpc();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<RemoteBuildCoordinator>();
builder.Services.AddHostedService<AgentLeaseMonitor>();

// Run engine.
builder.Services.AddSingleton<LocalJobRunQueue>();
builder.Services.AddSingleton<RunAggregator>();
builder.Services.AddSingleton<JobRunExecutor>();

// Start order matters: workflows load and recovery finishes before triggers are accepted.
builder.Services.AddHostedService<CiBroadcaster>();
// Register concrete types so the hosted instance is the SAME one consumers resolve.
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowStore>());
builder.Services.AddSingleton<RunQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunQueueService>());
// Scheduled triggers start after recovery (they await queue.Ready themselves).
builder.Services.AddHostedService<InfinityCI.Server.Scheduling.CronScheduler>();

var app = builder.Build();

// Versioned schema migrations: fresh databases are created at the latest
// version, existing ones upgraded in place — history is never wiped.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
    DbMigrator.Migrate(db);

    // EnsureCreated never alters existing tables, so new columns on older
    // databases are added by guarded migrations here.
    var connection = db.Database.GetDbConnection();
    await connection.OpenAsync();
    await using (var command = connection.CreateCommand())
    {
        command.CommandText =
            """
            SELECT COUNT(*) FROM pragma_table_info('Agents') WHERE name = 'EnvironmentJson';
            """;
        var hasColumn = Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        if (!hasColumn)
        {
            command.CommandText = "ALTER TABLE Agents ADD COLUMN EnvironmentJson TEXT NOT NULL DEFAULT '{}'";
            await command.ExecuteNonQueryAsync();
            Log.Information("Migrated Agents table: added EnvironmentJson column");
        }
    }

    if (!await db.Users.AnyAsync())
    {
        var defaultProject = new Project { Name = "Default", Description = "Default project" };
        db.Projects.Add(defaultProject);
        db.Users.Add(new User
        {
            Username = "admin",
            DisplayName = "管理员",
            PasswordHash = PasswordHasher.Hash("admin"),
            Role = AppRoles.SuperAdmin,
            Projects = { new UserProject { Project = defaultProject } },
        });
        await db.SaveChangesAsync();
        Log.Information("Seeded initial SuperAdmin 'admin' (password 'admin') and project 'Default'");
    }
}

app.UseSerilogRequestLogging();

// Pick up the UI language from the Accept-Language header sent by the SPA so
// API error/validation messages match the language selected in the UI.
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("zh"),
    SupportedCultures = [new CultureInfo("en"), new CultureInfo("zh")],
    SupportedUICultures = [new CultureInfo("en"), new CultureInfo("zh")],
});

// Serve the built SPA when present (production/published layout uses wwwroot).
// index.html must always revalidate (hashed /static assets are immutable), or
// browsers heuristically cache a stale entry page and miss new deployments.
var ciOptions = app.Services.GetRequiredService<CiServerOptions>();
var distDir = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, ciOptions.WebDistDir));
if (Directory.Exists(distDir))
{
    var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distDir);
    var cacheHeaders = (Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext ctx) =>
    {
        ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Path.StartsWithSegments("/static")
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    };
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider, OnPrepareResponse = cacheHeaders });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fileProvider, OnPrepareResponse = cacheHeaders });
}

app.UseAuthentication();
app.UseAuthorization();

// Subscribe notification channels before the server starts accepting triggers.
app.Services.GetRequiredService<NotificationDispatcher>()
    .Subscribe(app.Services.GetRequiredService<RunEvents>());

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));
app.MapHub<CiHub>("/hubs/ci");
app.MapGrpcService<AgentHubService>();
app.MapCiApi();

app.Run();

// Expose the entry point to WebApplicationFactory-based integration tests.
public partial class Program;
