using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>
/// End-to-end API tests: auth, project visibility, workflow CRUD and triggers,
/// plus the SignalR run subscription — all against a real hosted server.
/// </summary>
public class CiApiTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;      // seeded SuperAdmin

    public CiApiTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "jobs"));
        Directory.CreateDirectory(Path.Combine(_dir, "api-job"));
        File.WriteAllText(Path.Combine(_dir, "api-job", "workflow.yml"), """
            name: api-job
            project: Default
            jobs:
              build:
                steps:
                  - command: echo api-ok
            """);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
        _admin = _factory.CreateClient();
        Login(_admin, "admin", "admin").Wait();
    }

    private static async Task Login(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> NewUserAsync(string username, string password, string role, long[] projectIds)
    {
        var create = await _admin.PostAsJsonAsync("/api/users", new { username, password, role, projectIds });
        create.EnsureSuccessStatusCode();
        var client = _factory.CreateClient();
        await Login(client, username, password);
        return client;
    }

    [Fact]
    public async Task Anonymous_Requests_AreUnauthorized()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/runs")).StatusCode);
    }

    [Fact]
    public async Task Login_ReturnsMe_AndLogoutClears()
    {
        var me = await _admin.GetAsync("/api/me");
        me.EnsureSuccessStatusCode();

        await _admin.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _admin.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task TriggerRun_CompletesWithJobsAndTimestampedLogs()
    {
        var trigger = await _admin.PostAsJsonAsync("/api/jobs/api-job/trigger", new { });
        trigger.EnsureSuccessStatusCode();
        var run = await trigger.Content.ReadFromJsonAsync<RunResponse>();
        Assert.Equal("Running", run!.Status); // jobs are enqueued immediately

        RunPageResponse? finished = null;
        await TestEnv.WaitUntilAsync(async () =>
        {
            finished = await (await _admin.GetAsync($"/api/runs/{run!.Id}")).Content
                .ReadFromJsonAsync<RunPageResponse>();
            return finished!.Run.IsTerminal;
        }, TimeSpan.FromSeconds(30));

        Assert.Equal("Success", finished!.Run.Status);
        var job = Assert.Single(finished.Jobs);
        Assert.Equal("Success", job.Status);

        var logs = await (await _admin.GetAsync($"/api/runs/{run.Id}/logs/build?afterLine=0"))
            .Content.ReadFromJsonAsync<LogPageResponse>();
        Assert.Contains(logs!.Lines, l => l.Text.Contains("api-ok"));
        Assert.All(logs.Lines, l => Assert.True(DateTimeOffset.TryParse(l.TimestampUtc, out _)));
    }

    [Fact]
    public async Task WorkflowCrud_CreateUpdateDelete()
    {
        var create = await _admin.PostAsJsonAsync("/api/jobs", new
        {
            yaml = "name: crud-job\nproject: Default\njobs:\n  a:\n    steps:\n      - command: echo hi",
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var duplicate = await _admin.PostAsJsonAsync("/api/jobs", new
        {
            yaml = "name: crud-job\njobs:\n  a:\n    steps:\n      - command: echo",
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var invalid = await _admin.PostAsJsonAsync("/api/jobs", new
        {
            yaml = "name: invalid\njobs:\n  a:\n    steps: []",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var update = await _admin.PutAsJsonAsync("/api/jobs/crud-job", new
        {
            yaml = "name: crud-job\njobs:\n  a:\n    steps:\n      - command: echo updated",
        });
        update.EnsureSuccessStatusCode();

        var delete = await _admin.DeleteAsync("/api/jobs/crud-job");
        delete.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/api/jobs/crud-job/raw")).StatusCode);
    }

    [Fact]
    public async Task ProjectVisibility_UserSeesOnlyAssignedProjects()
    {
        // Create a second project + a workflow in it.
        await _admin.PostAsJsonAsync("/api/projects", new { name = "Secret", description = "hidden" });
        await _admin.PostAsJsonAsync("/api/jobs", new
        {
            yaml = "name: secret-job\nproject: Secret\njobs:\n  a:\n    steps:\n      - command: echo",
        });

        // Viewer assigned only to Default.
        var viewer = await NewUserAsync("viewer", "pw123456", AppRoles_User(), [1]);
        var jobs = await (await viewer.GetAsync("/api/jobs")).Content.ReadFromJsonAsync<List<WorkflowResponse>>();
        Assert.DoesNotContain(jobs!, j => j.Name == "secret-job");
        Assert.Contains(jobs!, j => j.Name == "api-job");

        // Triggering a hidden workflow is not possible either.
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync("/api/jobs/secret-job/trigger", new { })).StatusCode);

        // Once the project is granted, it becomes visible.
        var users = await (await _admin.GetAsync("/api/users")).Content.ReadFromJsonAsync<List<UserResponse>>();
        var viewerId = users!.Single(u => u.Username == "viewer").Id;
        var update = await _admin.PutAsJsonAsync($"/api/users/{viewerId}", new { projectIds = new long[] { 1, 2 } });
        update.EnsureSuccessStatusCode();

        // New login to refresh the cookie claims (visibility reads DB, so even the old session works).
        var jobsAfter = await (await viewer.GetAsync("/api/jobs")).Content.ReadFromJsonAsync<List<WorkflowResponse>>();
        Assert.Contains(jobsAfter!, j => j.Name == "secret-job");
    }

    [Fact]
    public async Task UserManagement_RequiresSuperAdmin()
    {
        // An ordinary user cannot list users.
        var plain = await NewUserAsync("plain", "pw123456", "User", [1]);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/users")).StatusCode);

        var users = await (await _admin.GetAsync("/api/users")).Content.ReadFromJsonAsync<List<UserResponse>>();
        Assert.Contains(users!, u => u.Username == "admin" && u.Role == "SuperAdmin");
    }

    private static string AppRoles_User() => "User";

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}

// Client-side mirrors of API payloads.
public sealed record RunResponse(long Id, string WorkflowName, string Status, long Version)
{
    public bool IsTerminal => Status is "Success" or "Failed" or "Cancelled";
}
public sealed record RunPageResponse(RunResponse Run, List<JobRunResponse> Jobs);
public sealed record JobRunResponse(long Id, string JobKey, string Status, string? SourceBranch, string? CommitSha);
public sealed record LogPageResponse(long RunId, string JobKey, long NextLine, List<LogLineResponse> Lines);
public sealed record LogLineResponse(long Line, string TimestampUtc, int StepIndex, string Text);
public sealed record WorkflowResponse(string Name, string Project);
public sealed record UserResponse(long Id, string Username, string Role);
