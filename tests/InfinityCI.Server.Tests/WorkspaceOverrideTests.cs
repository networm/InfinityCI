using System.Net.Http.Json;
using System.Text.Json;
using InfinityCI.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>Runtime-state working-directory override: API roundtrip and real execution.</summary>
public class WorkspaceOverrideTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;

    public WorkspaceOverrideTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ws-job"));
        File.WriteAllText(Path.Combine(_dir, "ws-job", "workflow.yml"), """
            name: ws-job
            project: Default
            jobs:
              build:
                steps:
                  - command: echo ws-ok
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

    [Fact]
    public async Task SetWorkspaceDir_RoundtripsThroughState_AndClearsOnEmpty()
    {
        var admin = AdminClient();

        var put = await admin.PutAsJsonAsync("/api/jobs/ws-job/workspace", new { workspaceDir = "custom-ws" });
        put.EnsureSuccessStatusCode();
        var state = await (await admin.GetAsync("/api/jobs/ws-job/state")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("custom-ws", state.GetProperty("workspaceDir").GetString());

        var cleared = await admin.PutAsJsonAsync("/api/jobs/ws-job/workspace", new { workspaceDir = "" });
        cleared.EnsureSuccessStatusCode();
        var after = await (await admin.GetAsync("/api/jobs/ws-job/state")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(after.GetProperty("workspaceDir").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task Run_WithWorkspaceOverride_ExecutesInConfiguredDirectory()
    {
        var admin = AdminClient();
        (await admin.PutAsJsonAsync("/api/jobs/ws-job/workspace", new { workspaceDir = "custom-ws" })).EnsureSuccessStatusCode();

        var trigger = await admin.PostAsJsonAsync("/api/jobs/ws-job/trigger", new { });
        trigger.EnsureSuccessStatusCode();
        var runId = (await trigger.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        await TestEnv.WaitUntilAsync(async () =>
        {
            var page = await (await admin.GetAsync($"/api/runs/{runId}")).Content.ReadFromJsonAsync<JsonElement>();
            var status = page.GetProperty("run").GetProperty("status").GetString();
            return status is "Success" or "Failed" or "Cancelled";
        }, TimeSpan.FromSeconds(30));

        var page = await (await admin.GetAsync($"/api/runs/{runId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Success", page.GetProperty("run").GetProperty("status").GetString());

        // The configured directory really exists (relative to the data directory).
        var expected = Path.GetFullPath(Path.Combine(_dir, "custom-ws"));
        Assert.True(Directory.Exists(expected), $"missing workspace dir {expected}");

        // The job log announces the override.
        var logs = await (await admin.GetAsync($"/api/runs/{runId}/logs/build?afterLine=0")).Content.ReadFromJsonAsync<JsonElement>();
        var texts = logs!.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!);
        Assert.Contains(texts, t => t.Contains("working directory override") && t.Contains(expected));
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
