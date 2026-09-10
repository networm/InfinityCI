using LibGit2Sharp;

namespace InfinityCI.Core;

/// <summary>
/// Ensures a job workspace contains a checkout of the workflow's Git source:
/// clones when the directory is fresh, otherwise fetches and hard-resets to
/// the requested branch/ref. Returns the checked-out commit and branch.
/// </summary>
public static class GitSourceFetcher
{
    public static CheckoutResult Fetch(
        ScmConfig scm,
        string workspace,
        GitCredential? credential,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scm.Url);
        Directory.CreateDirectory(workspace);

        var isRepo = Directory.Exists(Path.Combine(workspace, ".git"));
        if (!isRepo)
        {
            log?.Invoke($"[server] cloning {scm.Url} into the workspace...");
            Clone(scm, workspace, credential);
        }
        else
        {
            using (var repo = new Repository(workspace))
            {
                var remote = repo.Network.Remotes["origin"]
                    ?? throw new InvalidOperationException("Workspace repository has no 'origin' remote.");
                log?.Invoke("[server] fetching origin...");
                var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification).ToArray();
                Commands.Fetch(repo, remote.Name, refSpecs, FetchOptions(credential), "fetch for build");
            }
        }

        using var repository = new Repository(workspace);
        var commitSha = CheckoutTarget(repository, scm, log);
        return new CheckoutResult(commitSha, DescribeBranch(repository));
    }

    private static void Clone(ScmConfig scm, string workspace, GitCredential? credential)
    {
        // Clone the default branch only; the exact branch/ref is checked out
        // afterwards in one code path (so unknown branches get a clean error).
        var options = new CloneOptions
        {
            Checkout = false,
            FetchOptions = { CredentialsProvider = CredentialProvider(credential) },
        };
        Repository.Clone(scm.Url, workspace, options);
    }

    /// <summary>Checks out the configured ref/branch and returns the commit sha.</summary>
    private static string CheckoutTarget(Repository repo, ScmConfig scm, Action<string>? log)
    {
        if (scm.Ref is { } reference)
        {
            var commit = repo.Lookup<Commit>(reference)
                ?? throw new InvalidOperationException($"Ref '{reference}' not found in the source repository.");
            Commands.Checkout(repo, commit, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            log?.Invoke($"[server] checked out {commit.Sha[..10]} (ref {reference}).");
            return commit.Sha;
        }

        if (scm.Branch is { } branchName)
        {
            var remoteBranch = repo.Branches.FirstOrDefault(b => b.FriendlyName == $"origin/{branchName}")
                ?? throw new InvalidOperationException($"Branch '{branchName}' not found on origin.");
            // Checking out a remote branch creates a local tracking branch automatically.
            Commands.Checkout(repo, remoteBranch, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            var tip = remoteBranch.Tip ?? throw new InvalidOperationException($"Branch '{branchName}' has no commits.");
            log?.Invoke($"[server] checked out {tip.Sha[..10]} on branch {branchName}.");
            return tip.Sha;
        }

        // No branch configured: follow origin's HEAD (fetch updates remote-tracking
        // refs but NOT local branches, so the remote tip is authoritative).
        // origin/HEAD may be symbolic (→ default branch ref) or direct (→ commit sha).
        var defaultTip = repo.Refs["refs/remotes/origin/HEAD"] switch
        {
            SymbolicReference sym when repo.Refs[sym.Target.CanonicalName] is DirectReference d => d.Target as Commit,
            DirectReference direct => direct.Target as Commit,
            _ => null,
        };
        defaultTip ??= repo.Head.Tip
            ?? throw new InvalidOperationException("The source repository has no commits.");
        Commands.Checkout(repo, defaultTip, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        log?.Invoke($"[server] checked out {defaultTip.Sha[..10]} (default branch).");
        return defaultTip.Sha;
    }

    private static string DescribeBranch(Repository repo) =>
        repo.Head?.FriendlyName ?? "unknown";

    private static FetchOptions FetchOptions(GitCredential? credential) => new()
    {
        CredentialsProvider = CredentialProvider(credential),
        Prune = true,
    };

    private static LibGit2Sharp.Handlers.CredentialsHandler? CredentialProvider(GitCredential? credential)
    {
        if (string.IsNullOrEmpty(credential?.Username))
            return null;
        return (_url, _user, _types) => new UsernamePasswordCredentials
        {
            Username = credential!.Username,
            Password = credential.Password ?? "",
        };
    }
}
