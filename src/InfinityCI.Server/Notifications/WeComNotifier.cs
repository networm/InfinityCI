using System.Text;
using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Runs;

namespace InfinityCI.Server.Notifications;

/// <summary>
/// Sends run-completion notifications to WeCom (企业微信) group-bot webhooks.
/// Per-workflow URLs come from WorkflowControlService; failures are logged and
/// never affect the run itself.
/// </summary>
public sealed class WeComNotifier(
    WorkflowControlService workflowControl,
    IHttpClientFactory httpClientFactory,
    ILogger<WeComNotifier> logger)
{
    public const string MarkdownColorSuccess = "info";
    public const string MarkdownColorFailed = "warning";
    public const string MarkdownColorNeutral = "comment";

    /// <summary>Builds the WeCom markdown payload body for a finished run.</summary>
    public static string BuildMessage(Run run)
    {
        var (statusText, color) = run.Status switch
        {
            RunStatus.Success => ("成功", MarkdownColorSuccess),
            RunStatus.Failed => ("失败", MarkdownColorFailed),
            RunStatus.Cancelled => ("已取消", MarkdownColorNeutral),
            _ => (run.Status.ToString(), MarkdownColorNeutral),
        };
        var duration = FormatDuration(run);
        var content =
            $"**Infinity CI 任务通知**\n" +
            $"> 任务：<font color=\"{color}\">{run.WorkflowName}</font> #{run.Id}\n" +
            $"> 状态：<font color=\"{color}\">{statusText}</font>\n" +
            $"> 触发人：{run.TriggeredBy}\n" +
            $"> 耗时：{duration}";
        return JsonSerializer.Serialize(new { msgtype = "markdown", markdown = new { content } });
    }

    private static string FormatDuration(Run run)
    {
        if (run.StartedAt is null)
            return "—";
        var end = run.FinishedAt ?? DateTimeOffset.UtcNow;
        var totalSeconds = Math.Max(0, (long)(end - run.StartedAt.Value).TotalSeconds);
        if (totalSeconds < 1)
            return "<1s";
        var days = totalSeconds / 86400;
        var hours = totalSeconds % 86400 / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        var units = new[] { (days, "天"), (hours, "小时"), (minutes, "分"), (seconds, "秒") };
        var parts = units.SkipWhile(u => u.Item1 == 0).Take(2).Where(u => u.Item1 > 0)
            .Select(u => $"{u.Item1}{u.Item2}").ToList();
        return parts.Count > 0 ? string.Concat(parts) : "<1s";
    }

    /// <summary>Subscribes to run events; call from a hosted service Start.</summary>
    public void Subscribe(RunEvents events)
    {
        events.RunUpdated += async run =>
        {
            if (!run.IsTerminal)
                return;
            try
            {
                var urls = await workflowControl.GetNotifyUrlsAsync();
                if (!urls.TryGetValue(run.WorkflowName, out var url))
                    return;
                await SendAsync(url, BuildMessage(run));
                logger.LogInformation("WeCom notification sent for run {RunId} ({Workflow})", run.Id, run.WorkflowName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send WeCom notification for run {RunId}", run.Id);
            }
        };
    }

    /// <summary>Posts the WeCom bot message. Internal for testability.</summary>
    public async Task SendAsync(string webhookUrl, string jsonBody, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("wecom");
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(webhookUrl, content, ct);
        response.EnsureSuccessStatusCode();
    }
}
