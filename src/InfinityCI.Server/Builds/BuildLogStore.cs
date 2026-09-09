using System.Text;
using InfinityCI.Core;

namespace InfinityCI.Server.Builds;

public readonly record struct LogLine(long Offset, string Text);

/// <summary>
/// Appends build output to per-build files under {DataDir}/logs/{buildId}.log.
/// Each line carries its byte offset so real-time clients can resume streaming
/// after reconnect without losing or duplicating lines.
/// </summary>
public sealed class BuildLogStore(CiServerOptions options, BuildEvents events)
{
    // Serializes appends across concurrent builds and stdout/stderr pumps so
    // published offsets are consistent with file contents.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Appends one line and publishes a log event. Returns the line's [start, end) byte offsets.</summary>
    public async Task<(long StartOffset, long EndOffset)> AppendAsync(long buildId, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\n'))
            throw new ArgumentException("Append one line at a time; newlines are added by the store.", nameof(text));

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(options.LogsDir);
            await using var fs = new FileStream(LogPath(buildId), FileMode.Append, FileAccess.Write, FileShare.Read);
            var start = fs.Length;
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
            // Publish while holding the lock so live events keep file order.
            await events.PublishLogAppendedAsync(buildId, start, text);
            return (start, start + bytes.Length);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetEndOffsetAsync(long buildId)
    {
        await _gate.WaitAsync();
        try
        {
            var file = new FileInfo(LogPath(buildId));
            return file.Exists ? file.Length : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns lines starting at or after <paramref name="afterOffset"/> (a byte cursor:
    /// the caller holds everything before it, so the first read uses 0), oldest first.
    /// </summary>
    public async Task<IReadOnlyList<LogLine>> ReadAfterAsync(long buildId, long afterOffset, int maxLines = 10_000, CancellationToken ct = default)
    {
        var path = LogPath(buildId);
        if (!File.Exists(path))
            return [];

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var lines = new List<LogLine>();
        var pos = 0L;
        while (pos < bytes.Length && lines.Count < maxLines)
        {
            var nl = Array.IndexOf(bytes, (byte)'\n', (int)pos);
            var end = nl < 0 ? bytes.Length : nl;
            if (pos >= afterOffset)
                lines.Add(new LogLine(pos, Encoding.UTF8.GetString(bytes, (int)pos, (int)(end - pos))));
            pos = end + 1;
        }
        return lines;
    }

    public string LogPath(long buildId) => Path.Combine(options.LogsDir, buildId + ".log");
}
