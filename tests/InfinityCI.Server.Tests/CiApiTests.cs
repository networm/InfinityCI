using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Api;
using InfinityCI.Server.Builds;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfinityCI.Server.Tests;

public class CiApiTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CiApiTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "jobs"));
        File.WriteAllText(Path.Combine(_dir, "jobs", "api-job.yml"),
            "name: api-job\ndescription: rest test\nsteps:\n  - command: echo rest-ok");
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
        _client = _factory.CreateClient();
    }

    private BuildQueueService Queue() => _factory.Services.GetRequiredService<BuildQueueService>();

    [Fact]
    public async Task GetJobs_ReturnsParsedDefinitions()
    {
        var jobs = await _client.GetFromJsonAsync<List<JobDefinitionResponse>>("/api/jobs");
        var job = Assert.Single(jobs!);
        Assert.Equal("api-job", job.Name);
        Assert.Single(job.Steps);
    }

    [Fact]
    public async Task GetUnknownJob_Returns404()
    {
        var response = await _client.GetAsync("/api/jobs/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Trigger_Build_CompletesAndLogsArePaged()
    {
        var trigger = await _client.PostAsync("/api/jobs/api-job/trigger", null);
        Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);
        var build = await trigger.Content.ReadFromJsonAsync<BuildResponse>();
        Assert.NotNull(build);
        Assert.Equal(BuildStatus.Queued, build!.Status); // returned snapshot is the queued state

        // Wait for terminal state via the REST API.
        BuildResponse? finished = null;
        await TestEnv.WaitUntilAsync(async () =>
        {
            finished = await _client.GetFromJsonAsync<BuildResponse>($"/api/builds/{build!.Id}");
            return finished is { IsTerminal: true };
        }, TimeSpan.FromSeconds(30));
        Assert.Equal(BuildStatus.Success, finished!.Status);
        Assert.Single(finished.Steps);

        // Log paging with cursor semantics: full read, then a resumed read.
        var page1 = await _client.GetFromJsonAsync<LogPage>($"/api/builds/{build.Id}/logs?afterOffset=0");
        Assert.Contains(page1!.Lines, l => l.Text.Contains("rest-ok"));
        Assert.True(page1.NextOffset > 0);

        var page2 = await _client.GetFromJsonAsync<LogPage>($"/api/builds/{build.Id}/logs?afterOffset={page1.NextOffset}");
        Assert.Empty(page2!.Lines);
    }

    [Fact]
    public async Task CancelQueuedBuild_Succeeds()
    {
        var trigger = await _client.PostAsync("/api/jobs/api-job/trigger", null);
        var build = await trigger.Content.ReadFromJsonAsync<BuildResponse>();

        // Racing the executor: cancel may land while queued or running; either way the
        // build must reach a terminal state.
        await _client.PostAsync($"/api/builds/{build!.Id}/cancel", null);
        await TestEnv.WaitUntilAsync(async () =>
        {
            var fresh = await _client.GetFromJsonAsync<BuildResponse>($"/api/builds/{build!.Id}");
            return fresh is { IsTerminal: true };
        }, TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        _client.Dispose();
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

// Client-side mirrors of API payloads (enums as strings per server config).
public sealed record JobDefinitionResponse(string Name, string? Description, List<StepResponse> Steps);
public sealed record StepResponse(string Name, string Command);
public sealed record BuildResponse(
    long Id,
    string JobName,
    [property: JsonPropertyName("status")] BuildStatus Status,
    int? ExitCode,
    long Version,
    List<BuildStepResponse> Steps)
{
    public bool IsTerminal => Status is BuildStatus.Success or BuildStatus.Failed or BuildStatus.Cancelled;
}
public sealed record BuildStepResponse(string Name, string Status, int? ExitCode);
