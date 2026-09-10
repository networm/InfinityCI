using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Runs;
using Xunit;

namespace InfinityCI.Server.Tests;

public class JobLogStoreTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly JobLogStore _store;

    public JobLogStoreTests()
    {
        _store = new JobLogStore(new CiServerOptions { DataDir = _dir });
    }

    [Fact]
    public async Task Append_AssignsSequentialLineIndexes_AndTimestamps()
    {
        var line0 = await _store.AppendAsync(1, "build", 0, "first");
        var line1 = await _store.AppendAsync(1, "build", 1, "second");

        Assert.Equal(0, line0.Line);
        Assert.Equal(1, line1.Line);
        Assert.Equal(0, line0.StepIndex);
        Assert.Equal(1, line1.StepIndex);
        Assert.True(DateTimeOffset.TryParse(line1.TimestampUtc, out _), "timestamp must be ISO-8601");

        var raw = await File.ReadAllLinesAsync(_store.LogPath(1, "build"));
        var parsed = JsonSerializer.Deserialize<JsonElement>(raw[0]);
        Assert.Equal("first", parsed.GetProperty("text").GetString());
        Assert.Equal(0, parsed.GetProperty("stepIndex").GetInt32());
        Assert.NotEmpty(parsed.GetProperty("timestampUtc").GetString());
    }

    [Fact]
    public async Task Append_PerJobFilesAreIndependent()
    {
        await _store.AppendAsync(1, "build", 0, "from-build");
        await _store.AppendAsync(1, "test", 0, "from-test");

        var buildLines = await _store.ReadAfterAsync(1, "build", 0);
        var testLines = await _store.ReadAfterAsync(1, "test", 0);

        Assert.Equal(["from-build"], buildLines.Select(l => l.Text));
        Assert.Equal(["from-test"], testLines.Select(l => l.Text));
    }

    [Fact]
    public async Task ReadAfter_ResumesFromLineCursor()
    {
        for (var i = 0; i < 5; i++)
            await _store.AppendAsync(1, "build", 0, $"line-{i}");

        var all = await _store.ReadAfterAsync(1, "build", 0);
        var rest = await _store.ReadAfterAsync(1, "build", 3);
        var none = await _store.ReadAfterAsync(1, "build", 5);

        Assert.Equal(5, all.Count);
        Assert.Equal(["line-3", "line-4"], rest.Select(l => l.Text));
        Assert.Empty(none);
    }

    [Fact]
    public async Task ReadAfter_SkipsTornLines()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "logs", "1"));
        await File.WriteAllTextAsync(_store.LogPath(1, "build"),
            """{"line":0,"timestampUtc":"2026-01-01T00:00:00.000Z","stepIndex":0,"text":"ok"}""" + "\n" + """{"line":1,"timestampUtc":"torn""");

        var lines = await _store.ReadAfterAsync(1, "build", 0);
        var line = Assert.Single(lines);
        Assert.Equal("ok", line.Text);
    }

    [Fact]
    public void SanitizeJobKey_ReplacesUnsafeCharacters()
    {
        Assert.Equal("build_x64", JobLogStore.SanitizeJobKey("build x64"));
        Assert.Equal("a-b_c", JobLogStore.SanitizeJobKey("a-b_c"));
    }

    public void Dispose()
    {
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
