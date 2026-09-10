using InfinityCI.Core;
using LibGit2Sharp;
using Xunit;

namespace InfinityCI.Core.Tests;

/// <summary>End-to-end checkout tests against a real local Git repository.</summary>
public class GitSourceFetcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "infinityci-tests", Guid.NewGuid().ToString("N"));
    private readonly string _sourceRepo;
    private string _lastCommitSha;

    public GitSourceFetcherTests()
    {
        _sourceRepo = Path.Combine(_root, "source");
        Directory.CreateDirectory(_sourceRepo);
        using var repo = new Repository(Repository.Init(_sourceRepo));
        CommitFile(repo, "README.md", "hello scm");
        _lastCommitSha = repo.Head.Tip.Sha;
    }

    private void CommitFile(Repository repo, string path, string content)
    {
        var fullPath = Path.Combine(_sourceRepo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        Commands.Stage(repo, "*");
        var signature = new Signature("tester", "tester@test", DateTimeOffset.Now);
        repo.Commit($"add {path}", signature, signature);
    }

    [Fact]
    public void Fetch_FreshClone_ChecksOutDefaultBranch()
    {
        var workspace = Path.Combine(_root, "ws1");
        var result = GitSourceFetcher.Fetch(new ScmConfig { Url = _sourceRepo }, workspace, null);

        Assert.Equal(40, result.CommitSha.Length);
        Assert.Equal(File.ReadAllText(Path.Combine(workspace, "README.md")), "hello scm");
        Assert.True(LibGit2Sharp.Repository.IsValid(workspace), "workspace should be a git repo");
    }

    [Fact]
    public void Fetch_ExistingWorkspace_FastForwardsToNewCommit()
    {
        var workspace = Path.Combine(_root, "ws2");
        var first = GitSourceFetcher.Fetch(new ScmConfig { Url = _sourceRepo }, workspace, null);

        // New commit appears upstream after the initial clone.
        using (var repo = new Repository(_sourceRepo))
        {
            CommitFile(repo, "feature.txt", "new content");
            _lastCommitSha = repo.Head.Tip.Sha;
        }

        var second = GitSourceFetcher.Fetch(new ScmConfig { Url = _sourceRepo }, workspace, null);

        Assert.NotEqual(first.CommitSha, second.CommitSha);
        Assert.Equal(_lastCommitSha, second.CommitSha);
        Assert.True(File.Exists(Path.Combine(workspace, "feature.txt")));
    }

    [Fact]
    public void Fetch_BranchOverride_ChecksOutThatBranch()
    {
        // Create a "develop" branch in the source repo with an extra file.
        using (var repo = new Repository(_sourceRepo))
        {
            var signature = new Signature("tester", "tester@test", DateTimeOffset.Now);
            var develop = repo.CreateBranch("develop");
            Commands.Checkout(repo, develop);
            CommitFile(repo, "develop-only.txt", "from develop");
            Commands.Checkout(repo, repo.Branches["master"] ?? repo.Branches["main"]);
        }

        var workspace = Path.Combine(_root, "ws3");
        GitSourceFetcher.Fetch(new ScmConfig { Url = _sourceRepo, Branch = "develop" }, workspace, null);

        Assert.True(File.Exists(Path.Combine(workspace, "develop-only.txt")));
    }

    [Fact]
    public void Fetch_RefOverride_ChecksOutSpecificCommit()
    {
        var workspace = Path.Combine(_root, "ws4");
        // Pin to the FIRST commit: add a second commit first.
        using (var repo = new Repository(_sourceRepo))
        {
            CommitFile(repo, "second.txt", "second");
        }

        GitSourceFetcher.Fetch(new ScmConfig { Url = _sourceRepo, Ref = _lastCommitSha }, workspace, null);

        Assert.True(File.Exists(Path.Combine(workspace, "README.md")));
        Assert.False(File.Exists(Path.Combine(workspace, "second.txt")), "pinned ref must not include later commits");
    }

    [Fact]
    public void Fetch_UnknownBranch_Throws()
    {
        var workspace = Path.Combine(_root, "ws5");
        Assert.Throws<InvalidOperationException>(() =>
            GitSourceFetcher.Fetch(new ScmConfig { Url = _sourceRepo, Branch = "nope" }, workspace, null));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort cleanup (packed files can linger briefly on Windows)
        }
    }
}
