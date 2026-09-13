using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Notifications;
using Microsoft.AspNetCore.DataProtection;
using InfinityCI.Server.Runs;
using LibGit2Sharp;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Api;

public sealed record LogPage(long RunId, string JobKey, long NextLine, IReadOnlyList<LogLine> Lines);
public sealed record RunsPageItem(Run Run, IReadOnlyList<JobRun> Jobs);
public record LoginRequest(string Username, string Password);
public record CreateUserRequest(string Username, string Password, string Role, long[] ProjectIds, string? DisplayName);
public record UpdateUserRequest(string? Password, string? Role, long[]? ProjectIds, string? DisplayName);
public record TriggerRequest(Dictionary<string, string>? Params);
public record ProjectRequest(string Name, string? Description);
public record SaveWorkflowRequest(string Yaml);
public record EnrollmentRequest(string Name, string[] Labels, int MaxConcurrentBuilds);
public record RestoreRequest(string Sha);
public record CredentialRequest(string Name, string Username, string Secret);
public record FavoriteRequest(bool Favorite);
public record EnabledToggleRequest(bool Enabled);
public record NotifyWebhookRequest(string? Url);
public record WebhookTriggerRequest(Dictionary<string, string>? Params);
public record EnabledRequest(bool Enabled);

public static class CiApi
{
    public static IEndpointRouteBuilder MapCiApi(this IEndpointRouteBuilder app)
    {
        MapAuth(app);
        MapAdmin(app);
        MapCredentials(app);
        MapJobs(app);
        MapDashboard(app);
        MapWorkflowControl(app);
        MapRuns(app);
        MapAgents(app);
        MapGitClone(app);
        return app;
    }

    // -- auth --

