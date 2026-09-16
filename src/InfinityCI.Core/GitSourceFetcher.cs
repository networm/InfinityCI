using System.Diagnostics;
using System.Text;

namespace InfinityCI.Core;

/// <summary>
/// Ensures a job workspace contains a checkout of the workflow's Git source:
/// clones when the directory is fresh, otherwise fetches and hard-resets to
/// the requested branch/ref. Returns the checked-out commit and branch.
/// Implemented with the git CLI (LibGit2Sharp remains reserved for the
/// workflow config repositories) so very large source repositories stay fast
/// and memory-friendly on agents and the server alike.
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
            // --no-checkout mirrors the old CloneOptions.Checkout=false: the
            // exact branch/ref is checked out afterwards in one code path
            // (so unknown branches get a clean error).
            RunGit(workspace, scm.Url, credential, "clone", "--no-checkout", scm.Url, ".");
        }
        else
        {
            log?.Invoke("[server] fetching origin...");
            RunGit(workspace, scm.Url, credential, "fetch", "--prune", "origin");
        }

        return CheckoutTarget(scm, workspace, credential, log);
    }

    /// <summary>Checks out the configured ref/branch and reports commit + branch name.</summary>
    private static CheckoutResult CheckoutTarget(ScmConfig scm, string workspace, GitCredential? credential, Action<string>? log)
    {
        if (scm.Ref is { } reference)
        {
            if (TryGit(workspace, scm.Url, credential, "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}") is null)
                throw new InvalidOperationException(Msg.T(
                    $"Ref '{reference}' not found in the source repository.",
                    $"源仓库中找不到 Ref「{reference}」。"));
            RunGit(workspace, scm.Url, credential, "checkout", "--force", reference);
            var refSha = HeadSha(workspace, scm.Url, credential);
            log?.Invoke($"[server] checked out {refSha[..10]} (ref {reference}).");
            return new CheckoutResult(refSha, $"ref {reference}");
        }

        if (scm.Branch is { } branchName)
        {
            var remoteBranch = $"origin/{branchName}";
            if (TryGit(workspace, scm.Url, credential, "rev-parse", "--verify", "--quiet", remoteBranch) is null)
                throw new InvalidOperationException(Msg.T(
                    $"Branch '{branchName}' not found on origin.",
                    $"在 origin 上找不到分支「{branchName}」。"));
            // Reset/create a local branch on the remote tip (tracking it), the
            // CLI equivalent of checking out a remote branch.
            RunGit(workspace, scm.Url, credential, "checkout", "--force", "-B", branchName, "--track", remoteBranch);
            var branchSha = HeadSha(workspace, scm.Url, credential);
            log?.Invoke($"[server] checked out {branchSha[..10]} on branch {branchName}.");
            return new CheckoutResult(branchSha, branchName);
        }

        // No branch configured: follow origin's HEAD (clone records it as a
        // symbolic ref; fetch keeps it authoritative).
        var headRef = TryGit(workspace, scm.Url, credential, "symbolic-ref", "--quiet", "refs/remotes/origin/HEAD");
        var defaultName = headRef is null ? null : headRef["refs/remotes/origin/".Length..];
        if (defaultName is { Length: > 0 } name)
        {
            RunGit(workspace, scm.Url, credential, "checkout", "--force", "-B", name, "--track", $"origin/{name}");
        }
        else
        {
            RunGit(workspace, scm.Url, credential, "checkout", "--force");
            defaultName = "default";
        }
        var sha = HeadSha(workspace, scm.Url, credential);
        log?.Invoke($"[server] checked out {sha[..10]} on branch {defaultName}.");
        return new CheckoutResult(sha, defaultName);
    }

    private static string HeadSha(string workspace, string url, GitCredential? credential) =>
        RunGit(workspace, url, credential, "rev-parse", "HEAD");

    /// <summary>Runs git and throws on failure. Network operations wait without
    /// a timeout (large repositories may legitimately take a long time);
    /// GIT_TERMINAL_PROMPT=0 and a disabled credential helper guarantee a
    /// missing credential fails fast instead of prompting.</summary>
    private static string RunGit(string workspace, string url, GitCredential? credential, params string[] args)
    {
        var (output, exitCode, error) = Execute(workspace, url, credential, args);
        if (exitCode != 0)
            throw new InvalidOperationException(Msg.T(
                $"git {args[0]} failed ({exitCode}): {error.Trim()}",
                $"git {args[0]} 失败（{exitCode}）：{error.Trim()}"));
        return output;
    }

    /// <summary>Probe variant: returns trimmed stdout, or null when git exits non-zero.</summary>
    private static string? TryGit(string workspace, string url, GitCredential? credential, params string[] args)
    {
        var (output, exitCode, _) = Execute(workspace, url, credential, args);
        return exitCode == 0 ? output : null;
    }

    private static (string Output, int ExitCode, string Error) Execute(
        string workspace, string url, GitCredential? credential, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Credentials go in an extra HTTP header instead of the URL: they never
        // touch .git/config or the process command line visible in logs.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("credential.helper=");
        if (credential?.Username is { Length: > 0 } user && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{credential.Password ?? ""}"));
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"http.extraHeader=Authorization: Basic {token}");
        }
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (stdout.Result.Trim(), process.ExitCode, stderr.Result);
    }
}
