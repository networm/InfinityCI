using System.Text;
using InfinityCI.Server;
using InfinityCI.Server.Builds;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfinityCI.Server.Tests;

public class BuildLogStoreTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly BuildEvents _events = new(NullLogger<BuildEvents>.Instance);
    private readonly BuildLogStore _store;

    public BuildLogStoreTests()
    {
        _store = new BuildLogStore(new CiServerOptions { DataDir = _dir }, _events);
    }

    [Fact]
    public async Task Append_AssignsMonotonicByteOffsets()
    {
        var (start1, end1) = await _store.AppendAsync(1, "hello");
        var (start2, end2) = await _store.AppendAsync(1, "world");

        Assert.Equal(0, start1);
        Assert.Equal(6, end1); // "hello" is 5 bytes + newline
        Assert.Equal(6, start2);
        Assert.Equal(12, end2);

        Assert.Equal("hello\nworld\n", await File.ReadAllTextAsync(_store.LogPath(1)));
    }

    [Fact]
    public async Task Append_MultiByteCharacters_UseByteOffsets()
    {
        // "中文" is 6 UTF-8 bytes + 1 newline = 7.
        var (start1, end1) = await _store.AppendAsync(1, "中文");
        var (start2, _) = await _store.AppendAsync(1, "abc");

        Assert.Equal((0L, 7L), (start1, end1));
        Assert.Equal(7, start2);
    }

    [Fact]
    public async Task Append_RejectsEmbeddedNewlines()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(1, "two\nlines"));
    }

    [Fact]
    public async Task ReadAfter_ReturnsOnlyLinesAfterOffset()
    {
        await _store.AppendAsync(1, "line-1");
        var (start2, _) = await _store.AppendAsync(1, "line-2");
        await _store.AppendAsync(1, "line-3");

        var all = await _store.ReadAfterAsync(1, 0);
        var rest = await _store.ReadAfterAsync(1, start2);
        var none = await _store.ReadAfterAsync(1, long.MaxValue);

        Assert.Equal(["line-1", "line-2", "line-3"], all.Select(l => l.Text));
        Assert.Equal(["line-2", "line-3"], rest.Select(l => l.Text));
        Assert.Empty(none);
    }

    [Fact]
    public async Task ReadAfter_NonexistentBuild_ReturnsEmpty()
    {
        Assert.Empty(await _store.ReadAfterAsync(999, 0));
    }

    [Fact]
    public async Task Append_PublishesEventWithCorrectOffset()
    {
        LogAppendedEventArgs? received = null;
        _events.LogAppended += args =>
        {
            received = args;
            return Task.CompletedTask;
        };

        await _store.AppendAsync(42, "eventual");

        Assert.NotNull(received);
        Assert.Equal(42, received!.BuildId);
        Assert.Equal(0, received.Offset);
        Assert.Equal("eventual", received.Text);
    }

    [Fact]
    public async Task GetEndOffset_TracksFileLength()
    {
        Assert.Equal(0, await _store.GetEndOffsetAsync(1));
        await _store.AppendAsync(1, "12345");
        Assert.Equal(6, await _store.GetEndOffsetAsync(1));
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
