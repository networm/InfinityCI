using System.Net;
using System.Net.Http.Json;
using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Storage;
using LibGit2Sharp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfinityCI.Server.Tests;

public class ScmIntegrationTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly string _sourceRepo = Path.Combine(Path.GetTempPath(), "infinityci-tests", Guid.NewGuid().ToString("N"), "source");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public ScmIntegrationTests()
    {
        Directory.CreateDirectory(_sourceRepo);
        using (var repo = new Repository(Repository.Init(_sourceRepo)))
        {
            File.WriteAllText(Path.Combine(_sourceRepo, "app.txt"), "source-of-truth");
            Commands.Stage(repo, "*");
            var signature = new Signature("tester", "tester@test", DateTimeOffset.Now);
            repo.Commit("initial", signature, signature);
        }

        Directory.CreateDirectory(Path.Combine(_dir, "jobs"));
        Directory.CreateDirectory(Path.Combine(_dir, "scm-job"));
        File.WriteAllText(Path.Combine(_dir, "scm-job", "workflow.yml"), $"""
            name: scm-job
            scm:
              url: {_sourceRepo.Replace('\\', '/')}
            jobs:
              build:
                steps:
                  - name: Read source
                    command: type app.txt || cat app.txt
            """);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
        _admin = _factory.CreateClient();
        _admin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" }).Wait();
    }

    [Fact]
    public async Task Run_WithScm_ChecksOutSourceIntoWorkspace()
    {
        var trigger = await _admin.PostAsync("/api/jobs/scm-job/trigger", null);
        trigger.EnsureSuccessStatusCode();
        var run = await trigger.Content.ReadFromJsonAsync<RunResponse>();

        RunResponse? finished = null;
        await TestEnv.WaitUntilAsync(async () =>
        {
            var page = await (await _admin.GetAsync($"/api/runs/{run!.Id}")).Content
                .ReadFromJsonAsync<RunPageResponse>();
            finished = page!.Run;
            return finished.IsTerminal;
        }, TimeSpan.FromSeconds(60));

        Assert.Equal("Success", finished!.Status);
        var jobPage = await (await _admin.GetAsync($"/api/runs/{run.Id}")).Content.ReadFromJsonAsync<RunPageResponse>();
        var job = Assert.Single(jobPage!.Jobs);
        Assert.NotNull(job.CommitSha);
        Assert.NotEmpty(job.CommitSha!);

        // The step actually read the checked-out file.
        var logs = await (await _admin.GetAsync($"/api/runs/{run.Id}/logs/build?afterLine=0"))
            .Content.ReadFromJsonAsync<LogPageResponse>();
        Assert.Contains(logs!.Lines, l => l.Text.Contains("source-of-truth"));
        Assert.Contains(logs.Lines, l => l.Text.Contains("checked out"));

        var logDump = string.Join(" | ", logs.Lines.Select(l => l.Text));
        Assert.True(job.SourceBranch is "master" or "main", $"branch was {job.SourceBranch}; log=[{logDump}]");
        Assert.Contains(logs.Lines, l => l.Text.Contains("checked out"));
    }

    [Fact]
    public async Task FavoriteToggle_PersistsPerUser()
    {
        // A run first, so the dashboard row carries last-build info.
        (await _admin.PostAsync("/api/jobs/scm-job/trigger", null)).EnsureSuccessStatusCode();
        await TestEnv.WaitUntilAsync(async () =>
        {
            var page = await (await _admin.GetAsync("/api/runs?take=1")).Content.ReadFromJsonAsync<List<RunPageResponse>>();
            return page is { Count: 1 } && page[0].Run.IsTerminal;
        }, TimeSpan.FromSeconds(60));

        var toggle1 = await (await _admin.PostAsync("/api/jobs/scm-job/favorite", null))
            .Content.ReadFromJsonAsync<FavoriteResponse>();
        Assert.True(toggle1!.IsFavorite);

        var dashboard = await (await _admin.GetAsync("/api/dashboard")).Content.ReadFromJsonAsync<List<DashboardRow>>();
        var row = Assert.Single(dashboard!, d => d.Name == "scm-job");
        Assert.True(row.IsFavorite);
        // Last-run and source info surfaces in the aggregate.
        Assert.NotNull(row.LastRun);

        var toggle2 = await (await _admin.PostAsync("/api/jobs/scm-job/favorite", null))
            .Content.ReadFromJsonAsync<FavoriteResponse>();
        Assert.False(toggle2!.IsFavorite);

        var after = await (await _admin.GetAsync("/api/dashboard")).Content.ReadFromJsonAsync<List<DashboardRow>>();
        Assert.False(Assert.Single(after!, d => d.Name == "scm-job").IsFavorite);
    }

    [Fact]
    public async Task CredentialStore_EncryptsSecrets_AndResolves()
    {
        var store = _factory.Services.GetRequiredService<CredentialStore>();
        store.Save("deploy-key", "git-user", "super-secret");

        var list = store.List();
        var info = Assert.Single(list, c => c.Name == "deploy-key");
        Assert.Equal("git-user", info.Username);

        var resolved = store.Resolve("deploy-key");
        Assert.Equal("super-secret", resolved!.Password);

        Assert.True(store.Delete("deploy-key"));
        Assert.Throws<InvalidOperationException>(() => store.Resolve("deploy-key"));
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(_sourceRepo)!, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}

public sealed record FavoriteResponse(bool IsFavorite);
public sealed record DashboardRow(
    string Name,
    string Project,
    bool IsFavorite,
    string? Branch,
    string? CommitSha,
    RunDto? LastRun);
