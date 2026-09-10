using System.Net;
using System.Net.Http.Json;
using InfinityCI.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>Paged per-workflow run history endpoint (GET /api/jobs/{name}/runs).</summary>
public class WorkflowRunsPageTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public WorkflowRunsPageTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "jobs"));
        File.WriteAllText(Path.Combine(_dir, "jobs", "paged.yml"),
            "name: paged\njobs:\n  a:\n    steps:\n      - command: echo hi");
        File.WriteAllText(Path.Combine(_dir, "jobs", "other.yml"),
            "name: other\njobs:\n  a:\n    steps:\n      - command: echo hi");
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
        _admin = _factory.CreateClient();
        Login().Wait();
    }

    private async Task Login()
    {
        var response = await _admin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        response.EnsureSuccessStatusCode();
    }

    private async Task TriggerAsync(string workflow)
    {
        (await _admin.PostAsync($"/api/jobs/{workflow}/trigger", null)).EnsureSuccessStatusCode();
        // Let the fast job finish so statuses settle; pagination does not care.
        await Task.Delay(500);
    }

    [Fact]
    public async Task Runs_ArePagedWithTotal_AndScopedToWorkflow()
    {
        await TriggerAsync("paged");
        await TriggerAsync("paged");
        await TriggerAsync("paged");
        await TriggerAsync("other"); // must not leak into paged's history

        var page1 = await (await _admin.GetAsync("/api/jobs/paged/runs?skip=0&take=2"))
            .Content.ReadFromJsonAsync<PagedRuns>();
        Assert.Equal(3, page1!.Total);
        Assert.Equal(2, page1.Items.Count);
        // Newest first.
        Assert.True(page1.Items[0].Run.Id > page1.Items[1].Run.Id);

        var page2 = await (await _admin.GetAsync("/api/jobs/paged/runs?skip=2&take=2"))
            .Content.ReadFromJsonAsync<PagedRuns>();
        Assert.Equal(3, page2!.Total);
        var item = Assert.Single(page2.Items);

        var page3 = await (await _admin.GetAsync("/api/jobs/paged/runs?skip=4&take=2"))
            .Content.ReadFromJsonAsync<PagedRuns>();
        Assert.Empty(page3!.Items);

        var all = await (await _admin.GetAsync("/api/jobs/paged/runs"))
            .Content.ReadFromJsonAsync<PagedRuns>();
        Assert.All(all!.Items, i => Assert.Equal("paged", i.Run.WorkflowName));
        Assert.Single(all.Items, i => i.Run.Id == item.Run.Id);
    }

    [Fact]
    public async Task UnknownWorkflow_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.GetAsync("/api/jobs/no-such/runs")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_Returns401()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/jobs/paged/runs")).StatusCode);
    }

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

public sealed record PagedRuns(int Total, List<Item> Items);
public sealed record Item(RunDto Run, List<object> Jobs);
public sealed record RunDto(long Id, string WorkflowName, string Status);
