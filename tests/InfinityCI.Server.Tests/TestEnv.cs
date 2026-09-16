using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Scm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Tests;

internal static class TestEnv
{
    /// <summary>A CommitStatusReporter whose HTTP calls would fail loudly — tests
    /// using it never enable commit_status, so no request is ever made.</summary>
    public static CommitStatusReporter CreateCommitStatusReporter(CredentialStore credentialStore) =>
        new(
            new FailingHttpClientFactory(),
            credentialStore,
            Options.Create(new CiServerOptions { DataDir = "." }),
            NullLogger<CommitStatusReporter>.Instance);

    private sealed class FailingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new HttpClientHandlerStub());
    }

    private sealed class HttpClientHandlerStub : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    /// <summary>Writes a workflow config in the task-first layout:
    /// {dataDir}/{sanitized workflow name}/workflow.yml (derived from the YAML's name field).</summary>
    public static string WriteWorkflow(string dataDir, string yaml)
    {
        var nameMatch = Regex.Match(yaml, @"^name:\s*(.+)$", RegexOptions.Multiline);
        if (!nameMatch.Success)
            throw new ArgumentException("Workflow YAML is missing a name.");
        var name = nameMatch.Groups[1].Value.Trim();
        var dir = Path.Combine(dataDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workflow.yml"), yaml);
        return name;
    }

    public static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "infinityci-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, int intervalMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return;
            await Task.Delay(intervalMs);
        }
        throw new TimeoutException("Condition was not met within the timeout.");
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, int intervalMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
                return;
            await Task.Delay(intervalMs);
        }
        throw new TimeoutException("Condition was not met within the timeout.");
    }
}
