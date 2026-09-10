using System.Text;
using LibGit2Sharp;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Jobs;

public sealed record WorkflowCommit(string Sha, string Message, string Author, DateTimeOffset When);

/// <summary>
/// Keeps {DataDir}/jobs under its own Git repository: workflow saves and
/// deletes become commits (author = the acting user), history is browsable and
/// restorable, and the repo can be cloned read-only over the /git/jobs
/// endpoints. Updates .git/info/refs after each commit so dumb-HTTP clones work.
/// </summary>
public sealed class WorkflowGitStore(IOptions<CiServerOptions> optionsAccessor, ILogger<WorkflowGitStore> logger)
{
    private readonly CiServerOptions _options = optionsAccessor.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const string IdentityName = "Infinity CI";
    private const string IdentityEmail = "ci@localhost";

    public string RepositoryPath => _options.JobsDir;

    /// <summary>Inits the repo on first run; commits any existing files as the initial commit.</summary>
    public void EnsureRepository()
    {
        Directory.CreateDirectory(_options.JobsDir);
        if (!Repository.IsValid(_options.JobsDir))
        {
            Repository.Init(_options.JobsDir);
            logger.LogInformation("Initialized workflow config repository at {Path}", _options.JobsDir);
        }
        CommitAll("initial import", "system");
    }

    /// <summary>Commits pending changes to the tracked YAML files. No-op when the tree is clean.</summary>
    public void CommitAll(string message, string author)
    {
        var path = CommitPath();
        if (path is null)
            return;
        using var repo = new Repository(path);
        Commands.Stage(repo, "*");
        // After staging, any non-clean entry means there is something to commit.
        var isDirty = repo.RetrieveStatus(new StatusOptions()).Any(entry => entry.State != FileStatus.Unaltered && entry.State != FileStatus.Ignored);
        if (isDirty)
        {
            var signature = new Signature(author, IdentityEmail, DateTimeOffset.Now);
            repo.Commit(message, signature, signature);
            UpdateInfoRefs();
        }
    }

    /// <summary>History of commits touching {name}.yml, newest first.</summary>
    public IReadOnlyList<WorkflowCommit> History(string name)
    {
        var path = CommitPath();
        if (path is null)
            return [];
        using var repo = new Repository(path);
        var relative = WorkflowFileName(name);
        var commits = new List<WorkflowCommit>();
        foreach (var commit in repo.Commits)
        {
            var entry = commit.Tree[relative];
            if (entry is null)
                continue;
            var parent = commit.Parents.FirstOrDefault();
            var changed = parent is null || parent.Tree[relative] is null || parent.Tree[relative]!.Target.Sha != entry.Target.Sha;
            if (changed)
                commits.Add(new WorkflowCommit(commit.Sha, commit.MessageShort, commit.Author.Name, commit.Author.When));
        }
        return commits;
    }

    /// <summary>The workflow file's content at a given commit; null when absent there.</summary>
    public string? ReadAt(string name, string sha)
    {
        var path = CommitPath();
        if (path is null)
            return null;
        using var repo = new Repository(path);
        var commit = repo.Lookup<Commit>(sha);
        if (commit is null)
            return null;
        var entry = commit.Tree[WorkflowFileName(name)];
        if (entry?.Target.Id is not { } blobId)
            return null;
        return repo.Lookup<Blob>(blobId)?.GetContentText();
    }

    /// <summary>Current HEAD sha of the workflow file's last touching commit; null when untracked.</summary>
    public string? HeadShaFor(string name) => History(name).FirstOrDefault()?.Sha;

    /// <summary>Packfile names (without directory) for the dumb-HTTP packs index.</summary>
    public IReadOnlyList<string> ReadPackNames()
    {
        var packDir = Path.Combine(CommitPath() ?? "", ".git", "objects", "pack");
        if (!Directory.Exists(packDir))
            return [];
        return Directory.EnumerateFiles(packDir, "*.pack")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .ToList();
    }

    /// <summary>Reads a raw object from .git/objects (loose or packed) for dumb-HTTP clone.</summary>
    public byte[]? ReadObjectFile(string relativePath)
    {
        var basePath = CommitPath();
        if (basePath is null)
        {
            Console.WriteLine($"DEBUG ReadObjectFile: CommitPath null (IsValid=false) for '{_options.JobsDir}'");
            return null;
        }
        var fullPath = Path.GetFullPath(Path.Combine(basePath, ".git", "objects", relativePath));
        var root = Path.GetFullPath(Path.Combine(basePath, ".git", "objects"));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            Console.WriteLine($"DEBUG ReadObjectFile: fullPath={fullPath} exists={File.Exists(fullPath)} root={root} cwd={Environment.CurrentDirectory}");
            return null;
        }
        return File.ReadAllBytes(fullPath);
    }

    public string? ReadHeadRef()
    {
        var path = CommitPath();
        if (path is null)
            return null;
        var head = Path.Combine(path, ".git", "HEAD");
        if (!File.Exists(head))
            return null;
        // "ref: refs/heads/master\n"
        var content = File.ReadAllText(head).Trim();
        const string prefix = "ref: ";
        if (!content.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var refPath = Path.Combine(path, ".git", content[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : null;
    }

    /// <summary>Rewrites .git/info/refs (dumb-HTTP protocol index).</summary>
    private void UpdateInfoRefs()
    {
        try
        {
            var path = CommitPath();
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
                if (obj is Commit commit)
                {
                    // Peel annotated tags to commits for the ^{} entry.
                    var peeled = $"{commit.Sha}\tcommit\t{reference.CanonicalName}^{{}}\n";
                    if (reference.IsTag || reference.CanonicalName.EndsWith("^{}", StringComparison.Ordinal))
                        sb.Append(peeled);
                }
            }
            File.WriteAllText(Path.Combine(refsDir, "refs"), sb.ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update .git/info/refs for dumb HTTP clone");
        }
    }

    private string? CommitPath() => Repository.IsValid(_options.JobsDir) ? _options.JobsDir : null;

    private static string WorkflowFileName(string name) => WorkflowStore.Sanitize(name) + ".yml";
}
