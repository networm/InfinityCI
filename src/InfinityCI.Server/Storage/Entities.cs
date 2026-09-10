using System.Text.Json.Serialization;

namespace InfinityCI.Server.Storage;

public sealed class User
{
    public long Id { get; set; }
    public required string Username { get; set; }
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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole { SuperAdmin, Admin, User }
