using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfinityCI.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>Parameterized builds, log downloads, and display-name attribution.</summary>
public class ParamsAndDownloadsTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public ParamsAndDownloadsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "jobs"));
        Directory.CreateDirectory(Path.Combine(_dir, "param-job"));
        File.WriteAllText(Path.Combine(_dir, "param-job", "workflow.yml"), """
            name: param-job
            params:
              GREETING: hello-default
              TARGET:
                default: ""
                required: true
                description: who to greet
            jobs:
              a:
                steps:
                  - name: Show params
                    command: echo value=%GREETING%-%TARGET% || echo value=$GREETING-$TARGET
            """);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
        _admin = _factory.CreateClient();
        _admin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" }).Wait();
    }

    private async Task<JsonElement> TriggerAsync(object body)
    {
        var response = await _admin.PostAsJsonAsync("/api/jobs/param-job/trigger", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetRunAsync(long id) =>
        await (await _admin.GetAsync($"/api/runs/{id}")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task TriggerWithParams_AreExportedAsEnvVars_AndRecorded()
    {
        var run = await TriggerAsync(new { @params = new { GREETING = "hi-from-test", TARGET = "world" } });
        var runId = run.GetProperty("id").GetInt64();

        await TestEnv.WaitUntilAsync(async () =>
        {
            var fresh = await GetRunAsync(runId);
            return fresh.GetProperty("run").GetProperty("status").GetString() is "Success" or "Failed";
        }, TimeSpan.FromSeconds(30));

        var fresh = await GetRunAsync(runId);
        Assert.Equal("Success", fresh.GetProperty("run").GetProperty("status").GetString());

        // Params recorded on the run.
        var recorded = fresh.GetProperty("run").GetProperty("params");
        Assert.Equal("hi-from-test", recorded.GetProperty("GREETING").GetString());
        Assert.Equal("world", recorded.GetProperty("TARGET").GetString());

        // The step saw them as environment variables.
        var logs = await (await _admin.GetAsync($"/api/runs/{runId}/logs/a?afterLine=0"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var texts = logs.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString());
        Assert.Contains(texts, t => t!.Contains("value=hi-from-test-world"));
    }

    [Fact]
    public async Task RequiredParamMissing_Returns400()
    {
        var response = await _admin.PostAsJsonAsync("/api/jobs/param-job/trigger", new { @params = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("TARGET", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Download_RawAndTimestamped_Formats()
    {
        var run = await TriggerAsync(new { @params = new { GREETING = "g", TARGET = "t" } });
        var runId = run.GetProperty("id").GetInt64();
        await TestEnv.WaitUntilAsync(async () =>
        {
            var fresh = await GetRunAsync(runId);
            return fresh.GetProperty("run").GetProperty("status").GetString() is "Success" or "Failed";
        }, TimeSpan.FromSeconds(30));

        var raw = await _admin.GetAsync($"/api/runs/{runId}/logs/a/download?format=raw");
        Assert.Equal("text/plain", raw.Content.Headers.ContentType!.MediaType);
        Assert.True(raw.Content.Headers.ContentDisposition is not null,
            "download must carry Content-Disposition");
        var rawText = await raw.Content.ReadAsStringAsync();
        Assert.DoesNotContain("T00:", rawText.Split('\n').First()); // raw first line has no timestamp column
        Assert.Contains("value=g-t", rawText);

        var stamped = await _admin.GetAsync($"/api/runs/{runId}/logs/a/download?format=timestamped");
        var stampedText = await stamped.Content.ReadAsStringAsync();
        var firstLine = stampedText.ReplaceLineEndings().Split('\n').First();
        // yyyy-MM-dd HH:mm:ss.fff prefix
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  ", firstLine);
    }

    [Fact]
    public async Task UserNames_Endpoint_ReturnsMap()
    {
        var names = await (await _admin.GetAsync("/api/users/names")).Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("管理员", names!["admin"]);
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
