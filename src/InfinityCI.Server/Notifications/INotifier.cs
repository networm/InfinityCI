using InfinityCI.Core;

namespace InfinityCI.Server.Notifications;

/// <summary>Everything a notification channel needs to render its payload for one finished run.</summary>
public sealed record NotificationMessage(
    Run Run,
    string StatusText,
    string Subject,
    string Markdown,
    string Text,
    string? Url);

/// <summary>One outbound notification channel implementation (WeCom, DingTalk, Slack,
/// generic webhook, email). Implementations must be safe to call for any terminal run.</summary>
public interface INotifier
{
    /// <summary>Channel discriminator stored in the workflow notification config.</summary>
    string Type { get; }

    /// <summary>Delivers the message to one target (webhook URL or recipient list).</summary>
    Task SendAsync(NotificationMessage message, string target, CancellationToken ct = default);
}
