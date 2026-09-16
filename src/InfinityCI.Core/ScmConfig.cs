namespace InfinityCI.Core;

/// <summary>Optional Git source control block: steps run inside a checked-out copy.</summary>
public sealed class ScmConfig
{
    public required string Url { get; init; }
    /// <summary>Branch to track; null = the remote's default branch.</summary>
    public string? Branch { get; init; }
    /// <summary>Explicit tag or commit sha; takes precedence over <see cref="Branch"/>.</summary>
    public string? Ref { get; init; }
    /// <summary>Name of a stored credential for private repositories; also used as the
    /// API token for commit-status reporting.</summary>
    public string? Credentials { get; init; }

    /// <summary>Report run status back to the SCM host (GitHub commit status /
    /// GitLab pipeline status); provider is detected from the clone URL host.</summary>
    public bool CommitStatus { get; init; }
}

/// <summary>Resolved credentials handed to the checkout (plaintext, short-lived).</summary>
public sealed record GitCredential(string? Username, string? Password);

public sealed record CheckoutResult(string CommitSha, string Branch);
