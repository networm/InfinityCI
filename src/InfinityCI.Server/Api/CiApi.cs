using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Jobs;
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
public record CreateUserRequest(string Username, string Password, string Role, long[] ProjectIds);
public record UpdateUserRequest(string? Password, string? Role, long[]? ProjectIds);
public record ProjectRequest(string Name, string? Description);
public record SaveWorkflowRequest(string Yaml);
public record EnrollmentRequest(string Name, string[] Labels, int MaxConcurrentBuilds);
public record RestoreRequest(string Sha);
public record EnabledRequest(bool Enabled);

public static class CiApi
{
    public static IEndpointRouteBuilder MapCiApi(this IEndpointRouteBuilder app)
    {
        MapAuth(app);
        MapAdmin(app);
        MapJobs(app);
        MapRuns(app);
        MapAgents(app);
        MapGitClone(app);
        return app;
    }

    // -- auth --

    private static void MapAuth(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, CiDbContext db, HttpContext http) =>
        {
            var user = await db.Users.Include(u => u.Projects).ThenInclude(up => up.Project)
                .FirstOrDefaultAsync(u => u.Username == request.Username);
            if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash))
                return Results.Unauthorized();

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Role, user.Role),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(new { username = user.Username, role = user.Role });
        }).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).AllowAnonymous();

        app.MapGet("/api/me", (ClaimsPrincipal user) =>
            user.Identity?.IsAuthenticated == true
                ? Results.Ok(new { username = user.Identity.Name, role = user.FindFirst(ClaimTypes.Role)?.Value })
                : Results.Unauthorized()).AllowAnonymous();
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

        app.MapDelete("/api/projects/{id:long}", async (long id, CiDbContext db) =>
        {
            var project = await db.Projects.Include(p => p.Users).FirstOrDefaultAsync(p => p.Id == id);
            if (project is null) return Results.NotFound();
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
                    u.Role,
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
        app.MapGet("/api/jobs", async (WorkflowStore store, CiDbContext db, ClaimsPrincipal user) =>
        {
            var visible = await VisibleProjectsAsync(user, db);
            return Results.Ok(store.Workflows
                .Where(w => visible is null || visible.Contains(w.Project))
                .OrderBy(w => w.Name)
                .Select(w => new
                {
                    w.Name,
                    w.Project,
                    jobs = w.Jobs.Select(j => new { key = j.Key, runsOn = j.Value.RunsOn, needs = j.Value.Needs, steps = j.Value.Steps.Count }),
                }));
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{name}/raw", async (string name, WorkflowStore store, CiDbContext db, ClaimsPrincipal user) =>
        {
            if (!await CanSeeWorkflowAsync(store, db, user, name))
                return Results.NotFound();
            return store.TryGetRawYaml(name) is { } raw ? Results.Ok(new { name, yaml = raw }) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/api/jobs", async (SaveWorkflowRequest request, WorkflowStore store, ClaimsPrincipal user) =>
        {
            try
            {
                var workflow = WorkflowYaml.Parse(request.Yaml);
                if (store.TryGet(workflow.Name) is not null)
                    return Results.Conflict(new { message = $"Workflow '{workflow.Name}' already exists." });
                store.Save(workflow.Name, request.Yaml, user.Identity?.Name ?? "anonymous");
                return Results.Ok(new { name = workflow.Name });
            }
            catch (WorkflowYamlException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization("Admins");

        app.MapPut("/api/jobs/{name}", async (string name, SaveWorkflowRequest request, WorkflowStore store, ClaimsPrincipal user) =>
        {
            try
            {
                store.Save(name, request.Yaml, user.Identity?.Name ?? "anonymous");
                return Results.Ok(new { name });
            }
            catch (WorkflowYamlException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization("Admins");

        app.MapDelete("/api/jobs/{name}", (string name, WorkflowStore store, ClaimsPrincipal user) =>
            store.Delete(name, user.Identity?.Name ?? "anonymous") ? Results.Ok() : Results.NotFound()).RequireAuthorization("Admins");

        app.MapGet("/api/jobs/{name}/history", (string name, WorkflowStore store, WorkflowGitStore git, ClaimsPrincipal user) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            return Results.Ok(git.History(name));
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{name}/blob/{sha}", (string name, string sha, WorkflowStore store, WorkflowGitStore git, ClaimsPrincipal user) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            return git.ReadAt(name, sha) is { } yaml ? Results.Ok(new { name, sha, yaml }) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{name}/restore", (string name, RestoreRequest request, WorkflowStore store, WorkflowGitStore git, ClaimsPrincipal user) =>
        {
            if (store.TryGet(name) is null) return Results.NotFound();
            if (git.ReadAt(name, request.Sha) is not { } yaml)
                return Results.NotFound(new { message = $"Commit {request.Sha} has no version of '{name}'." });
            try
            {
                store.Save(name, yaml, user.Identity?.Name ?? "anonymous");
                return Results.Ok(new { name, restoredFrom = request.Sha });
            }
            catch (WorkflowYamlException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization("Admins");

        app.MapPost("/api/jobs/{name}/trigger", async (string name, RunQueueService queue, WorkflowStore store, CiDbContext db, ClaimsPrincipal user) =>
        {
            if (!await CanSeeWorkflowAsync(store, db, user, name))
                return Results.NotFound(new { message = $"Unknown workflow '{name}'." });
            try
            {
                var run = await queue.TriggerAsync(name, user.Identity?.Name ?? "anonymous");
                return Results.Ok(run);
            }
            catch (InvalidOperationException)
            {
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

        app.MapGet("/api/runs/{id:long}/logs/{jobKey}", async (long id, string jobKey, JobLogStore logs, long afterLine = 0, int maxLines = 20_000) =>
        {
            var lines = await logs.ReadAfterAsync(id, jobKey, afterLine, maxLines);
            var next = lines.Count > 0 ? lines[^1].Line + 1 : afterLine;
            return Results.Ok(new LogPage(id, jobKey, next, lines));
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
}
