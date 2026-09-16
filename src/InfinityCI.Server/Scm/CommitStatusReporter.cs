using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server.Auth;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Scm;

/// <summary>
/// Reports run status back to the SCM host for workflows that declare
/// `commit_status: true` in their scm block. GitHub uses the commit status
/// API; GitLab hosts (any host containing "gitlab") use the pipeline-status
/// API on the repository origin; other non-github.com hosts are treated as
/// GitHub Enterprise. The scm credential supplies the API token. Failures are
/// logged and never affect the run itself.
/// </summary>
public sealed class CommitStatusReporter(
    IHttpClientFactory httpClientFactory,
    CredentialStore credentialStore,
    IOptions<CiServerOptions> optionsAccessor,
    ILogger<CommitStatusReporter> logger)
{
    public const string ContextPrefix = "ci/infinity";

    // $"{runId}:{sha}" -> last reported state; guards against duplicate reports
    // when a run's status is recomputed or several jobs discover the same sha.
    private readonly ConcurrentDictionary<string, string> _reported = new();

    /// <summary>Reports the run as pending once the first checkout pins a commit.</summary>
    public Task ReportPendingAsync(ScmConfig scm, long runId, string workflow, int runNumber, string commitSha) =>
        ReportAsync(scm, runId, workflow, runNumber, commitSha, "pending");

    /// <summary>Reports the terminal state of a run (success/failure).</summary>
    public Task ReportFinalAsync(ScmConfig scm, long runId, string workflow, int runNumber, string commitSha, RunStatus status)
    {
        var state = status switch
        {
            RunStatus.Success => "success",
            RunStatus.Failed => "failure",
            RunStatus.Cancelled => "failure",
            _ => null,
        };
        return state is null ? Task.CompletedTask : ReportAsync(scm, runId, workflow, runNumber, commitSha, state);
    }

    private async Task ReportAsync(ScmConfig scm, long runId, string workflow, int runNumber, string commitSha, string state)
    {
        try
        {
            if (!ShouldSend(runId, commitSha, state))
                return;

            var api = ResolveApi(scm.Url);
            var targetUrl = BuildTargetUrl(workflow, runNumber);
            var context = $"{ContextPrefix}/{workflow}";
            var description = $"#{runNumber} {Describe(state)}";

            GitCredential? credential = null;
            try
            {
                credential = credentialStore.Resolve(scm.Credentials);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Commit status for {Workflow} #{RunNumber}: credential '{Name}' not resolvable",
                    workflow, runNumber, scm.Credentials);
            }
            var token = credential?.Password ?? credential?.Username;

            var client = httpClientFactory.CreateClient("commit-status");
            HttpResponseMessage response;
            if (api.Provider == "gitlab")
            {
                var query = $"state={Uri.EscapeDataString(ToGitLabState(state))}&name={Uri.EscapeDataString(context)}&description={Uri.EscapeDataString(description)}";
                if (targetUrl is not null)
                    query += $"&target_url={Uri.EscapeDataString(targetUrl)}";
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{api.ApiBase}/projects/{Uri.EscapeDataString(api.RepoPath)}/statuses/{commitSha}?{query}");
                if (!string.IsNullOrEmpty(token))
                    request.Headers.Add("PRIVATE-TOKEN", token);
                response = await client.SendAsync(request);
            }
            else
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["state"] = state,
                    ["context"] = context,
                    ["description"] = description,
                    ["target_url"] = targetUrl,
                });
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{api.ApiBase}/repos/{api.RepoPath}/statuses/{commitSha}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.UserAgent.ParseAdd("InfinityCI");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                if (!string.IsNullOrEmpty(token))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                response = await client.SendAsync(request);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    logger.LogWarning("Commit status {State} for {Workflow} #{RunNumber} on {Host} returned {StatusCode}",
                        state, workflow, runNumber, new Uri(scm.Url).Host, (int)response.StatusCode);
                else
                    logger.LogInformation("Commit status {State} reported for {Workflow} #{RunNumber} on {CommitSha}",
                        state, workflow, runNumber, commitSha);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report commit status {State} for {Workflow} #{RunNumber}",
                state, workflow, runNumber);
        }
    }

    /// <summary>One pending per run; terminal states are sent once and supersede pending.</summary>
    private bool ShouldSend(long runId, string commitSha, string state)
    {
        var key = $"{runId}:{commitSha}";
        while (true)
        {
            if (_reported.TryGetValue(key, out var last))
            {
                if (last == state || (last != "pending" && state != "pending") || (state == "pending" && last != "pending"))
                    return false; // already reported at this (or a final) state
                if (_reported.TryUpdate(key, state, last))
                    return true;
            }
            else if (_reported.TryAdd(key, state))
            {
                return true;
            }
        }
    }

    private string? BuildTargetUrl(string workflow, int runNumber)
    {
        var origin = optionsAccessor.Value.PublicOrigin;
        return string.IsNullOrWhiteSpace(origin)
            ? null
            : $"{origin.TrimEnd('/')}/runs/{Uri.EscapeDataString(workflow)}/{runNumber}";
    }

    private static string Describe(string state) => state switch
    {
        "pending" => "running",
        "success" => "passed",
        _ => "failed",
    };

    private static string ToGitLabState(string state) => state switch
    {
        "failure" => "failed",
        var other => other,
    };

    internal sealed record ScmApi(string Provider, string ApiBase, string RepoPath);

    internal static ScmApi ResolveApi(string cloneUrl)
    {
        var uri = new Uri(cloneUrl);
        var path = uri.AbsolutePath.TrimEnd('/').TrimStart('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var isGitLab = uri.Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase);
        var apiBase = isGitLab
            ? $"{uri.Scheme}://{uri.Authority}/api/v4"
            : uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? "https://api.github.com"
                : $"{uri.Scheme}://{uri.Authority}/api/v3"; // GitHub Enterprise
        return new ScmApi(isGitLab ? "gitlab" : "github", apiBase, path);
    }
}
