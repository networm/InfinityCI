using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InfinityCI.Server;
using InfinityCI.Server.Scm;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>Provider webhook parsing (GitHub/GitLab/Gitea) and PR/MR trigger gating.</summary>
public class WebhookEventsTests : IDisposable
{
    // -- parser unit tests --

    private static ScmWebhookEvent Parse(Dictionary<string, string> headers, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var headerDict = new HeaderDictionary();
        foreach (var (key, value) in headers)
            headerDict[key] = value;
        return WebhookEventParser.Parse(headerDict, doc.RootElement)!;
    }

    [Fact]
    public void GitHub_Push_ExtractsBranch()
    {
        var evt = Parse(
            new() { ["X-GitHub-Event"] = "push" },
            """{"ref":"refs/heads/release/1.0"}""");
        Assert.Equal("github", evt.Provider);
        Assert.Equal("push", evt.Kind);
        Assert.Equal("release/1.0", evt.Branch);
    }

    [Fact]
    public void GitHub_PullRequest_CarriesBranchesAndAction()
    {
        var evt = Parse(
            new() { ["X-GitHub-Event"] = "pull_request" },
            """{"action":"opened","number":12,"pull_request":{"title":"Fix bug","head":{"ref":"feature/x"},"base":{"ref":"main"}}}""");
        Assert.Equal("pull_request", evt.Kind);
        Assert.Equal(12, evt.PrNumber);
        Assert.Equal("opened", evt.PrAction);
        Assert.Equal("feature/x", evt.PrSourceBranch);
        Assert.Equal("main", evt.PrTargetBranch);
        Assert.Equal("Fix bug", evt.PrTitle);
    }

    [Fact]
    public void GitHub_Ping_IsAcknowledged()
    {
        var evt = Parse(new() { ["X-GitHub-Event"] = "ping" }, """{"zen":"ok"}""");
        Assert.Equal("ping", evt.Kind);
    }

    [Fact]
    public void GitLab_MergeRequest_NormalizesActions()
    {
        var evt = Parse(
            new() { ["X-Gitlab-Event"] = "Merge Request Hook" },
            """{"object_attributes":{"iid":7,"action":"update","title":"T","source_branch":"mr","target_branch":"main"}}""");
        Assert.Equal("gitlab", evt.Provider);
        Assert.Equal("pull_request", evt.Kind);
        Assert.Equal(7, evt.PrNumber);
        Assert.Equal("synchronize", evt.PrAction);
        Assert.Equal("mr", evt.PrSourceBranch);
        Assert.Equal("main", evt.PrTargetBranch);
    }

    [Fact]
    public void GitLab_PushHook_ExtractsBranch()
    {
        var evt = Parse(
            new() { ["X-Gitlab-Event"] = "Push Hook" },
            """{"ref":"refs/heads/dev"}""");
        Assert.Equal("push", evt.Kind);
        Assert.Equal("dev", evt.Branch);
    }

    [Fact]
    public void Gitea_PullRequest_ParsesLikeGitHub()
    {
        var evt = Parse(
            new() { ["X-Gitea-Event"] = "pull_request" },
            """{"action":"opened","number":3,"pull_request":{"head":{"ref":"patch"},"base":{"ref":"main"}}}""");
        Assert.Equal("gitea", evt.Provider);
        Assert.Equal("pull_request", evt.Kind);
        Assert.Equal("patch", evt.PrSourceBranch);
    }

    [Fact]
    public void Generic_Payload_WithoutHeaders_UsesRef()
    {
        var evt = Parse(new(), """{"ref":"refs/heads/plain"}""");
        Assert.Equal("generic", evt.Provider);
        Assert.Equal("plain", evt.Branch);
    }

    // -- integration: PR trigger gating against the live endpoint --

    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;

