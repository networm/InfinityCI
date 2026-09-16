using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using InfinityCI.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>
/// Tests for the trigger/auth features: user API tokens, signed push webhooks
/// with branch filtering, `if: always()` jobs, step retry and step timeouts.
/// </summary>
public class NewFeaturesTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;

    public NewFeaturesTests()
    {
        WriteWorkflow("nf-job", """
            name: nf-job
            project: Default
            jobs:
              build:
                steps:
                  - command: echo nf-ok
            """);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
    }

    private void WriteWorkflow(string name, string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_dir, name));
        File.WriteAllText(Path.Combine(_dir, name, "workflow.yml"), yaml);
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" }).Wait();
        return client;
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

    // -- API tokens --

    [Fact]
    public async Task ApiToken_AuthenticatesBearerRequests()
    {
        var admin = AdminClient();
        var created = await admin.PostAsJsonAsync("/api/tokens", new { name = "ci-cli" });
        created.EnsureSuccessStatusCode();
        var payload = await created.Content.ReadFromJsonAsync<TokenCreated>();
        Assert.StartsWith("ifc_", payload!.Token);

        // Bearer token works without any cookie.
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Add("Authorization", $"Bearer {payload.Token}");
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/me")).StatusCode);

        // Garbage tokens do not authenticate.
        var bad = _factory.CreateClient();
        bad.DefaultRequestHeaders.Add("Authorization", "Bearer ifc_0000000000000000000000000000000000000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("/api/runs")).StatusCode);

        // Revocation kills the token immediately.
        var revoke = await admin.DeleteAsync($"/api/tokens/{payload.Id}");
        revoke.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/runs")).StatusCode);
    }

    [Fact]
    public async Task ApiTokens_AreScopedToTheirOwner()
    {
        var admin = AdminClient();
        var created = await admin.PostAsJsonAsync("/api/tokens", new { name = "admins-token" });
        var payload = await created.Content.ReadFromJsonAsync<TokenCreated>();

        // Another user creates and lists tokens — admin's token is invisible.
        var plain = _factory.CreateClient();
        await plain.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        var viewer = await plain.PostAsJsonAsync("/api/users", new { username = "tokuser", password = "pw123456", role = "User", projectIds = new[] { 1L } });
        viewer.EnsureSuccessStatusCode();
        var viewerLogin = _factory.CreateClient();
        await viewerLogin.PostAsJsonAsync("/api/auth/login", new { username = "tokuser", password = "pw123456" });

        var viewerTokens = await (await viewerLogin.GetAsync("/api/tokens")).Content.ReadFromJsonAsync<List<TokenRow>>();
        Assert.DoesNotContain(viewerTokens!, t => t.Name == "admins-token");

        // A user cannot delete someone else's token.
        Assert.Equal(HttpStatusCode.NotFound, (await viewerLogin.DeleteAsync($"/api/tokens/{payload!.Id}")).StatusCode);
    }

    // -- push webhooks: signature + branch filter --

    [Fact]
    public async Task PushWebhook_VerifiesSignatureAndFiltersBranches()
    {
        var admin = AdminClient();

        var tokenResponse = await admin.PostAsJsonAsync("/api/jobs/nf-job/webhook-token", new { });
        tokenResponse.EnsureSuccessStatusCode();
        var tokenPayload = await tokenResponse.Content.ReadFromJsonAsync<WebhookTokenCreated>();

        var config = await admin.PutAsJsonAsync("/api/jobs/nf-job/webhook-config", new { secret = "s3cret", branches = "main,release/*" });
        config.EnsureSuccessStatusCode();

        string Signed(string body)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("s3cret"));
            return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        }
        static async Task<HttpResponseMessage> Post(HttpClient client, string path, string body, Dictionary<string, string>? headers = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (headers is not null)
                foreach (var (key, value) in headers)
                    request.Headers.Add(key, value);
            return await client.SendAsync(request);
        }

        // Wrong signature → unauthorized.
        var badSignature = await Post(admin, $"/api/webhooks/{tokenPayload!.Token}", """{"ref":"refs/heads/main"}""",
            new Dictionary<string, string> { ["X-Signature"] = "sha256=deadbeef" });
        Assert.Equal(HttpStatusCode.Unauthorized, badSignature.StatusCode);

        // Valid signature, unmatched branch → acknowledged, not triggered.
        var unmatched = await Post(admin, $"/api/webhooks/{tokenPayload!.Token}", """{"ref":"refs/heads/dev"}""",
            new Dictionary<string, string> { ["X-Signature"] = Signed("""{"ref":"refs/heads/dev"}""") });
        unmatched.EnsureSuccessStatusCode();
        Assert.False((await unmatched.Content.ReadFromJsonAsync<WebhookResult>())!.Triggered);

        // GitHub ping → acknowledged, not triggered.
        var ping = await Post(admin, $"/api/webhooks/{tokenPayload!.Token}", "{}",
            new Dictionary<string, string> { ["X-Signature"] = Signed("{}"), ["X-GitHub-Event"] = "ping" });
        ping.EnsureSuccessStatusCode();
        Assert.False((await ping.Content.ReadFromJsonAsync<WebhookResult>())!.Triggered);

        // Valid signature + matching (wildcard) branch → triggered.
        var matched = await Post(admin, $"/api/webhooks/{tokenPayload!.Token}", """{"ref":"refs/heads/release/1.0"}""",
            new Dictionary<string, string> { ["X-Hub-Signature-256"] = Signed("""{"ref":"refs/heads/release/1.0"}""") });
        matched.EnsureSuccessStatusCode();
        Assert.True((await matched.Content.ReadFromJsonAsync<WebhookResult>())!.Triggered);
    }

    // -- if: always() --

    [Fact]
    public async Task AlwaysJob_RunsWhenDependencyFails_WhilePlainDependentsSkip()
    {
        WriteWorkflow("nf-always", """
            name: nf-always
            project: Default
            jobs:
              broken:
                steps:
                  - command: exit 1
              cleanup:
                needs: [broken]
                if: always()
                steps:
                  - command: echo cleaned
              skippedjob:
                needs: [broken]
                steps:
                  - command: echo never
            """);

        var run = await (await AdminClient().PostAsJsonAsync("/api/jobs/nf-always/trigger", new { })).Content
            .ReadFromJsonAsync<RunResponse>();
        RunPageResponse? finished = null;
        await TestEnv.WaitUntilAsync(async () =>
        {
            finished = await (await AdminClient().GetAsync($"/api/runs/{run!.Id}")).Content
                .ReadFromJsonAsync<RunPageResponse>();
            return finished!.Run.IsTerminal;
        }, TimeSpan.FromSeconds(30));

        Assert.Equal("Failed", finished!.Run.Status);
        Assert.Equal("Success", finished.Jobs.Single(j => j.JobKey == "cleanup").Status);
        Assert.Equal("Skipped", finished.Jobs.Single(j => j.JobKey == "skippedjob").Status);
    }

    // -- step retry --

    [Fact]
    public async Task StepRetry_RerunsFailedSteps()
    {
        WriteWorkflow("nf-retry", """
            name: nf-retry
            project: Default
            jobs:
              build:
                steps:
                  - name: Flaky
                    command: exit 1
                    retry: 2
            """);

        var run = await (await AdminClient().PostAsJsonAsync("/api/jobs/nf-retry/trigger", new { })).Content
            .ReadFromJsonAsync<RunResponse>();
        RunPageResponse? finished = null;
        var logs = "";
        await TestEnv.WaitUntilAsync(async () =>
        {
            var page = await (await AdminClient().GetAsync($"/api/runs/{run!.Id}")).Content
                .ReadFromJsonAsync<RunPageResponse>();
            finished = page;
            var logPage = await (await AdminClient().GetAsync($"/api/runs/{run!.Id}/logs/build?afterLine=0"))
                .Content.ReadFromJsonAsync<LogPageResponse>();
            logs = string.Join("\n", logPage!.Lines.Select(l => l.Text));
            return finished!.Run.IsTerminal;
        }, TimeSpan.FromSeconds(30));

        Assert.Equal("Failed", finished!.Run.Status);
        Assert.Contains("attempt 1/3 failed", logs);
        Assert.Contains("attempt 2/3 failed", logs);
    }

    // -- step timeout --

    [Fact]
    public async Task StepTimeout_KillsAndFailsTheStep()
    {
        WriteWorkflow("nf-timeout", """
            name: nf-timeout
            project: Default
            jobs:
              build:
                steps:
                  - name: Slow
                    command: ping -n 30 127.0.0.1 > nul
                    timeout_seconds: 2
            """);

        var run = await (await AdminClient().PostAsJsonAsync("/api/jobs/nf-timeout/trigger", new { })).Content
            .ReadFromJsonAsync<RunResponse>();
        RunPageResponse? finished = null;
        var logs = "";
        await TestEnv.WaitUntilAsync(async () =>
        {
            var page = await (await AdminClient().GetAsync($"/api/runs/{run!.Id}")).Content
                .ReadFromJsonAsync<RunPageResponse>();
            finished = page;
            var logPage = await (await AdminClient().GetAsync($"/api/runs/{run!.Id}/logs/build?afterLine=0"))
                .Content.ReadFromJsonAsync<LogPageResponse>();
            logs = string.Join("\n", logPage!.Lines.Select(l => l.Text));
            return finished!.Run.IsTerminal;
        }, TimeSpan.FromSeconds(30));

        Assert.Equal("Failed", finished!.Run.Status);
        Assert.Contains("timed out", logs);
    }

    // -- hub visibility --

    [Fact]
    public async Task Hub_RejectsHiddenRunsAndFiltersSnapshots()
    {
        // Hidden project + workflow inside it.
        await AdminClient().PostAsJsonAsync("/api/projects", new { name = "NF-Secret", description = "hidden" });
        WriteWorkflow("nf-secret", """
            name: nf-secret
            project: NF-Secret
            jobs:
              a:
                steps:
                  - command: echo secret
            """);
        // The workflow file is hot-reloaded with a debounce — wait for it to appear.
        await TestEnv.WaitUntilAsync(async () =>
        {
            var jobs = await (await AdminClient().GetAsync("/api/jobs")).Content.ReadFromJsonAsync<List<WorkflowResponse>>();
            return jobs!.Any(j => j.Name == "nf-secret");
        }, TimeSpan.FromSeconds(10));
        var hiddenRun = await (await AdminClient().PostAsJsonAsync("/api/jobs/nf-secret/trigger", new { })).Content
            .ReadFromJsonAsync<RunResponse>();

        // A viewer assigned only to Default (project 1).
        var admin = AdminClient();
        await admin.PostAsJsonAsync("/api/users", new { username = "nfviewer", password = "pw123456", role = "User", projectIds = new[] { 1L } });
        var viewer = _factory.CreateClient();
        await viewer.PostAsJsonAsync("/api/auth/login", new { username = "nfviewer", password = "pw123456" });

        // REST: hidden run detail is 404 even though it exists.
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/runs/{hiddenRun!.Id}")).StatusCode);
        // Visible run detail still works for the viewer (create one in Default).
        var visibleRun = await (await viewer.PostAsJsonAsync("/api/jobs/nf-job/trigger", new { })).Content
            .ReadFromJsonAsync<RunResponse>();
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/runs/{visibleRun!.Id}")).StatusCode);

        // Hub: hidden run subscription is rejected; dashboard snapshot filters.
        // The login cookie must be captured from the raw handler response.
        var loginClient = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
        var login = await loginClient.PostAsJsonAsync("/api/auth/login", new { username = "nfviewer", password = "pw123456" });
        var cookieValue = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/ci", Microsoft.AspNetCore.Http.Connections.HttpTransportType.ServerSentEvents | Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => new CookieHeaderHandler(cookieValue) { InnerHandler = _factory.Server.CreateHandler() };
            })
            .Build();
        await connection.StartAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync<InfinityCI.Server.Hubs.RunSubscription>("SubscribeRun", hiddenRun!.Id));

        var dashboard = await connection.InvokeAsync<List<RunsPageItemDto>>("SubscribeDashboard", 0, 30);
        Assert.DoesNotContain(dashboard, i => i.Run.WorkflowName == "nf-secret");
        Assert.Contains(dashboard, i => i.Run.WorkflowName == "nf-job");
        await connection.DisposeAsync();
    }

    private sealed class CookieHeaderHandler(string cookie) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

// Client-side mirrors of API payloads.
public sealed record TokenCreated(long Id, string Name, string Token);
public sealed record TokenRow(long Id, string Name);
public sealed record WebhookTokenCreated(string Token, string Url);
public sealed record WebhookResult(bool Triggered);
public sealed record RunsPageItemDto(NfRunDto Run);
public sealed record NfRunDto(long Id, string WorkflowName, string Project);
