using System.Text;
using LibGit2Sharp;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Jobs;

public sealed record WorkflowCommit(string Sha, string Message, string Author, DateTimeOffset When);

/// <summary>
/// Each workflow owns an independent Git repository at {DataDir}/{workflow}/.git
/// tracking its workflow.yml only — a task's config history is fully isolated
/// from every other task. Logs and workspaces are ignored. .git/info/refs is
/// refreshed after each commit so dumb-HTTP clones keep working.
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
        if (!Repository.IsValid(dir))
        {
            Repository.Init(dir);
            // Keep runtime directories out of the config repo's history.
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "logs/\nworkspaces/\n");
            logger.LogInformation("Initialized config repository for workflow {Workflow} at {Path}", workflowName, dir);
        }
        CommitAll(workflowName, "initial import", "system");
    }

    /// <summary>Commits pending config changes for one workflow. No-op when clean.</summary>
    public void CommitAll(string workflowName, string message, string author)
    {
        var path = RepoPath(workflowName);
        if (path is null)
            return;
        using var repo = new Repository(path);
        Commands.Stage(repo, "*");
        var isDirty = repo.RetrieveStatus(new StatusOptions()).Any(entry => entry.State != FileStatus.Unaltered && entry.State != FileStatus.Ignored);
        if (isDirty)
        {
            var signature = new Signature(author, IdentityEmail, DateTimeOffset.Now);
            repo.Commit(message, signature, signature);
            UpdateInfoRefs(workflowName);
        }
    }

    /// <summary>History of the task's config commits, newest first.</summary>
    public IReadOnlyList<WorkflowCommit> History(string workflowName)
    {
        var path = RepoPath(workflowName);
        if (path is null)
            return [];
        using var repo = new Repository(path);
        var commits = new List<WorkflowCommit>();
        foreach (var commit in repo.Commits)
        {
            var entry = commit.Tree[ConfigFileName];
            if (entry is null)
                continue;
            var parent = commit.Parents.FirstOrDefault();
            var changed = parent is null || parent.Tree[ConfigFileName] is null || parent.Tree[ConfigFileName]!.Target.Sha != entry.Target.Sha;
            if (changed)
                commits.Add(new WorkflowCommit(commit.Sha, commit.MessageShort, commit.Author.Name, commit.Author.When));
        }
        return commits;
    }

    /// <summary>The config content at a given commit; null when absent there.</summary>
    public string? ReadAt(string workflowName, string sha)
    {
        var path = RepoPath(workflowName);
        if (path is null)
            return null;
        using var repo = new Repository(path);
        var commit = repo.Lookup<Commit>(sha);
        if (commit is null)
            return null;
        var entry = commit.Tree[ConfigFileName];
        if (entry?.Target.Id is not { } blobId)
            return null;
        return repo.Lookup<Blob>(blobId)?.GetContentText();
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
        try
        {
            using var repo = new Repository(RepoPath(workflowName)!);
            return repo.Head?.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Rewrites .git/info/refs (dumb-HTTP protocol index) for one task repo.</summary>
    private void UpdateInfoRefs(string workflowName)
    {
        try
        {
            var path = RepoPath(workflowName);
            if (path is null)
                return;
            using var repo = new Repository(path);
            var refsDir = Path.Combine(path, ".git", "info");
            Directory.CreateDirectory(refsDir);
            var sb = new StringBuilder();
            foreach (var reference in repo.Refs)
            {
                var obj = repo.Lookup(reference.TargetIdentifier);
                var type = obj is Commit ? "commit" : obj is TagAnnotation ? "tag" : null;
                if (type is null)
                    continue;
                sb.Append(reference.TargetIdentifier).Append('\t').Append(type).Append('\t')
                    .Append(reference.CanonicalName).Append('\n');
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
        return Repository.IsValid(dir) ? dir : null;
    }
}
