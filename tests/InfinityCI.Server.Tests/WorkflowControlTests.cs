using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace InfinityCI.Server.Tests;

public class WorkflowControlTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _anon;

    public WorkflowControlTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "jobs"));
        Directory.CreateDirectory(Path.Combine(_dir, "ctl-job"));
        File.WriteAllText(Path.Combine(_dir, "ctl-job", "workflow.yml"), """
            name: ctl-job
            params:
              TARGET:
                default: ""
                required: true
            jobs:
              a:
                steps:
                  - command: echo target=%TARGET%
            """);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
        _anon = _factory.CreateClient();
        _admin = _factory.CreateClient();
        _admin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" }).Wait();
    }

    private async Task<string?> IssueTokenAsync(string workflow = "ctl-job")
    {
        var response = await _admin.PostAsync($"/api/jobs/{workflow}/webhook-token", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString();
    }

    [Fact]
    public async Task DisabledWorkflow_RejectsTriggers_ThenWorksAfterEnable()
    {
        (await _admin.PostAsJsonAsync("/api/jobs/ctl-job/enabled", new { enabled = false })).EnsureSuccessStatusCode();

        // Manual trigger → 409
        var manual = await _admin.PostAsJsonAsync("/api/jobs/ctl-job/trigger", new { @params = new { TARGET = "x" } });
        Assert.Equal(HttpStatusCode.Conflict, manual.StatusCode);

        // Webhook trigger → 409
        var token = await IssueTokenAsync();
        var webhook = await _anon.PostAsJsonAsync($"/api/webhooks/{token}", new { @params = new { TARGET = "x" } });
        Assert.Equal(HttpStatusCode.Conflict, webhook.StatusCode);

        // Re-enable → works again
        (await _admin.PostAsJsonAsync("/api/jobs/ctl-job/enabled", new { enabled = true })).EnsureSuccessStatusCode();
        var ok = await _admin.PostAsJsonAsync("/api/jobs/ctl-job/trigger", new { @params = new { TARGET = "x" } });
        ok.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Toggle_RequiresAdmin()
    {
        var response = await _admin.PostAsJsonAsync("/api/jobs/ctl-job/enabled", new { enabled = false });
        response.EnsureSuccessStatusCode(); // admin is SuperAdmin

        // Anonymous callers are rejected outright (401); authenticated non-admins get 403.
        var anonToggle = await _anon.PostAsJsonAsync("/api/jobs/ctl-job/enabled", new { enabled = true });
        Assert.True(anonToggle.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401/403, got {anonToggle.StatusCode}");
    }

    [Fact]
    public async Task WebhookTrigger_EndToEnd()
    {
        var token = await IssueTokenAsync();

        // Unknown token → 404
        Assert.Equal(HttpStatusCode.NotFound,
            (await _anon.PostAsJsonAsync("/api/webhooks/deadbeef", new { })).StatusCode);

        // Valid token + params → run created with params, triggered by webhook
        var response = await _anon.PostAsJsonAsync($"/api/webhooks/{token}", new { @params = new { TARGET = "from-hook" } });
        response.EnsureSuccessStatusCode();
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("webhook", run.GetProperty("triggeredBy").GetString());

        var runId = run.GetProperty("id").GetInt64();
        await TestEnv.WaitUntilAsync(async () =>
        {
            var fresh = await (await _admin.GetAsync($"/api/runs/{runId}")).Content.ReadFromJsonAsync<JsonElement>();
            return fresh.GetProperty("run").GetProperty("status").GetString() is "Success" or "Failed";
        }, TimeSpan.FromSeconds(30));

        var finished = await (await _admin.GetAsync($"/api/runs/{runId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Success", finished.GetProperty("run").GetProperty("status").GetString());

        // Revoke → subsequent calls 404
        (await _admin.DeleteAsync("/api/jobs/ctl-job/webhook-token")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,
            (await _anon.PostAsJsonAsync($"/api/webhooks/{token}", new { })).StatusCode);
    }

    [Fact]
    public async Task Webhook_MissingRequiredParam_Returns400()
    {
        var token = await IssueTokenAsync();
        var response = await _anon.PostAsJsonAsync($"/api/webhooks/{token}", new { @params = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _anon.Dispose();
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
