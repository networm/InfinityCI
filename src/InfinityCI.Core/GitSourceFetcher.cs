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
        return CheckoutTarget(repository, scm, log);
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

    /// <summary>Checks out the configured ref/branch and reports commit + branch name.</summary>
    private static CheckoutResult CheckoutTarget(Repository repo, ScmConfig scm, Action<string>? log)
    {
        if (scm.Ref is { } reference)
        {
            var commit = repo.Lookup<Commit>(reference)
                ?? throw new InvalidOperationException($"Ref '{reference}' not found in the source repository.");
            Commands.Checkout(repo, commit, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            log?.Invoke($"[server] checked out {commit.Sha[..10]} (ref {reference}).");
            return new CheckoutResult(commit.Sha, $"ref {reference}");
        }

        if (scm.Branch is { } branchName)
        {
            var remoteBranch = repo.Branches.FirstOrDefault(b => b.FriendlyName == $"origin/{branchName}")
                ?? throw new InvalidOperationException($"Branch '{branchName}' not found on origin.");
            // Checking out a remote branch creates a local tracking branch automatically.
            Commands.Checkout(repo, remoteBranch, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            var tip = remoteBranch.Tip ?? throw new InvalidOperationException($"Branch '{branchName}' has no commits.");
            log?.Invoke($"[server] checked out {tip.Sha[..10]} on branch {branchName}.");
            return new CheckoutResult(tip.Sha, branchName);
        }

        // No branch configured: follow origin's HEAD (fetch updates remote-tracking
        // refs but NOT local branches, so the remote tip is authoritative).
        var defaultBranch = repo.Refs["refs/remotes/origin/HEAD"] switch
        {
            SymbolicReference sym => repo.Branches[BranchFriendlyName(sym.Target.CanonicalName)],
            // Direct origin/HEAD: find the remote branch pointing at the same commit.
            DirectReference direct => repo.Branches
                .FirstOrDefault(b => b.FriendlyName.StartsWith("origin/", StringComparison.Ordinal)
                                     && b.Tip is not null && b.Tip.Sha == direct.TargetIdentifier),
            _ => null,
        };
        var defaultName = defaultBranch is null ? null : BranchStripOrigin(defaultBranch.FriendlyName);
        if (defaultBranch?.Tip is { } defaultTip)
        {
            Commands.Checkout(repo, defaultBranch, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            defaultName ??= "default";
            log?.Invoke($"[server] checked out {defaultTip.Sha[..10]} on branch {defaultName}.");
            return new CheckoutResult(defaultTip.Sha, defaultName);
        }

        var head = repo.Head.Tip ?? throw new InvalidOperationException("The source repository has no commits.");
        Commands.Checkout(repo, head, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        log?.Invoke($"[server] checked out {head.Sha[..10]} (default branch).");
        return new CheckoutResult(head.Sha, "default");
    }

    private static string BranchFriendlyName(string canonicalName) =>
        canonicalName.StartsWith("refs/remotes/", StringComparison.Ordinal)
            ? canonicalName["refs/remotes/".Length..]
            : canonicalName;

    private static string BranchStripOrigin(string friendlyName) =>
        friendlyName.StartsWith("origin/", StringComparison.Ordinal)
            ? friendlyName["origin/".Length..]
            : friendlyName;

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
