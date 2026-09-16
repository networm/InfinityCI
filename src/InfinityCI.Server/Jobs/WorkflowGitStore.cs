using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Jobs;

public sealed record WorkflowCommit(string Sha, string Message, string Author, DateTimeOffset When);

/// <summary>
/// Each workflow owns an independent Git repository at {DataDir}/{workflow}/.git
/// tracking its workflow.yml only — a task's config history is fully isolated
/// from every other task. Logs and workspaces are ignored. .git/info/refs is
/// refreshed after each commit so dumb-HTTP clones keep working.
///
/// All repository operations shell out to the git CLI (LibGit2Sharp stays
/// reserved for source checkouts): the CLI handles huge repositories, exotic
/// pack layouts and future format changes without a managed-object-model
/// rewrite, and it keeps one battle-tested code path on every platform.
/// </summary>
public sealed class WorkflowGitStore(IOptions<CiServerOptions> optionsAccessor, ILogger<WorkflowGitStore> logger)
{
    private readonly CiServerOptions _options = optionsAccessor.Value;
    private const string IdentityName = "Infinity CI";
    private const string IdentityEmail = "ci@localhost";
    private const string ConfigFileName = WorkflowStore.ConfigFileName;

    /// <summary>Task directory: {DataDir}/{sanitized workflow name}.</summary>
    public string WorkflowDir(string workflowName) => Path.Combine(_options.DataDir, WorkflowStore.Sanitize(workflowName));