    public WebhookEventsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "wh-job"));
        File.WriteAllText(Path.Combine(_dir, "wh-job", "workflow.yml"), """
            name: wh-job
            project: Default
            jobs:
              build:
                steps:
                  - command: echo webhook-ok
            """);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" }).Wait();
        return client;
    }

    private async Task<(HttpClient Admin, string Path)> SetupWebhookAsync(string events)
    {
        var admin = AdminClient();
        var token = await (await admin.PostAsJsonAsync("/api/jobs/wh-job/webhook-token", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var path = $"/api/webhooks/{token.GetProperty("token").GetString()}";
        (await admin.PutAsJsonAsync("/api/jobs/wh-job/webhook-config",
            new { secret = "", branches = "", events })).EnsureSuccessStatusCode();
        return (admin, path);
    }

    private static HttpResponseMessage Post(HttpClient client, string path, string body, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (headers is not null)
            foreach (var (key, value) in headers)
                request.Headers.Add(key, value);
        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    private const string GitHubPrBody = """
        {"action":"opened","number":5,"pull_request":{"title":"PR title","head":{"ref":"feature/pr"},"base":{"ref":"main"}}}
        """;

    [Fact]
    public async Task PrEvent_DisabledByDefault_IsNotTriggered()
    {
        var admin = AdminClient();
        var token = await (await admin.PostAsJsonAsync("/api/jobs/wh-job/webhook-token", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var response = Post(admin, $"/api/webhooks/{token.GetProperty("token").GetString()}", GitHubPrBody,
            new() { ["X-GitHub-Event"] = "pull_request" });

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("triggered").GetBoolean());
    }

    [Fact]
    public async Task PrEvent_Enabled_TriggersWithSourceBranchOverride()
    {
        var (admin, path) = await SetupWebhookAsync("push,pr");
        var response = Post(admin, path, GitHubPrBody, new() { ["X-GitHub-Event"] = "pull_request" });

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("triggered").GetBoolean());
        Assert.True(payload.TryGetProperty("kind", out var kind) && kind.GetString() == "pull_request",
            "response was: " + payload.GetRawText());

        // The run record carries the PR source branch as the checkout override.
        var runId = payload.GetProperty("id").GetInt64();
        var page = await (await admin.GetAsync($"/api/runs/{runId}")).Content.ReadFromJsonAsync<JsonElement>();
        var run = page.GetProperty("run");
        Assert.Equal("feature/pr", run.GetProperty("sourceBranch").GetString());
        Assert.Equal("webhook:pr/#5", run.GetProperty("triggeredBy").GetString());
    }

    [Fact]
    public async Task PrEvent_ClosedAction_IsNotTriggered()
    {
        var (admin, path) = await SetupWebhookAsync("push,pr");
        var body = """{"action":"closed","number":5,"pull_request":{"head":{"ref":"feature/pr"},"base":{"ref":"main"}}}""";
        var response = Post(admin, path, body, new() { ["X-GitHub-Event"] = "pull_request" });

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("triggered").GetBoolean());
    }

    [Fact]
    public async Task GitLab_MergeRequest_WithPlainTokenSecret_Triggers()
    {
        var (admin, path) = await SetupWebhookAsync("pr");
        (await admin.PutAsJsonAsync("/api/jobs/wh-job/webhook-config",
            new { secret = "gl-secret", branches = "", events = "pr" })).EnsureSuccessStatusCode();

        const string body = """
            {"object_attributes":{"iid":9,"action":"open","title":"MR","source_branch":"mr-src","target_branch":"main"}}
            """;
        var wrong = Post(admin, path, body, new()
        {
            ["X-Gitlab-Event"] = "Merge Request Hook",
            ["X-Gitlab-Token"] = "nope",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var right = Post(admin, path, body, new()
        {
            ["X-Gitlab-Event"] = "Merge Request Hook",
            ["X-Gitlab-Token"] = "gl-secret",
        });
        var payload = await right.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("triggered").GetBoolean());
    }

    [Fact]
    public async Task PushEvent_StillWorks_WhenPrEnabled()
    {
        var (admin, path) = await SetupWebhookAsync("push,pr");
        var response = Post(admin, path, """{"ref":"refs/heads/main"}""", new() { ["X-GitHub-Event"] = "push" });

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("triggered").GetBoolean());
        Assert.Equal("push", payload.GetProperty("kind").GetString());
    }

    public void Dispose()
    {
        try
        {
            _factory.Dispose();
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
