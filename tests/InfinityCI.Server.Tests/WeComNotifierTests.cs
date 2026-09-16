using System.Net;
using System.Text.Json;
using System.Text;
using InfinityCI.Core;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Notifications;
using InfinityCI.Server.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using InfinityCI.Server.Realtime;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Tests;

public class WeComNotifierTests
{
    private sealed class StubHandler(Func<string, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body!));
            return respond(request.RequestUri.ToString(), body);
        }
    }

    [Fact]
    public void BuildMessage_ContainsRunFacts_AndWeComShape()
    {
        var run = new Run
        {
            Id = 12,
            WorkflowName = "demo",
            Project = "Default",
            TriggeredBy = "管理员",
            Status = RunStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-50),
            FinishedAt = DateTimeOffset.UtcNow,
        };
        var json = WeComNotifier.BuildMessage(run);
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal("markdown", doc.GetProperty("msgtype").GetString());
        var content = doc.GetProperty("markdown").GetProperty("content").GetString()!;
        Assert.Contains("demo", content);
        Assert.Contains("#12", content);
        Assert.Contains("失败", content);
        Assert.Contains("管理员", content);
        Assert.Contains("50秒", content);
    }

    [Fact]
    public async Task TerminalRun_WithConfiguredUrl_SendsNotification()
    {
        string? capturedBody = null;
        var handler = new StubHandler((url, body) =>
        {
            capturedBody = body;
            return new(HttpStatusCode.OK);
        });
        var httpClientFactory = new StubHttpClientFactory(new HttpClient(handler));

        var events = new RunEvents(NullLogger<RunEvents>.Instance);
        var notifier = new WeComNotifier(new StubWorkflowControl("https://qyapi.example/webhook"), httpClientFactory, NullLogger<WeComNotifier>.Instance);
        notifier.Subscribe(events);

        var run = new Run
        {
            Id = 7,
            WorkflowName = "notify-job",
            Project = "Default",
            TriggeredBy = "admin",
            Status = RunStatus.Success,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-3),
            FinishedAt = DateTimeOffset.UtcNow,
            Version = 2,
        };
        await events.PublishRunUpdatedAsync(run);

        Assert.NotNull(capturedBody);
        Assert.Contains("notify-job", capturedBody);
    }

    [Fact]
    public async Task NonTerminalRun_IsIgnored()
    {
        var handler = new StubHandler((_, _) => new(HttpStatusCode.OK));
        var events = new RunEvents(NullLogger<RunEvents>.Instance);
        var notifier = new WeComNotifier(new StubWorkflowControl("https://qyapi.example/webhook"), new StubHttpClientFactory(new HttpClient(handler)), NullLogger<WeComNotifier>.Instance);
        notifier.Subscribe(events);

        await events.PublishRunUpdatedAsync(new Run { Id = 1, WorkflowName = "notify-job", Project = "Default", Status = RunStatus.Running, Version = 1 });
        Assert.Empty(handler.Requests);
    }

    private sealed class StubWorkflowControl(string url) : WorkflowControlService(
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        new WorkflowStore(Options.Create(new CiServerOptions { DataDir = "." }),
            new WorkflowGitStore(Options.Create(new CiServerOptions { DataDir = "." }),
                NullLogger<WorkflowGitStore>.Instance),
            new ChangeEvents(NullLogger<ChangeEvents>.Instance),
            NullLogger<WorkflowStore>.Instance),
        new ChangeEvents(NullLogger<ChangeEvents>.Instance),
        NullLogger<WorkflowControlService>.Instance)
    {
        public override Task<Dictionary<string, string>> GetNotifyUrlsAsync(CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string> { ["notify-job"] = url });
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
