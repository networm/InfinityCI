using InfinityCI.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Jobs;

/// <summary>
/// Task-first layout: every workflow owns a directory {DataDir}/{workflow}
/// containing its config (workflow.yml, tracked by the task's own Git repo),
/// its logs and its workspaces. Changes hot-reload on file changes.
/// </summary>
public sealed class WorkflowStore(IOptions<CiServerOptions> optionsAccessor, WorkflowGitStore gitStore, ILogger<WorkflowStore> logger) : IHostedService, IDisposable
{
    public const string ConfigFileName = "workflow.yml";

    public sealed record WorkflowEntry(Workflow Definition, string RawYaml);

    private readonly CiServerOptions _options = optionsAccessor.Value;
    private Dictionary<string, WorkflowEntry> _workflows = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;

    public IReadOnlyList<Workflow> Workflows => _workflows.Values.Select(w => w.Definition).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Workflow? TryGet(string name) => _workflows.TryGetValue(name, out var entry) ? entry.Definition : null;

    /// <summary>Original YAML text, sent verbatim to agents in job assignments.</summary>
    public string? TryGetRawYaml(string name) => _workflows.TryGetValue(name, out var entry) ? entry.RawYaml : null;

    /// <summary>Task directory: {DataDir}/{sanitized workflow name}.</summary>
    public string WorkflowDir(string name) => Path.Combine(_options.DataDir, Sanitize(name));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataDir);
        EnsureSampleWorkflow();
        Reload();
        // Every task directory gets its own config repository.
        foreach (var name in _workflows.Keys)
            gitStore.EnsureRepository(name);
        StartWatcher();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeWatcher();
        return Task.CompletedTask;
    }

    /// <summary>Writes/updates a workflow config and its per-task Git repo. Returns the parsed definition.</summary>
    public Workflow Save(string name, string yaml, string author = "system")
    {
        var parsed = WorkflowYaml.Parse(yaml);
        if (!parsed.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new WorkflowYamlException($"Workflow 'name' ({parsed.Name}) does not match the target file name '{name}'.");

        var dir = WorkflowDir(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ConfigFileName), yaml);
        gitStore.EnsureRepository(name);
        gitStore.CommitAll(name, $"update workflow {parsed.Name}", author);
        Reload();
        return parsed;
    }

    public bool Delete(string name, string author = "system")
    {
        var dir = WorkflowDir(name);
        if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, ConfigFileName)))
            return false;
        File.Delete(Path.Combine(dir, ConfigFileName));
        Reload();
        return true;
    }

    public static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    private void Reload()
    {
        var workflows = new Dictionary<string, WorkflowEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(_options.DataDir))
        {
            var configPath = Path.Combine(dir, ConfigFileName);
            if (!File.Exists(configPath))
                continue;
            try
            {
                var raw = File.ReadAllText(configPath);
                var workflow = WorkflowYaml.Parse(raw);
                workflows[workflow.Name] = new WorkflowEntry(workflow, raw);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping invalid workflow config {File}", configPath);
            }
        }

        _workflows = workflows;
        logger.LogInformation("Loaded {Count} workflow(s) from {DataDir}", workflows.Count, _options.DataDir);
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_options.DataDir, ConfigFileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (!Path.GetFileName(e.FullPath).Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase))
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
        if (!_options.CreateSampleWorkflow)
            return;
        var sampleDir = Path.Combine(_options.DataDir, "hello-workflow");
        if (File.Exists(Path.Combine(sampleDir, ConfigFileName)))
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
        Directory.CreateDirectory(sampleDir);
        File.WriteAllText(Path.Combine(sampleDir, ConfigFileName), sample);
        gitStore.EnsureRepository("hello-workflow");
        gitStore.CommitAll("hello-workflow", "initial import", "system");
        logger.LogInformation("Wrote sample workflow to {Path}", sampleDir);
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
