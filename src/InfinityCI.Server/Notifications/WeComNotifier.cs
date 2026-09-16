using System.Text;
using System.Text.Json;
using InfinityCI.Core;

namespace InfinityCI.Server.Notifications;

/// <summary>
/// Sends run notifications to WeCom (企业微信) group-bot webhooks. Failures in
/// the underlying HTTP call propagate to the dispatcher, which logs them.
/// </summary>
public sealed class WeComNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public const string MarkdownColorSuccess = "info";
    public const string MarkdownColorFailed = "warning";
    public const string MarkdownColorNeutral = "comment";

    public string Type => "wecom";

    /// <summary>Builds the WeCom markdown payload body for a finished run.</summary>
    public static string BuildMessage(Run run)
    {
        var color = run.Status switch
        {
            RunStatus.Success => MarkdownColorSuccess,
            RunStatus.Failed => MarkdownColorFailed,
            _ => MarkdownColorNeutral,
        };
        var statusText = NotificationFormatter.StatusText(run.Status);
        var content =
            $"**Infinity CI 任务通知**\n" +
            $"> 任务：<font color=\"{color}\">{run.WorkflowName}</font> #{run.Id}\n" +
            $"> 状态：<font color=\"{color}\">{statusText}</font>\n" +
            $"> 触发人：{run.TriggeredBy}\n" +
            $"> 耗时：{NotificationFormatter.FormatDuration(run)}";
        return JsonSerializer.Serialize(new { msgtype = "markdown", markdown = new { content } });
    }

    public async Task SendAsync(NotificationMessage message, string target, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { msgtype = "markdown", markdown = new { content = message.Markdown } });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient("wecom").PostAsync(target, content, ct);
        response.EnsureSuccessStatusCode();
    }
}
