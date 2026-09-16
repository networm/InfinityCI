using System.Net;
using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Notifications;
using InfinityCI.Server.Runs;
using InfinityCI.Server.Realtime;
using InfinityCI.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Tests;

/// <summary>Notification channels: WeCom payload shape, channel routing and the failure-only filter.</summary>
public class NotificationChannelsTests
{
    private sealed class StubHandler(Func<string, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? ContentType)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var contentType = request.Content?.Headers.ContentType?.MediaType;
            Requests.Add((request.RequestUri!.ToString(), body, contentType));
            return respond(request.RequestUri.ToString(), body);
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static Run FinishedRun(RunStatus status) => new()
    {
        Id = 7,
        RunNumber = 3,
        WorkflowName = "notify-job",
        Project = "Default",
        TriggeredBy = "admin",
        Status = status,
        StartedAt = DateTimeOffset.UtcNow.AddSeconds(-50),
        FinishedAt = DateTimeOffset.UtcNow,
        Version = 2,
    };

    [Fact]
    public void WeComBuildMessage_ContainsRunFacts_AndWeComShape()
    {
        var json = WeComNotifier.BuildMessage(FinishedRun(RunStatus.Failed));
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal("markdown", doc.GetProperty("msgtype").GetString());
        var content = doc.GetProperty("markdown").GetProperty("content").GetString()!;
        Assert.Contains("notify-job", content);
        Assert.Contains("#7", content);
        Assert.Contains("失败", content);
        Assert.Contains("admin", content);
        Assert.Contains("50秒", content);
    }

    [Fact]
    public async Task WeCom_SendAsync_PostsMarkdownJson()
    {
        var handler = new StubHandler((_, _) => new(HttpStatusCode.OK));
        var notifier = new WeComNotifier(new StubHttpClientFactory(new HttpClient(handler)));
        var message = NotificationFormatter.Build(FinishedRun(RunStatus.Success), "https://ci.example.com");

        await notifier.SendAsync(message, "https://qyapi.example/webhook");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://qyapi.example/webhook", request.Url);
        Assert.Equal("application/json", request.ContentType);
        Assert.Contains("msgtype", request.Body);
    }

    [Fact]
    public async Task Slack_SendsPlainText()
    {
        var handler = new StubHandler((_, _) => new(HttpStatusCode.OK));
        var notifier = new SlackNotifier(new StubHttpClientFactory(new HttpClient(handler)));
        var message = NotificationFormatter.Build(FinishedRun(RunStatus.Success), null);

        await notifier.SendAsync(message, "https://hooks.slack.example/t/b/x");

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"text\":", request.Body);
        Assert.Contains("notify-job", request.Body);
    }

    [Fact]
    public async Task GenericWebhook_PostsStructuredPayload()
    {
        var handler = new StubHandler((_, _) => new(HttpStatusCode.OK));
        var notifier = new GenericWebhookNotifier(new StubHttpClientFactory(new HttpClient(handler)));
        var message = NotificationFormatter.Build(FinishedRun(RunStatus.Failed), "https://ci.example.com");

        await notifier.SendAsync(message, "https://ops.example/hook");

        var request = Assert.Single(handler.Requests);
        var doc = JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("notify-job", doc.GetProperty("workflow").GetString());
        Assert.Equal("Failed", doc.GetProperty("status").GetString());
        Assert.Equal(3, doc.GetProperty("runNumber").GetInt32());
        Assert.Equal("https://ci.example.com/runs/notify-job/3", doc.GetProperty("url").GetString());
    }

    [Fact]
    public async Task DingTalk_SendsMarkdownWithTitle()
    {
        var handler = new StubHandler((_, _) => new(HttpStatusCode.OK));
        var notifier = new DingTalkNotifier(new StubHttpClientFactory(new HttpClient(handler)));
        var message = NotificationFormatter.Build(FinishedRun(RunStatus.Success), null);

        await notifier.SendAsync(message, "https://oapi.dingtalk.example/robot/send?x");

        var request = Assert.Single(handler.Requests);
        var doc = JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("markdown", doc.GetProperty("msgtype").GetString());
        Assert.Contains("notify-job", doc.GetProperty("markdown").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Email_WithoutSmtpHost_IsSkippedWithoutError()
    {
        var notifier = new EmailNotifier(Options.Create(new SmtpOptions()), NullLogger<EmailNotifier>.Instance);
        var message = NotificationFormatter.Build(FinishedRun(RunStatus.Success), null);

        await notifier.SendAsync(message, "ops@example.com"); // must not throw
    }

    [Fact]
    public async Task Dispatcher_FailureOnlyChannel_SkipsSuccessfulRuns()
    {
        var channels = new List<NotifyChannel> { new("wecom", "https://qyapi.example/x", "failure") };
        var control = new StubWorkflowControl(channels);
        var sent = new List<string>();
        var notifier = new CapturingNotifier("wecom", sent);
        var dispatcher = new NotificationDispatcher(
            control,
            [notifier],
            Options.Create(new CiServerOptions()),
            NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(FinishedRun(RunStatus.Success));
        Assert.Empty(sent);

        await dispatcher.DispatchAsync(FinishedRun(RunStatus.Failed));
        Assert.Single(sent);
    }

    [Fact]
    public async Task Dispatcher_UnknownChannelType_IsSkipped()
    {
        var control = new StubWorkflowControl([new NotifyChannel("fax", "555", "always")]);
        var sent = new List<string>();
        var dispatcher = new NotificationDispatcher(
            control,
            [new CapturingNotifier("wecom", sent)],
            Options.Create(new CiServerOptions()),
            NullLogger<NotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(FinishedRun(RunStatus.Failed));
        Assert.Empty(sent);
    }

    private sealed class CapturingNotifier(string type, List<string> sent) : INotifier
    {
        public string Type => type;
        public Task SendAsync(NotificationMessage message, string target, CancellationToken ct = default)
        {
            sent.Add($"{target}:{message.Subject}");
            return Task.CompletedTask;
        }
    }

    private sealed class StubWorkflowControl(List<NotifyChannel> channels) : WorkflowControlService(
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        new WorkflowStore(Options.Create(new CiServerOptions { DataDir = "." }),
            new WorkflowGitStore(Options.Create(new CiServerOptions { DataDir = "." }),
                NullLogger<WorkflowGitStore>.Instance),
            new ChangeEvents(NullLogger<ChangeEvents>.Instance),
            NullLogger<WorkflowStore>.Instance),
        new ChangeEvents(NullLogger<ChangeEvents>.Instance),
        NullLogger<WorkflowControlService>.Instance)
    {
        public override Task<List<NotifyChannel>> GetNotifyChannelsAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult(channels);
    }
}
