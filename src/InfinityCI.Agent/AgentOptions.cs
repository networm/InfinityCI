namespace InfinityCI.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Master base URL; agents always connect outbound.</summary>
    public string MasterUrl { get; set; } = "http://127.0.0.1:5000";

    public string AgentName { get; set; } = Environment.MachineName;

    public string Version { get; set; } = "1.0.0";

    public string[] Labels { get; set; } = [];

    public int MaxConcurrentBuilds { get; set; } = 1;

    /// <summary>Local state (agent id, workspaces), relative to the agent's working directory.</summary>
    public string DataDir { get; set; } = "agent-data";

    /// <summary>Seconds between heartbeats; must stay well under the master's lease timeout.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 5;

    public string WorkspacesDir => Path.Combine(DataDir, "workspaces");
    public string AgentIdPath => Path.Combine(DataDir, "agent-id.txt");
}
