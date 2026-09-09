using InfinityCI.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Jobs;

/// <summary>
/// Loads job definitions from {DataDir}/jobs/*.yml and hot-reloads on file changes.
/// Writes a sample job on first run when the directory is empty.
/// </summary>
public sealed class JobStore(IOptions<CiServerOptions> optionsAccessor, ILogger<JobStore> logger) : IHostedService, IDisposable
{
    private static readonly string[] Extensions = [".yml", ".yaml"];

    public sealed record JobEntry(JobDefinition Definition, string RawYaml);

    private readonly CiServerOptions _options = optionsAccessor.Value;
    private Dictionary<string, JobEntry> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;

    public IReadOnlyList<JobDefinition> Jobs => _jobs.Values.Select(j => j.Definition).OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public JobDefinition? TryGet(string name) => _jobs.TryGetValue(name, out var entry) ? entry.Definition : null;

    /// <summary>Original YAML text, sent verbatim to agents in job assignments.</summary>
    public string? TryGetRawYaml(string name) => _jobs.TryGetValue(name, out var entry) ? entry.RawYaml : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.JobsDir);
        EnsureSampleJob();
        Reload();
        StartWatcher();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeWatcher();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var jobs = new Dictionary<string, JobEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_options.JobsDir))
        {
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            try
            {
                var raw = File.ReadAllText(file);
                var job = JobYaml.Parse(raw);
                jobs[job.Name] = new JobEntry(job, raw);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping invalid job file {File}", file);
            }
        }

        _jobs = jobs;
        logger.LogInformation("Loaded {Count} job(s) from {JobsDir}", jobs.Count, _options.JobsDir);
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_options.JobsDir, "*.*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (!Extensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase))
            return;

        // Debounce bursts of events (editors often write several) into one reload.
        if (_reloadTimer is null)
        {
            _reloadTimer = new Timer(_ =>
            {
                try
                {
                    Reload();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Job reload failed");
                }
            });
        }
        _reloadTimer.Change(300, Timeout.Infinite);
    }

    private void EnsureSampleJob()
    {
        var hasJobs = Directory.EnumerateFiles(_options.JobsDir)
            .Any(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        if (hasJobs)
            return;

        const string sample = """
            # Sample job written on first run. Edit freely — changes hot-reload.
            # Keep commands portable (both cmd and sh support plain `echo`).
            name: hello-build
            description: Sample job created on first run
            steps:
              - name: Greet
                command: echo hello from Infinity CI
              - name: Second step
                command: echo the second step ran
            """;
        File.WriteAllText(Path.Combine(_options.JobsDir, "hello-build.yml"), sample);
        logger.LogInformation("Wrote sample job to {Path}", _options.JobsDir);
    }

    public void Dispose() => DisposeWatcher();

    private void DisposeWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _reloadTimer?.Dispose();
        _reloadTimer = null;
    }
}