    private static async Task<IResult> SignInAsync(CiDbContext db, HttpContext http, User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, user.Role),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Ok(new { username = user.Username, role = user.Role });
    }

    private static void MapAuth(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, CiDbContext db, HttpContext http, LdapAuthenticator ldap, ILogger<LdapAuthenticator> ldapLogger) =>
        {
            var user = await db.Users.Include(u => u.Projects).ThenInclude(up => up.Project)
                .FirstOrDefaultAsync(u => u.Username == request.Username);

            // Local accounts first: any user with a stored password hash.
            if (user is { } local
                && !string.IsNullOrEmpty(local.PasswordHash)
                && PasswordHasher.Verify(request.Password, local.PasswordHash))
            {
                return await SignInAsync(db, http, local);
            }

            // LDAP fallback: verify against the directory; first successful login
            // auto-provisions a plain User account without a local password.
            if (ldap.Enabled)
            {
                LdapUser? ldapUser;
                try
                {
                    ldapUser = ldap.Authenticate(request.Username, request.Password);
                }
                catch (Exception ex)
                {
                    // Directory unreachable/misconfigured: generic 401, details in the log.
                    ldapLogger.LogError(ex, "LDAP authentication for '{Username}' failed", request.Username);
                    return Results.Unauthorized();
                }
                if (ldapUser is not null)
                {
                    if (user is null)
                    {
                        user = new User
                        {
                            Username = request.Username.Trim(),
                            DisplayName = ldapUser.DisplayName,
                            PasswordHash = "", // LDAP-only: no local password until an admin sets one
                            Role = AppRoles.User,
                        };
                        db.Users.Add(user);
                        await db.SaveChangesAsync();
                        user = await db.Users.Include(u => u.Projects).ThenInclude(up => up.Project)
                            .FirstAsync(u => u.Id == user.Id);
                    }
                    return await SignInAsync(db, http, user);
                }
            }

            return Results.Unauthorized();
        }).AllowAnonymous();

        app.MapGet("/api/auth/config", (LdapAuthenticator ldap) =>
            Results.Ok(new { ldapEnabled = ldap.Enabled })).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).AllowAnonymous();

        app.MapGet("/api/me", async (ClaimsPrincipal user, CiDbContext db) =>
        {
            if (user.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            var account = await db.Users.FirstOrDefaultAsync(u => u.Username == user.Identity.Name);
            return Results.Ok(new
            {
                username = user.Identity.Name,
                displayName = account?.DisplayName ?? user.Identity.Name,
                role = user.FindFirst(ClaimTypes.Role)?.Value,
            });
        }).AllowAnonymous();

        // Username -> display name map for any authenticated user (UI attribution).
        app.MapGet("/api/users/names", async (CiDbContext db) =>
            Results.Ok(await db.Users.ToDictionaryAsync(
                u => u.Username,
                u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName)));
    }

    // -- users & projects --

    private static void MapAdmin(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects", async (CiDbContext db) =>
            Results.Ok(await db.Projects.OrderBy(p => p.Name).ToListAsync()));

        app.MapPost("/api/projects", async (ProjectRequest request, CiDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { message = "Project name is required." });
            if (await db.Projects.AnyAsync(p => p.Name == request.Name))
                return Results.Conflict(new { message = $"Project '{request.Name}' already exists." });
            var project = new Project { Name = request.Name.Trim(), Description = request.Description };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            return Results.Ok(project);
        }).RequireAuthorization("Admins");

        app.MapPut("/api/projects/{id:long}", async (long id, ProjectRequest request, CiDbContext db) =>
        {
            var project = await db.Projects.FindAsync([id]);
            if (project is null) return Results.NotFound();
            project.Name = request.Name.Trim();
            project.Description = request.Description;
            await db.SaveChangesAsync();
            return Results.Ok(project);
        }).RequireAuthorization("Admins");

        app.MapDelete("/api/projects/{id:long}", async (long id, WorkflowStore store, CiDbContext db) =>
        {
            var project = await db.Projects.Include(p => p.Users).FirstOrDefaultAsync(p => p.Id == id);
            if (project is null) return Results.NotFound();
            if (store.Workflows.Any(w => w.Project == project.Name))
                return Results.Conflict(new { message = $"Project '{project.Name}' still contains workflows; move or delete them first." });
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization("Admins");

        app.MapGet("/api/users", async (CiDbContext db) =>
            Results.Ok(await db.Users.Include(u => u.Projects).ThenInclude(up => up.Project)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.DisplayName,
                    u.Role,
                    // LDAP-provisioned users have no local password (empty hash).
                    hasPassword = u.PasswordHash != "",
                    projects = u.Projects.Select(up => new { up.ProjectId, up.Project.Name }),
                })
                .OrderBy(u => u.Username).ToListAsync()))
            .RequireAuthorization("SuperAdmin");

        app.MapPost("/api/users", async (CreateUserRequest request, CiDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { message = "Username and password are required." });
            if (request.Role is not (AppRoles.SuperAdmin or AppRoles.Admin or AppRoles.User))
                return Results.BadRequest(new { message = $"Unknown role '{request.Role}'." });
            if (await db.Users.AnyAsync(u => u.Username == request.Username))
                return Results.Conflict(new { message = $"User '{request.Username}' already exists." });

            var user = new User
            {
                Username = request.Username.Trim(),
                DisplayName = request.DisplayName,
                PasswordHash = PasswordHasher.Hash(request.Password),
                Role = request.Role,
            };
            await AttachProjectsAsync(db, user, request.ProjectIds);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok(new { user.Id, user.Username, user.Role });
        }).RequireAuthorization("SuperAdmin");

        app.MapPut("/api/users/{id:long}", async (long id, UpdateUserRequest request, CiDbContext db, ClaimsPrincipal actor) =>
        {
            var user = await db.Users.Include(u => u.Projects).FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();
            if (request.Role is { } role && role is not (AppRoles.SuperAdmin or AppRoles.Admin or AppRoles.User))
                return Results.BadRequest(new { message = $"Unknown role '{role}'." });

            if (request.Password is { Length: > 0 } password)
                user.PasswordHash = PasswordHasher.Hash(password);
            if (request.Role is { } r)
                user.Role = r;
            if (request.DisplayName is { } displayName)
                user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            if (request.ProjectIds is { } ids)
            {
                user.Projects.Clear();
                await AttachProjectsAsync(db, user, ids);
            }
            await db.SaveChangesAsync();
            if (user.Username == actor.Identity?.Name && user.Role != AppRoles.SuperAdmin && id.ToString() == actor.FindFirst(ClaimTypes.NameIdentifier)!.Value)
                return Results.BadRequest(new { message = "Cannot demote yourself." });
            return Results.Ok(new { user.Id, user.Username, user.Role });
        }).RequireAuthorization("SuperAdmin");

        app.MapDelete("/api/users/{id:long}", async (long id, CiDbContext db, ClaimsPrincipal actor) =>
        {
            var user = await db.Users.FindAsync([id]);
            if (user is null) return Results.NotFound();
            if (user.Username == actor.Identity?.Name)
                return Results.BadRequest(new { message = "Cannot delete yourself." });
            db.Users.Remove(user);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization("SuperAdmin");
    }

    private static async Task AttachProjectsAsync(CiDbContext db, User user, long[] projectIds)
    {
        foreach (var projectId in projectIds.Distinct())
        {
            if (await db.Projects.FindAsync([projectId]) is { } project)
                user.Projects.Add(new UserProject { ProjectId = project.Id });
        }
    }

    // -- workflows (任务) --

    private static void MapJobs(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", async (WorkflowStore store, WorkflowControlService control, CiDbContext db, ClaimsPrincipal user) =>
        {
            var visible = await VisibleProjectsAsync(user, db);
            var states = await control.GetAllAsync();
            return Results.Ok(store.Workflows
                .Where(w => visible is null || visible.Contains(w.Project))
                .OrderBy(w => w.Name)
                .Select(w => new
                {
                    w.Name,
                    w.Project,
                    enabled = states.GetValueOrDefault(w.Name, true),
                    @params = w.Params.Select(p => new { p.Name, p.Default, p.Required, p.Description }),
                    jobs = w.Jobs.Select(j => new { key = j.Key, runsOn = j.Value.RunsOn, needs = j.Value.Needs, steps = j.Value.Steps.Count }),
                }));
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{name}/raw", async (string name, WorkflowStore store, CiDbContext db, ClaimsPrincipal user) =>
        {
            if (!await CanSeeWorkflowAsync(store, db, user, name))
                return Results.NotFound();
            return store.TryGetRawYaml(name) is { } raw ? Results.Ok(new { name, yaml = raw }) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/api/jobs", async (SaveWorkflowRequest request, WorkflowStore store, CiDbContext db, ClaimsPrincipal user) =>
        {
            try
            {
                var workflow = WorkflowYaml.Parse(request.Yaml);
                if (store.TryGet(workflow.Name) is not null)
                    return Results.Conflict(new { message = $"Workflow '{workflow.Name}' already exists." });
                if (!await ProjectExistsAsync(db, workflow.Project))
                    return Results.BadRequest(new { message = $"Project '{workflow.Project}' does not exist. Create it on the Projects page first." });
                store.Save(workflow.Name, request.Yaml, await DisplayNameOfAsync(db, user));
                return Results.Ok(new { name = workflow.Name });
            }
            catch (WorkflowYamlException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization("Admins");

        app.MapPut("/api/jobs/{name}", async (string name, SaveWorkflowRequest request, WorkflowStore store, CiDbContext db, ClaimsPrincipal user) =>
        {
            try
            {
                var workflow = WorkflowYaml.Parse(request.Yaml);
                if (!await ProjectExistsAsync(db, workflow.Project))
                    return Results.BadRequest(new { message = $"Project '{workflow.Project}' does not exist. Create it on the Projects page first." });
                store.Save(name, request.Yaml, await DisplayNameOfAsync(db, user));
                return Results.Ok(new { name });
            }
            catch (WorkflowYamlException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization("Admins");

        app.MapDelete("/api/jobs/{name}", async (string name, WorkflowStore store, CiDbContext db, ClaimsPrincipal user) =>
            store.Delete(name, await DisplayNameOfAsync(db, user)) ? Results.Ok() : Results.NotFound()).RequireAuthorization("Admins");

        app.MapGet("/api/jobs/{name}/history", (string name, WorkflowStore store, WorkflowGitStore git, ClaimsPrincipal user) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            return Results.Ok(git.History(name));
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{name}/state", async (string name, WorkflowControlService control) =>
            Results.Ok(await control.GetAsync(name))).RequireAuthorization();

        app.MapPost("/api/jobs/{name}/enabled", async (string name, EnabledToggleRequest request, WorkflowControlService control, WorkflowStore store) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            await control.SetEnabledAsync(name, request.Enabled);
            return Results.Ok(new { name, enabled = request.Enabled });
        }).RequireAuthorization("Admins");

        app.MapPost("/api/jobs/{name}/webhook-token", async (string name, WorkflowControlService control, WorkflowStore store, HttpContext http) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            var token = await control.IssueWebhookTokenAsync(name);
            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            return Results.Ok(new
            {
                name,
                token,
                url = $"{baseUrl}/api/webhooks/{token}",
                curl = "curl -X POST " + baseUrl + "/api/webhooks/" + token + " -H \"Content-Type: application/json\" -d '\"params\":{{}}'",
            });
        }).RequireAuthorization("Admins");

        app.MapDelete("/api/jobs/{name}/webhook-token", async (string name, WorkflowControlService control, WorkflowStore store) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            await control.RevokeWebhookTokenAsync(name);
            return Results.Ok();
        }).RequireAuthorization("Admins");

        app.MapPut("/api/jobs/{name}/notify-webhook", async (string name, NotifyWebhookRequest request, WorkflowControlService control, WorkflowStore store) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            await control.SetNotifyWebhookUrlAsync(name, request.Url);
            return Results.Ok(new { name, url = request.Url });
        }).RequireAuthorization("Admins");

        app.MapGet("/api/jobs/{name}/blob/{sha}", (string name, string sha, WorkflowStore store, WorkflowGitStore git, ClaimsPrincipal user) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            return git.ReadAt(name, sha) is { } yaml ? Results.Ok(new { name, sha, yaml }) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{name}/restore", async (string name, RestoreRequest request, WorkflowStore store, WorkflowGitStore git, CiDbContext db, ClaimsPrincipal user) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            if (git.ReadAt(name, request.Sha) is not { } yaml)
                return Results.NotFound(new { message = $"Commit {request.Sha} has no version of '{name}'." });
            try
            {
                store.Save(name, yaml, await DisplayNameOfAsync(db, user));
                return Results.Ok(new { name, restoredFrom = request.Sha });
            }
            catch (WorkflowYamlException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization("Admins");

        app.MapPost("/api/jobs/{name}/trigger", async (string name, RunQueueService queue, WorkflowStore store, CiDbContext db, ClaimsPrincipal user, TriggerRequest? request) =>
        {
            if (!await CanSeeWorkflowAsync(store, db, user, name))
                return Results.NotFound(new { message = $"Unknown workflow '{name}'." });
            try
            {
                var displayName = await db.Users
                    .Where(u => u.Username == user.Identity!.Name)
                    .Select(u => u.DisplayName)
                    .FirstOrDefaultAsync();
                var triggeredBy = string.IsNullOrWhiteSpace(displayName) ? user.Identity?.Name ?? "anonymous" : displayName!;
                var run = await queue.TriggerAsync(name, triggeredBy, request?.Params);
                return Results.Ok(run);
            }
            catch (InvalidOperationException ex)
            {
                // Missing required params / disabled workflows surface distinctly.
                if (ex.Message.StartsWith("Missing required parameter", StringComparison.Ordinal))
                    return Results.BadRequest(new { message = ex.Message });
                if (ex is WorkflowDisabledException)
                    return Results.Conflict(new { message = ex.Message });
                return Results.NotFound(new { message = $"Unknown workflow '{name}'." });
            }
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{name}/runs", async (string name, RunRepository repo, WorkflowStore store, CiDbContext db, ClaimsPrincipal user, int skip = 0, int take = 20) =>
        {
            if (!await CanSeeWorkflowAsync(store, db, user, name))
                return Results.NotFound(new { message = $"Unknown workflow '{name}'." });
            take = take is < 1 or > 100 ? 20 : take;
            var runs = await repo.ListRunsAsync(Math.Max(0, skip), take, name);
            var total = await repo.CountRunsAsync(name);
            var jobs = await repo.GetJobRunsForAsync(runs.Select(r => r.Id));
            return Results.Ok(new
            {
                total,
                items = runs.Select(r => new RunsPageItem(r, jobs.GetValueOrDefault(r.Id, []))),
            });
        }).RequireAuthorization();
    }

    private static void MapRuns(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runs", async (RunRepository repo, CiDbContext db, ClaimsPrincipal user, int skip = 0, int take = 30) =>
        {
            var runs = await repo.ListRunsAsync(Math.Max(0, skip), take is < 1 or > 100 ? 30 : take);
            var visible = await VisibleProjectsAsync(user, db);
            if (visible is not null)
                runs = runs.Where(r => visible.Contains(r.Project)).ToList();
            var jobs = await repo.GetJobRunsForAsync(runs.Select(r => r.Id));
            return Results.Ok(runs.Select(r => new RunsPageItem(r, jobs.GetValueOrDefault(r.Id, []))));
        }).RequireAuthorization();

        app.MapGet("/api/runs/{id:long}", async (long id, RunRepository repo) =>
        {
            var run = await repo.GetRunAsync(id);
            if (run is null) return Results.NotFound();
            return Results.Ok(new RunsPageItem(run, await repo.GetJobRunsAsync(id)));
        }).RequireAuthorization();

        app.MapPost("/api/runs/{id:long}/cancel", async (long id, RunQueueService queue) =>
            Results.Ok(new { cancelled = await queue.TryCancelRunAsync(id) })).RequireAuthorization();

        app.MapPost("/api/runs/{id:long}/retry", async (long id, RunQueueService queue) =>
        {
            try
            {
                var run = await queue.RetryFromFailedAsync(id);
                return run is null ? Results.NotFound() : Results.Ok(run);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        }).RequireAuthorization();

        app.MapGet("/api/runs/{id:long}/logs/{jobKey}", async (long id, string jobKey, JobLogStore logs, long afterLine = 0, int maxLines = 20_000) =>
        {
            var lines = await logs.ReadAfterAsync(id, jobKey, afterLine, maxLines);
            var next = lines.Count > 0 ? lines[^1].Line + 1 : afterLine;
            return Results.Ok(new LogPage(id, jobKey, next, lines));
        }).RequireAuthorization();

        app.MapGet("/api/runs/{id:long}/logs/{jobKey}/download", async (long id, string jobKey, string? format, JobLogStore logs, RunRepository repo, HttpContext http) =>
        {
            var run = await repo.GetRunAsync(id);
            if (run is null) return Results.NotFound();
            var lines = await logs.ReadAfterAsync(id, jobKey, 0);
            var timestamped = string.Equals(format, "timestamped", StringComparison.OrdinalIgnoreCase);
            var content = string.Join("\n", lines.Select(l => timestamped
                ? $"{DateTimeOffset.Parse(l.TimestampUtc).ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  {l.Text}"
                : l.Text)) + "\n";
            var suffix = timestamped ? ".timestamped" : "";
            var fileName = $"{run.WorkflowName}-{id}-{jobKey}{suffix}.log";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            return Results.Text(content, "text/plain; charset=utf-8", System.Text.Encoding.UTF8);
        }).RequireAuthorization();
    }

    // -- agents --

    private static void MapAgents(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agents", (AgentRegistry registry) => Results.Ok(registry.Snapshot())).RequireAuthorization();

        app.MapGet("/api/agents/enrolled", async (CiDbContext db, AgentRegistry registry) =>
        {
            var agents = await db.Agents.OrderBy(a => a.Name).ToListAsync();
            return Results.Ok(agents.Select(a => new
            {
                a.Id,
                a.Name,
                labels = JsonSerializer.Deserialize<string[]>(a.LabelsJson) ?? [],
                a.MaxConcurrentBuilds,
                a.Enabled,
                a.EnrolledAt,
                a.LastSeenUtc,
                online = registry.Get(a.Id)?.Online == true,
            }));
        }).RequireAuthorization("Admins");

        app.MapPut("/api/agents/{id}/enabled", async (string id, EnabledRequest request, CiDbContext db, AgentRegistry registry) =>
        {
            var record = await db.Agents.FindAsync([id]);
            if (record is null) return Results.NotFound();
            record.Enabled = request.Enabled;
            await db.SaveChangesAsync();
            if (!request.Enabled)
                registry.MarkOffline(id);
            return Results.Ok(new { id, enabled = record.Enabled });
        }).RequireAuthorization("Admins");

        app.MapDelete("/api/agents/{id}", async (string id, CiDbContext db, AgentRegistry registry) =>
        {
            var record = await db.Agents.FindAsync([id]);
            if (record is null) return Results.NotFound();
            db.Agents.Remove(record);
            await db.SaveChangesAsync();
            registry.MarkOffline(id);
            return Results.Ok();
        }).RequireAuthorization("Admins");

        app.MapGet("/api/agents/enrollments", async (CiDbContext db) =>
            Results.Ok(await db.AgentEnrollments.OrderByDescending(e => e.CreatedUtc).ToListAsync()))
            .RequireAuthorization("Admins");

        app.MapPost("/api/agents/enrollments", async (EnrollmentRequest request, CiDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { message = "Enrollment name is required." });
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var enrollment = new AgentEnrollment
            {
                Token = token,
                Name = request.Name.Trim(),
                LabelsJson = JsonSerializer.Serialize(request.Labels ?? []),
                MaxConcurrentBuilds = Math.Max(1, request.MaxConcurrentBuilds),
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            db.AgentEnrollments.Add(enrollment);
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                enrollment.Id,
                enrollment.Token,
                command = $"dotnet run --project src/InfinityCI.Agent -- --Agent:EnrollToken={token} --Agent:AgentName=\"{request.Name}\"",
            });
        }).RequireAuthorization("Admins");

        app.MapDelete("/api/agents/enrollments/{id:long}", async (long id, CiDbContext db) =>
        {
            var enrollment = await db.AgentEnrollments.FindAsync([id]);
            if (enrollment is null) return Results.NotFound();
            db.AgentEnrollments.Remove(enrollment);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization("Admins");
    }

    private static async Task<string> DisplayNameOfAsync(CiDbContext db, ClaimsPrincipal user)
    {
        var name = await db.Users
            .Where(u => u.Username == user.Identity!.Name)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(name) ? user.Identity?.Name ?? "anonymous" : name;
    }

    // -- credentials (Git SCM) --

    private static void MapCredentials(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/credentials", (CredentialStore store) => Results.Ok(store.List()))
            .RequireAuthorization("Admins");

        app.MapPost("/api/credentials", (CredentialRequest request, CredentialStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Username))
                return Results.BadRequest(new { message = "Name and username are required." });
            store.Save(request.Name.Trim(), request.Username, request.Secret ?? "");
            return Results.Ok(new { name = request.Name.Trim() });
        }).RequireAuthorization("Admins");

        app.MapDelete("/api/credentials/{name}", (string name, CredentialStore store) =>
            store.Delete(name) ? Results.Ok() : Results.NotFound()).RequireAuthorization("Admins");
    }

    // -- dashboard aggregate (home page) --

    public sealed record DashboardItem(
        string Name,
        string Project,
        bool IsFavorite,
        bool Enabled,
        string? Branch,
        string? CommitSha,
        string? CommitMessage,
        string? CommitAuthor,
        DateTimeOffset? CommitWhen,
        Run? LastRun);

    private static void MapDashboard(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard", async (WorkflowStore store, WorkflowControlService control, WorkflowGitStore git, RunRepository repo, CiDbContext db, ClaimsPrincipal user) =>
        {
            var userId = long.Parse(user.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var favorites = (await db.UserFavorites.Where(f => f.UserId == userId).Select(f => f.WorkflowName).ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var visible = await VisibleProjectsAsync(user, db);

            var items = new List<DashboardItem>();
            foreach (var workflow in store.Workflows)
            {
                if (visible is not null && !visible.Contains(workflow.Project))
                    continue;
                var runtime = await control.GetAsync(workflow.Name);
                // Prefer real SCM source info from the latest checked-out job run;
                // fall back to the config repo's branch/last commit.
                var lastRun = (await repo.ListRunsAsync(0, 1, workflow.Name)).FirstOrDefault();
                string? branch = null, commitSha = null, commitMessage = null, commitAuthor = null;
                DateTimeOffset? commitWhen = null;
                if (lastRun is not null)
                {
                    var jobRuns = await repo.GetJobRunsAsync(lastRun.Id);
                    var checkedOut = jobRuns.FirstOrDefault(j => j.CommitSha is not null);
                    if (checkedOut is not null)
                    {
                        branch = checkedOut.SourceBranch;
                        commitSha = checkedOut.CommitSha;
                    }
                }
                if (commitSha is null)
                {
                    var configCommit = git.History(workflow.Name).FirstOrDefault();
                    if (configCommit is not null)
                    {
                        branch = git.BranchName();
                        commitSha = configCommit.Sha;
                        commitMessage = configCommit.Message;
                        commitAuthor = configCommit.Author;
                        commitWhen = configCommit.When;
                    }
                }
                items.Add(new DashboardItem(
                    workflow.Name, workflow.Project,
                    favorites.Contains(workflow.Name),
                    runtime.Enabled,
                    branch, commitSha, commitMessage, commitAuthor, commitWhen,
                    lastRun));
            }
            return Results.Ok(items);
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{name}/favorite", async (string name, CiDbContext db, ClaimsPrincipal user) =>
        {
            var userId = long.Parse(user.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var existing = await db.UserFavorites.FirstOrDefaultAsync(f => f.UserId == userId && f.WorkflowName == name);
            bool favorite;
            if (existing is not null)
            {
                db.UserFavorites.Remove(existing);
                favorite = false;
            }
            else
            {
                db.UserFavorites.Add(new UserFavorite { UserId = userId, WorkflowName = name });
                favorite = true;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { isFavorite = favorite });
        }).RequireAuthorization();
    }

    // -- incoming webhooks (token-authenticated triggers) --

    private static void MapWorkflowControl(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/{token}", async (string token, WebhookTriggerRequest? request,
            WorkflowControlService control, RunQueueService queue) =>
        {
            var workflowName = await control.FindByWebhookTokenAsync(token);
            if (workflowName is null)
                return Results.NotFound(new { message = "Unknown webhook token." });
            if (!await control.IsEnabledAsync(workflowName))
                return Results.Conflict(new { message = $"Workflow '{workflowName}' is disabled." });
            try
            {
                var run = await queue.TriggerAsync(workflowName, "webhook", request?.Params);
                return Results.Ok(new { run.Id, run.WorkflowName, run.Status, run.TriggeredBy });
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.StartsWith("Missing required parameter", StringComparison.Ordinal))
                    return Results.BadRequest(new { message = ex.Message });
                if (ex is WorkflowDisabledException)
                    return Results.Conflict(new { message = ex.Message });
                return Results.NotFound(new { message = $"Unknown workflow '{workflowName}'." });
            }
        }).AllowAnonymous();
    }

    // -- read-only git clone (dumb HTTP protocol) --

    private static void MapGitClone(IEndpointRouteBuilder app)
    {
        static bool CheckAuth(CiDbContext db, string? authHeader)
        {
            if (authHeader is null || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authHeader["Basic ".Length..]));
                var separator = decoded.IndexOf(':');
                if (separator < 0) return false;
                var username = decoded[..separator];
                var password = decoded[(separator + 1)..];
                var user = db.Users.FirstOrDefault(u => u.Username == username);
                return user is not null && Auth.PasswordHasher.Verify(password, user.PasswordHash);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        app.Map("/git/jobs/info/refs", async (HttpContext http, CiDbContext db, WorkflowGitStore git) =>
        {
            if (!CheckAuth(db, http.Request.Headers.Authorization))
            {
                http.Response.Headers.WWWAuthenticate = "Basic realm=infinityci";
                http.Response.StatusCode = 401;
                return;
            }
            if (git.ReadHeadRef() is not { } head)
            {
                http.Response.StatusCode = 404;
                return;
            }
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync($"{head}\trefs/heads/master\n");
        });

        app.Map("/git/jobs/HEAD", async (HttpContext http, WorkflowGitStore git) =>
        {
            await http.Response.WriteAsync("ref: refs/heads/master\n");
        }).AllowAnonymous();

        app.Map("/git/jobs/objects/info/packs", async (HttpContext http, CiDbContext db, WorkflowGitStore git) =>
        {
            if (!CheckAuth(db, http.Request.Headers.Authorization))
            {
                http.Response.Headers.WWWAuthenticate = "Basic realm=infinityci";
                http.Response.StatusCode = 401;
                return;
            }
            http.Response.ContentType = "text/plain; charset=utf-8";
            foreach (var packName in git.ReadPackNames())
            {
                await http.Response.WriteAsync("P " + packName + "\n");
            }
            await http.Response.WriteAsync("\n");
        });

        app.Map("/git/jobs/objects/{*path}", async (string path, HttpContext http, CiDbContext db, WorkflowGitStore git) =>
        {
            if (!CheckAuth(db, http.Request.Headers.Authorization))
            {
                http.Response.Headers.WWWAuthenticate = "Basic realm=infinityci";
                http.Response.StatusCode = 401;
                return;
            }
            if (!path.Contains("..") && git.ReadObjectFile(path) is { } bytes)
            {
                http.Response.ContentType = "application/octet-stream";
                await http.Response.Body.WriteAsync(bytes);
                return;
            }
            http.Response.StatusCode = 404;
        });
    }

    // -- visibility helpers --

    /// <summary>null = all projects visible (admins); otherwise the set of visible project names.</summary>
    private static async Task<HashSet<string>?> VisibleProjectsAsync(ClaimsPrincipal user, CiDbContext db)
    {
        if (IsAdmin(user))
            return null;
        var username = user.Identity?.Name ?? "";
        var names = await db.UserProjects
            .Where(up => up.User.Username == username)
            .Select(up => up.Project.Name)
            .ToListAsync();
        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> CanSeeWorkflowAsync(WorkflowStore store, CiDbContext db, ClaimsPrincipal user, string name)
    {
        var workflow = store.TryGet(name);
        if (workflow is null)
            return false;
        var visible = await VisibleProjectsAsync(user, db);
        return visible is null || visible.Contains(workflow.Project);
    }

    private static bool IsAdmin(ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.SuperAdmin) || user.IsInRole(AppRoles.Admin);

    /// <summary>Every workflow must live in a managed project; the name is matched case-insensitively.</summary>
    private static async Task<bool> ProjectExistsAsync(CiDbContext db, string projectName) =>
        await db.Projects.AnyAsync(p => p.Name == projectName);
}
