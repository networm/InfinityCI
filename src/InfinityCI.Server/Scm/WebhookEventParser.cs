using System.Text.Json;

namespace InfinityCI.Server.Scm;

/// <summary>Normalized incoming SCM webhook event, independent of the provider wire format.</summary>
public sealed record ScmWebhookEvent(
    string Provider,           // github | gitlab | gitea | generic
    string Kind,               // push | pull_request | ping
    string? Branch,            // push: pushed branch; PR: source branch (informational)
    long? PrNumber = null,
    string? PrAction = null,   // opened | synchronize | reopened | closed | ...
    string? PrTitle = null,
    string? PrSourceBranch = null,
    string? PrTargetBranch = null);

/// <summary>
/// Parses provider-specific webhook payloads into <see cref="ScmWebhookEvent"/>.
/// Provider is detected from the event headers: X-GitHub-Event, X-Gitea-Event
/// (GitHub-like payload shapes) or X-Gitlab-Event (GitLab shapes). Requests
/// without any of those headers are treated as generic JSON payloads.
/// </summary>
public static class WebhookEventParser
{
    public static ScmWebhookEvent? Parse(IHeaderDictionary headers, JsonElement root)
    {
        var githubEvent = Header(headers, "X-GitHub-Event");
        if (githubEvent is not null)
            return ParseGitHubLike("github", githubEvent, root);

        var giteaEvent = Header(headers, "X-Gitea-Event");
        if (giteaEvent is not null)
            return ParseGitHubLike("gitea", giteaEvent, root);

        var gitlabEvent = Header(headers, "X-Gitlab-Event");
        if (gitlabEvent is not null)
            return ParseGitLab(gitlabEvent, root);

        return new ScmWebhookEvent("generic", "push", Branch: ExtractRef(root));
    }

    private static ScmWebhookEvent ParseGitHubLike(string provider, string eventName, JsonElement root)
    {
        switch (eventName)
        {
            case "ping":
                return new ScmWebhookEvent(provider, "ping", Branch: null);
            case "pull_request" or "merge_request":
                return new ScmWebhookEvent(
                    provider,
                    "pull_request",
                    Branch: Str(root, "pull_request", "head", "ref") ?? Str(root, "pull_request", "head", "ref_name"),
                    PrNumber: root.TryGetProperty("number", out var number) && number.TryGetInt64(out var n) ? n : null,
                    PrAction: NormalizePrAction(Str(root, "action")),
                    PrTitle: Str(root, "pull_request", "title"),
                    PrSourceBranch: Str(root, "pull_request", "head", "ref") ?? Str(root, "pull_request", "head", "ref_name"),
                    PrTargetBranch: Str(root, "pull_request", "base", "ref") ?? Str(root, "pull_request", "base", "ref_name"));
            default: // push and anything else behaves as a push
                return new ScmWebhookEvent(provider, "push", Branch: ExtractRef(root));
        }
    }

    private static ScmWebhookEvent ParseGitLab(string eventName, JsonElement root)
    {
        switch (eventName)
        {
            case "Merge Request Hook":
                return new ScmWebhookEvent(
                    "gitlab",
                    "pull_request",
                    Branch: Str(root, "object_attributes", "source_branch"),
                    PrNumber: root.TryGetProperty("object_attributes", out var attrs) && attrs.TryGetProperty("iid", out var iid) && iid.TryGetInt64(out var n) ? n : null,
                    PrAction: NormalizePrAction(Str(root, "object_attributes", "action")),
                    PrTitle: Str(root, "object_attributes", "title"),
                    PrSourceBranch: Str(root, "object_attributes", "source_branch"),
                    PrTargetBranch: Str(root, "object_attributes", "target_branch"));
            case "Tag Push Hook":
                return new ScmWebhookEvent("gitlab", "push", Branch: null);
            default: // "Push Hook" and anything else
                return new ScmWebhookEvent("gitlab", "push", Branch: ExtractRef(root));
        }
    }

    /// <summary>Map provider-specific PR actions onto GitHub's vocabulary. Actions that
    /// don't represent new code (closed, renamed, ...) keep their raw name; the trigger
    /// endpoint only builds on opened/synchronize/reopened.</summary>
    private static string? NormalizePrAction(string? action) => action switch
    {
        "open" => "opened",
        "update" => "synchronize",
        "reopen" => "reopened",
        var other => other,
    };

    private static string? ExtractRef(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("ref", out var refr) && refr.ValueKind == JsonValueKind.String)
        {
            var value = refr.GetString() ?? "";
            const string heads = "refs/heads/";
            return value.StartsWith(heads, StringComparison.OrdinalIgnoreCase) ? value[heads.Length..] : value;
        }
        if (root.TryGetProperty("branch", out var branch) && branch.ValueKind == JsonValueKind.String)
            return branch.GetString();
        return null;
    }

    private static string? Str(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var (segment, isLast) in path.Select((s, i) => (s, i == path.Length - 1)))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
            if (isLast && current.ValueKind == JsonValueKind.String)
                return current.GetString();
        }
        return null;
    }

    private static string? Header(IHeaderDictionary headers, string name) =>
        headers[name].FirstOrDefault();
}
