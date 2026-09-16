using InfinityCI.Core;

namespace InfinityCI.Server.Notifications;

/// <summary>Renders one terminal run into the shared message shapes all channels consume.</summary>
public static class NotificationFormatter
{
    public static string StatusText(RunStatus status) => status switch
    {
        RunStatus.Success => "成功",
        RunStatus.Failed => "失败",
        RunStatus.Cancelled => "已取消",
        _ => status.ToString(),
    };

    public static string FormatDuration(Run run)
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

    /// <summary>Builds the complete message set for one terminal run.</summary>
    public static NotificationMessage Build(Run run, string? publicOrigin)
    {
        var statusText = StatusText(run.Status);
        var url = string.IsNullOrWhiteSpace(publicOrigin)
            ? null
            : $"{publicOrigin.TrimEnd('/')}/runs/{Uri.EscapeDataString(run.WorkflowName)}/{run.RunNumber}";
        var subject = $"[Infinity CI] {run.WorkflowName} #{run.RunNumber} {statusText}";
        var duration = FormatDuration(run);

        var color = run.Status switch
        {
            RunStatus.Success => "info",
            RunStatus.Failed => "warning",
            _ => "comment",
        };
        var markdown =
            $"**Infinity CI 任务通知**\n" +
            $"> 任务：<font color=\"{color}\">{run.WorkflowName}</font> #{run.RunNumber}\n" +
            $"> 状态：<font color=\"{color}\">{statusText}</font>\n" +
            $"> 触发人：{run.TriggeredBy}\n" +
            $"> 耗时：{duration}" +
            (url is null ? "" : $"\n> 详情：{url}");

        var text =
            $"Infinity CI: {run.WorkflowName} #{run.RunNumber} {statusText}\n" +
            $"Triggered by: {run.TriggeredBy}\n" +
            $"Duration: {duration}" +
            (url is null ? "" : $"\n{url}");

        return new NotificationMessage(run, statusText, subject, markdown, text, url);
    }
}
