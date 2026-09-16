namespace InfinityCI.Server.Notifications;

/// <summary>Global SMTP server settings for the email notification channel
/// (`InfinityCI:Smtp` in appsettings.json). The per-workflow channel target is
/// the recipient address.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "InfinityCI:Smtp";

    /// <summary>SMTP host; empty = email channel is disabled (sends are skipped with a warning).</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public string From { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool EnableSsl { get; set; } = true;
}
