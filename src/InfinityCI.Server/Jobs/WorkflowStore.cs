using InfinityCI.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Jobs;

/// <summary>
/// Loads workflow definitions from {DataDir}/jobs/*.yml and hot-reloads on file
/// changes. Writes a sample workflow on first run when the directory is empty.
/// </summary>
public sealed class WorkflowStore(IOptions<CiServerOptions> optionsAccessor, ILogger<WorkflowStore> logger) : IHostedService, IDisposable
{
    private static readonly string[] Extensions = [".yml", ".yaml"];

    public sealed record WorkflowEntry(Workflow Definition, string RawYaml);

    private readonly CiServerOptions _options = optionsAccessor.Value;
    private Dictionary<string, WorkflowEntry> _workflows = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;

    public IReadOnlyList<Workflow> Workflows => _workflows.Values.Select(w => w.Definition).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Workflow? TryGet(string name) => _workflows.TryGetValue(name, out var entry) ? entry.Definition : null;

    /// <summary>Original YAML text, sent verbatim to agents in job assignments.</summary>
    public string? TryGetRawYaml(string name) => _workflows.TryGetValue(name, out var entry) ? entry.RawYaml : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.JobsDir);
        EnsureSampleWorkflow();
        Reload();
        StartWatcher();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeWatcher();
        return Task.CompletedTask;
    }

    /// <summary>Writes/updates a workflow file by name (job editor save path). Returns the parsed definition.</summary>
    public Workflow Save(string name, string yaml)
    {
        var parsed = WorkflowYaml.Parse(yaml);
        if (!parsed.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new WorkflowYamlException($"Workflow 'name' ({parsed.Name}) does not match the target file name '{name}'.");
        Directory.CreateDirectory(_options.JobsDir);
        File.WriteAllText(Path.Combine(_options.JobsDir, WorkflowStore.Sanitize(name) + ".yml"), yaml);
        Reload();
        return parsed;
    }

    public bool Delete(string name)
    {
        foreach (var extension in Extensions)
        {
            var path = Path.Combine(_options.JobsDir, Sanitize(name) + extension);
            if (File.Exists(path))
            {
                File.Delete(path);
                Reload();
                return true;
            }
        }
        return false;
    }

    public static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    private void Reload()
    {
        var workflows = new Dictionary<string, WorkflowEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_options.JobsDir))
        {
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            try
            {
                var raw = File.ReadAllText(file);
                var workflow = WorkflowYaml.Parse(raw);
                workflows[workflow.Name] = new WorkflowEntry(workflow, raw);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping invalid workflow file {File}", file);
            }
        }

        _workflows = workflows;
        logger.LogInformation("Loaded {Count} workflow(s) from {JobsDir}", workflows.Count, _options.JobsDir);
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
                    logger.LogError(ex, "Workflow reload failed");
                }
            });
        }
        _reloadTimer.Change(300, Timeout.Infinite);
    }

    private void EnsureSampleWorkflow()
    {
        var hasWorkflows = Directory.EnumerateFiles(_options.JobsDir)
            .Any(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        if (hasWorkflows)
            return;

        const string sample = """
            # Sample workflow written on first run. Edit freely — changes hot-reload.
            # Keep commands portable (both cmd and sh support plain `echo`).
            name: hello-workflow
            description: Sample workflow created on first run
            project: Default
            jobs:
              greet:
                steps:
                  - name: Greet
                    command: echo hello from Infinity CI
              echo2:
                steps:
                  - name: Second job (runs in parallel)
                    command: echo parallel job executed
            """;
        File.WriteAllText(Path.Combine(_options.JobsDir, "hello-workflow.yml"), sample);
        logger.LogInformation("Wrote sample workflow to {Path}", _options.JobsDir);
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
