using System.Text.Json.Serialization;

namespace InfinityCI.Server.Storage;

public sealed class User
{
    public long Id { get; set; }
    public required string Username { get; set; }
    /// <summary>Human-readable name shown in the UI instead of the username.</summary>
    public string? DisplayName { get; set; }
    /// <summary>PBKDF2-SHA256 hash in "iterations.saltB64.hashB64" form.</summary>
    public required string PasswordHash { get; set; }
    public required string Role { get; set; }
    public List<UserProject> Projects { get; set; } = [];
}

public sealed class Project
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<UserProject> Users { get; set; } = [];
}

/// <summary>Grants a user visibility of one project.</summary>
public sealed class UserProject
{
    public long UserId { get; set; }
    public User? User { get; set; }
    public long ProjectId { get; set; }
    public Project? Project { get; set; }
}

/// <summary>A workflow a user pinned to their favorites section.</summary>
public sealed class UserFavorite
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public required string WorkflowName { get; set; }
}

/// <summary>Single-row marker of the current database schema version.</summary>
public sealed class VersionInfo
{
    public int Id { get; set; }
    public int Version { get; set; }
    public DateTimeOffset AppliedUtc { get; set; }
}

/// <summary>Runtime state of a workflow (enablement, webhook, notifications),
/// kept out of the config Git repo since these are operational toggles.</summary>
public sealed class WorkflowState
{
    public required string WorkflowName { get; set; }
    public bool Enabled { get; set; } = true;
    public string? WebhookToken { get; set; }

    /// <summary>Shared secret for HMAC-SHA256 request signing of push webhooks; null = unsigned.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Comma-separated branch patterns (`release/*`, `main`) a push webhook must match; null = any branch.</summary>
    public string? WebhookBranches { get; set; }

    /// <summary>Enabled webhook event kinds (comma-separated: "push", "pr"); null = push only.</summary>
    public string? WebhookEvents { get; set; }

    public string? NotifyWebhookUrl { get; set; }

    /// <summary>JSON list of notification channels ({type, target, events}); supersedes <see cref="NotifyWebhookUrl"/>.</summary>
    public string? NotifyChannelsJson { get; set; }

    /// <summary>Working-directory override for locally executed jobs (absolute, or relative
    /// to the data directory); null = default per-run isolated workspaces. Kept as runtime
    /// state, deliberately outside the workflow YAML.</summary>
    public string? WorkspaceDir { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>One outbound notification channel configured for a workflow.
/// Type: wecom | dingtalk | slack | webhook | email. Events: always | failure.</summary>
public sealed record NotifyChannel(string Type, string Target, string Events = "always");

/// <summary>Named Git credential for private repositories; secret is Data-Protection encrypted.</summary>
public sealed class StoredCredential
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string Username { get; set; }
    public required string EncryptedSecret { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>Global LDAP directory settings (single row, Id = 1), editable from
/// the admin page. Empty fields fall back to the "InfinityCI:Ldap" appsettings
/// defaults; the bind password is stored Data-Protection encrypted.</summary>
public sealed class LdapSettings
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string Server { get; set; } = "";
    public int Port { get; set; } = 389;
    public string BaseDn { get; set; } = "";
    public string BindDn { get; set; } = "";
    public string? EncryptedBindPassword { get; set; }
    public string UserSearchFilter { get; set; } = "";
    public string DisplayNameAttribute { get; set; } = "";
    public bool UseSsl { get; set; }
    public bool StartTls { get; set; }
    public bool AcceptAnyCertificate { get; set; }
    public string? AdminGroupDn { get; set; }
    public string? DefaultProject { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Persistent agent record: enrollment + lifecycle (enable/disable).</summary>
public sealed class AgentRecord
{
    public required string Id { get; set; }          // stable agent-generated id
    public required string Name { get; set; }
    public required string LabelsJson { get; set; } = "[]";
    public int MaxConcurrentBuilds { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset EnrolledAt { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }

    /// <summary>Agent-wide environment variables (name -> value) exported into every
    /// step this agent runs; serialized JSON, maintained from the config UI.</summary>
    public string EnvironmentJson { get; set; } = "{}";
}

/// <summary>One-time token an agent presents on registration.</summary>
public sealed class AgentEnrollment
{
    public long Id { get; set; }
    public required string Token { get; set; }
    public required string Name { get; set; }
    public required string LabelsJson { get; set; } = "[]";
    public int MaxConcurrentBuilds { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; }
    public string? UsedByAgentId { get; set; }
}

/// <summary>User-scoped API token for machine/CLI access (sent as `Authorization: Bearer`).
/// Only a PBKDF2 hash is stored; the plaintext is shown once at creation.</summary>
public sealed class ApiToken
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole { SuperAdmin, Admin, User }