    /// <summary>Inits (or validates) the repo for one workflow and commits existing config.</summary>
    public void EnsureRepository(string workflowName)
    {
        var dir = WorkflowDir(workflowName);
        Directory.CreateDirectory(dir);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            // Dumb-HTTP clone expects refs/heads/master (the endpoint is explicit).
            if (RunGit(dir, false, false, "init", "-b", "master") is null)
                RunGit(dir, false, false, "init");
            RunGit(dir, false, false, "symbolic-ref", "HEAD", "refs/heads/master");
            RunGit(dir, false, false, "config", "user.name", IdentityName);
            RunGit(dir, false, false, "config", "user.email", IdentityEmail);
            // Keep runtime directories out of the config repo's history.
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "logs/\nworkspaces/\n");
            logger.LogInformation("Initialized config repository for workflow {Workflow} at {Path}", workflowName, dir);
        }
        CommitAll(workflowName, "initial import", "system");
    }

    /// <summary>Commits pending config changes for one workflow. No-op when clean.</summary>
    public void CommitAll(string workflowName, string message, string author)
    {
        var dir = RepoPath(workflowName);
        if (dir is null)
            return;
        RunGit(dir, false, false, "add", "-A", "--", ".");
        var status = RunGit(dir, false, false, "status", "--porcelain");
        if (string.IsNullOrEmpty(status))
            return; // nothing staged — clean
        var safeAuthor = author.Replace("\"", "'");
        RunGit(dir, false, false, "commit", "--allow-empty-message", "-m", message, $"--author={safeAuthor} <{IdentityEmail}>");
        UpdateInfoRefs(workflowName);
    }

    /// <summary>History of the task's config commits (commits that touched the
    /// config file), newest first.</summary>
    public IReadOnlyList<WorkflowCommit> History(string workflowName)
    {
        var dir = RepoPath(workflowName);
        if (dir is null)
            return [];
        var output = RunGit(dir, false, true, "log", "--pretty=format:%H%x1f%an%x1f%aI%x1f%s", "--", ConfigFileName);
        if (string.IsNullOrEmpty(output))
            return [];
        var commits = new List<WorkflowCommit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\x1f');
            if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[2], out var when))
                continue;
            commits.Add(new WorkflowCommit(parts[0], parts[3], parts[1], when));
        }
        return commits;
    }

    /// <summary>The config content at a given commit; null when absent there.</summary>
    public string? ReadAt(string workflowName, string sha)
    {
        var dir = RepoPath(workflowName);
        if (dir is null || !IsSafeRevision(sha))
            return null;
        return RunGit(dir, false, true, "show", $"{sha}:{ConfigFileName}");
    }

    /// <summary>Packfile names for the task's repo (dumb-HTTP packs index).</summary>
    public IReadOnlyList<string> ReadPackNames(string workflowName)
    {
        var packDir = Path.Combine(RepoPath(workflowName) ?? "", ".git", "objects", "pack");
        if (!Directory.Exists(packDir))
            return [];
        return Directory.EnumerateFiles(packDir, "*.pack")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .ToList();
    }

    /// <summary>Reads a raw object from the task repo (loose or packed) for dumb-HTTP clone.</summary>
    public byte[]? ReadObjectFile(string workflowName, string relativePath)
    {
        var basePath = RepoPath(workflowName);
        if (basePath is null)
            return null;
        var fullPath = Path.GetFullPath(Path.Combine(basePath, ".git", "objects", relativePath));
        var root = Path.GetFullPath(Path.Combine(basePath, ".git", "objects"));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return null;
        return File.ReadAllBytes(fullPath);
    }

    /// <summary>Current HEAD sha of the task repo; null when unavailable.</summary>
    public string? ReadHeadRef(string workflowName)
    {
        var path = RepoPath(workflowName);
        if (path is null)
            return null;
        var head = Path.Combine(path, ".git", "HEAD");
        if (!File.Exists(head))
            return null;
        var content = File.ReadAllText(head).Trim();
        const string prefix = "ref: ";
        if (!content.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var refPath = Path.Combine(path, ".git", content[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : null;
    }

    /// <summary>Friendly branch name HEAD points at (e.g. "master").</summary>
    public string? BranchName(string workflowName)
    {
        var dir = RepoPath(workflowName);
        if (dir is null)
            return null;
        return RunGit(dir, false, true, "rev-parse", "--abbrev-ref", "HEAD");
    }

    /// <summary>Rewrites .git/info/refs (dumb-HTTP protocol index) for one task repo.</summary>
    private void UpdateInfoRefs(string workflowName)
    {
        try
        {
            var path = RepoPath(workflowName);
            if (path is null)
                return;
            var refsDir = Path.Combine(path, ".git", "info");
            Directory.CreateDirectory(refsDir);
            var output = RunGit(path, false, true, "show-ref") ?? "";
            var sb = new StringBuilder();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('\t');
                if (separator <= 0)
                    continue;
                var sha = line[..separator];
                var refName = line[(separator + 1)..].Trim();
                var type = RunGit(path, false, true, "cat-file", "-t", sha)?.Trim();
                if (type is not ("commit" or "tag"))
                    continue;
                sb.Append(sha).Append('\t').Append(type).Append('\t')
                    .Append(refName).Append('\n');
            }
            File.WriteAllText(Path.Combine(refsDir, "refs"), sb.ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update .git/info/refs for workflow {Workflow}", workflowName);
        }
    }

    private string? RepoPath(string workflowName)
    {
        var dir = Path.Combine(_options.DataDir, WorkflowStore.Sanitize(workflowName));
        return Directory.Exists(Path.Combine(dir, ".git")) ? dir : null;
    }

    /// <summary>Runs `git <args>` in the repo dir and returns trimmed stdout;
    /// null on any failure.</summary>
    private string? RunGit(string workingDir, params object[] args) =>
        RunGit(workingDir, false, false, args);

    /// <summary>`allowMissing` swallows expected non-zero exits (missing blob,
    /// empty repo) instead of warning; `allowExitCodeOne` treats exit 1 as success.</summary>
    private string? RunGit(string workingDir, bool allowExitCodeOne, bool allowMissing, params object[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg.ToString() ?? "");

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
            logger.LogWarning("git {Args} timed out in {Dir}", string.Join(' ', args), workingDir);
            return null;
        }

        var exit = process.ExitCode;
        if (exit == 0)
            return stdout.Result.TrimEnd('\n');
        if (exit == 1 && allowExitCodeOne)
            return stdout.Result.TrimEnd('\n');
        if (allowMissing)
            return null;
        logger.LogWarning("git {Args} failed in {Dir} with exit {Exit}: {Error}",
            string.Join(' ', args), workingDir, exit, stderr.Result.Trim());
        return null;
    }

    /// <summary>Revisions reach the CLI only after this check — never raw user input.</summary>
    private static bool IsSafeRevision(string revision) =>
        revision.Length is >= 4 and <= 64 && revision.All(c => char.IsAsciiHexDigit(c) || c == '_');
}
