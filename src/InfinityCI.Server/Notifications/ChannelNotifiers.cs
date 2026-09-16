using System.Text;
using System.Text.Json;
using InfinityCI.Core;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Notifications;

/// <summary>DingTalk (钉钉) group-bot webhooks; markdown message with a title.</summary>
public sealed class DingTalkNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public string Type => "dingtalk";

    public async Task SendAsync(NotificationMessage message, string target, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            msgtype = "markdown",
            markdown = new { title = message.Subject, text = message.Markdown },
        });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient("dingtalk").PostAsync(target, content, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Slack incoming webhooks; plain text payload.</summary>
public sealed class SlackNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public string Type => "slack";

    public async Task SendAsync(NotificationMessage message, string target, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { text = message.Text });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient("slack").PostAsync(target, content, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Generic outbound webhooks: posts a structured JSON event body, so any
/// in-house system (or a relay like n8n) can consume run completions.</summary>
public sealed class GenericWebhookNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public string Type => "webhook";

    public async Task SendAsync(NotificationMessage message, string target, CancellationToken ct = default)
    {
        var run = message.Run;
        var durationSeconds = run.StartedAt is null
            ? (long?)null
            : (long?)Math.Max(0, ((run.FinishedAt ?? DateTimeOffset.UtcNow) - run.StartedAt.Value).TotalSeconds);
        var json = JsonSerializer.Serialize(new
        {
            workflow = run.WorkflowName,
            project = run.Project,
            runId = run.Id,
            runNumber = run.RunNumber,
            status = run.Status.ToString(),
            triggeredBy = run.TriggeredBy,
            branch = run.SourceBranch,
            durationSeconds,
            url = message.Url,
        });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient("webhook").PostAsync(target, content, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Email notifications over SMTP. The server (host/port/from/credentials)
/// is configured globally via `InfinityCI:Smtp`; the channel target is the
/// recipient address (comma-separated for several). A missing host is a no-op
/// with a warning, so workflows can carry email channels before SMTP is set up.</summary>
public sealed class EmailNotifier(IOptions<SmtpOptions> optionsAccessor, ILogger<EmailNotifier> logger) : INotifier
{
    public string Type => "email";

    public async Task SendAsync(NotificationMessage message, string target, CancellationToken ct = default)
    {
        var smtp = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            logger.LogWarning("Email notification for {Workflow} skipped: InfinityCI:Smtp:Host is not configured",
                message.Run.WorkflowName);
            return;
        }

#pragma warning disable SYSLIB0010 // SmtpClient is the only built-in SMTP client; MailKit would add a dependency
        using var client = new System.Net.Mail.SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            Credentials = string.IsNullOrEmpty(smtp.Username)
                ? null
                : new System.Net.NetworkCredential(smtp.Username, smtp.Password ?? ""),
        };
        using var mail = new System.Net.Mail.MailMessage(smtp.From, target)
        {
            Subject = message.Subject,
            Body = message.Text,
        };
        await client.SendMailAsync(mail, ct);
#pragma warning restore SYSLIB0010
    }
}
