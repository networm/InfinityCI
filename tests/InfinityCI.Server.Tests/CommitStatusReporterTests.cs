using System.Net;
using InfinityCI.Core;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Scm;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Tests;

/// <summary>Commit status reporting: provider routing, auth headers and dedup.</summary>
public class CommitStatusReporterTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string? Auth, string? GitLabToken, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("PRIVATE-TOKEN", out var values) ? values.FirstOrDefault() : null,
                body));
            return respond(request);
        }
    }

    private static (CommitStatusReporter Reporter, StubHandler Handler) CreateReporter(string? credentialName = null, string cloneUrl = "https://github.com/acme/app")
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK));
        var services = new ServiceCollection();
        var dir = Path.Combine(Path.GetTempPath(), "csr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        services.AddDbContext<CiDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir, "t.db")}"));
        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<CiDbContext>().Database.EnsureCreated();

        var credentialStore = new CredentialStore(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EphemeralDataProtectionProvider());
        if (credentialName is not null)
            credentialStore.Save(credentialName, "ci-bot", "gh_s3cret");

        var reporter = new CommitStatusReporter(
            new StubHttpClientFactory(new HttpClient(handler)),
            credentialStore,
            Options.Create(new CiServerOptions { PublicOrigin = "https://ci.example.com" }),
            NullLogger<CommitStatusReporter>.Instance);
        return (reporter, handler);
    }

    private static ScmConfig Scm(string url = "https://github.com/acme/app", string? credentials = "token") =>
        new() { Url = url, Credentials = credentials, CommitStatus = true };

    [Fact]
    public async Task GitHub_Success_PostedToApiWithBearerAndContext()
    {
        var (reporter, handler) = CreateReporter("token");
        await reporter.ReportFinalAsync(Scm(), 1, "demo", 7, "abc123", RunStatus.Success);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/acme/app/statuses/abc123", request.Url);
        Assert.Equal("Bearer gh_s3cret", request.Auth);
        Assert.Contains("\"state\":\"success\"", request.Body);
        Assert.Contains("\"context\":\"ci/infinity/demo\"", request.Body);
        Assert.Contains("target_url\":\"https://ci.example.com/runs/demo/7", request.Body);
    }

    [Fact]
    public async Task GitHubEnterprise_StripsGitSuffix_AndUsesV3()
    {
        var (reporter, handler) = CreateReporter("token");
        await reporter.ReportPendingAsync(Scm("https://ghe.corp/team/app.git"), 1, "demo", 1, "abc123");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ghe.corp/api/v3/repos/team/app/statuses/abc123", request.Url);
        Assert.Contains("\"state\":\"pending\"", request.Body);
    }

    [Fact]
    public async Task GitLab_UsesPipelineEndpoint_AndPrivateToken()
    {
        var (reporter, handler) = CreateReporter("token", cloneUrl: "https://gitlab.corp/group/app");
        await reporter.ReportFinalAsync(Scm("https://gitlab.corp/group/app"), 2, "demo", 3, "def456", RunStatus.Failed);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://gitlab.corp/api/v4/projects/group%2Fapp/statuses/def456?", request.Url);
        Assert.Contains("state=failed", request.Url);
        Assert.Contains("name=ci%2Finfinity%2Fdemo", request.Url);
        Assert.Contains("target_url=https%3A%2F%2Fci.example.com%2Fruns%2Fdemo%2F3", request.Url);
        Assert.Equal("gh_s3cret", request.GitLabToken);
    }

    [Fact]
    public async Task DuplicateStates_AreSuppressed_PendingThenFinalSentTwice()
    {
        var (reporter, handler) = CreateReporter("token");
        var scm = Scm();
        await reporter.ReportPendingAsync(scm, 9, "demo", 1, "sha");
        await reporter.ReportPendingAsync(scm, 9, "demo", 1, "sha"); // duplicate pending
        await reporter.ReportFinalAsync(scm, 9, "demo", 1, "sha", RunStatus.Success);
        await reporter.ReportFinalAsync(scm, 9, "demo", 1, "sha", RunStatus.Success); // duplicate final

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"state\":\"pending\"", handler.Requests[0].Body);
        Assert.Contains("\"state\":\"success\"", handler.Requests[1].Body);
    }

    [Fact]
    public async Task CancelledRun_IsReportedAsFailure()
    {
        var (reporter, handler) = CreateReporter("token");
        await reporter.ReportFinalAsync(Scm(), 4, "demo", 4, "sha", RunStatus.Cancelled);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"state\":\"failure\"", request.Body);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
