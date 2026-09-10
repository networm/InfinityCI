using System.Text;
using System.Text.Json;
using InfinityCI.Core;

namespace InfinityCI.Server.Runs;

/// <summary>
/// Per-job JSONL logs under {DataDir}/logs/{runId}/{jobKey}.jsonl. Every line is
/// stamped by the master at append time and carries its line index, so clients
/// resume after reconnects without losing or duplicating lines, and per-step
/// consoles anchor by line ranges.
/// </summary>
public sealed class JobLogStore(CiServerOptions options)
{
    private static readonly JsonSerializerOptions WriteOptions = new();

    // Serializes appends across all jobs so line indexes are consistent with files.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(long RunId, string JobKey), long> _lineCounts = new();

    /// <summary>Appends one line, stamps the timestamp, publishes nothing (caller fans out). Returns the stored line.</summary>
    public async Task<LogLine> AppendAsync(long runId, string jobKey, int stepIndex, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\n'))
            throw new ArgumentException("Append one line at a time; newlines are added by the store.", nameof(text));

        await _gate.WaitAsync(ct);
        try
        {
            var path = LogPath(runId, jobKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            long index;
            await using (var readFs = File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                : null)
            {
                index = CountLines(runId, jobKey, readFs);
            }
            await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var line = new LogLine(index, DateTimeOffset.UtcNow.ToString("o"), stepIndex, text);
            var json = JsonSerializer.Serialize(new
            {
                line = line.Line,
                timestampUtc = line.TimestampUtc,
                stepIndex = line.StepIndex,
                text = line.Text,
            }, WriteOptions);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
            _lineCounts[(runId, jobKey)] = index + 1;
            return line;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetEndLineAsync(long runId, string jobKey)
    {
        await _gate.WaitAsync();
        try
        {
            if (_lineCounts.TryGetValue((runId, jobKey), out var known))
                return known;
            var path = LogPath(runId, jobKey);
            if (!File.Exists(path))
                return 0;
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return CountLines(runId, jobKey, fs);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns lines with index at/after <paramref name="afterLine"/> (0 = from the beginning), oldest first.</summary>
    public async Task<IReadOnlyList<LogLine>> ReadAfterAsync(long runId, string jobKey, long afterLine, int maxLines = 20_000, CancellationToken ct = default)
    {
        var path = LogPath(runId, jobKey);
        if (!File.Exists(path))
            return [];

        await _gate.WaitAsync(ct);
        List<string> raw;
        try
        {
            raw = (await File.ReadAllLinesAsync(path, ct)).ToList();
        }
        finally
        {
            _gate.Release();
        }

        var lines = new List<LogLine>();
        foreach (var json in raw)
        {
            if (json.Length == 0)
                continue;
            LogLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<LogLine?>(json, ReadOptions());
            }
            catch (JsonException)
            {
                continue; // torn final write from a crash
            }
            if (parsed is null)
                continue;
            _lineCounts[(runId, jobKey)] = parsed.Value.Line + 1;
            if (parsed.Value.Line >= afterLine)
                lines.Add(parsed.Value);
            if (lines.Count >= maxLines)
                break;
        }
        return lines;
    }

    public string LogPath(long runId, string jobKey) =>
        Path.Combine(options.LogsDir, runId.ToString(), SanitizeJobKey(jobKey) + ".jsonl");

    public static string SanitizeJobKey(string jobKey)
    {
        var chars = jobKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>Callers hold the gate. Scans the file once per process, then serves from memory.</summary>
    private long CountLines(long runId, string jobKey, FileStream? readStream)
    {
        if (_lineCounts.TryGetValue((runId, jobKey), out var known))
            return known;
        if (readStream is null)
            return 0;
        var count = 0L;
        var buffer = new byte[1024 * 64];
        int read;
        readStream.Seek(0, SeekOrigin.Begin);
        while ((read = readStream.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++)
                if (buffer[i] == (byte)'\n')
                    count++;
        _lineCounts[(runId, jobKey)] = count;
        return count;
    }

    private static JsonSerializerOptions ReadOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
