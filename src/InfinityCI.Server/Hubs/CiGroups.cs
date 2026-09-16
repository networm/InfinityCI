namespace InfinityCI.Server.Hubs;

/// <summary>SignalR group naming. One group per resource; tabs subscribe independently.
/// Run/job/workflow events fan out per project group so subscribers only ever
/// receive projects they are allowed to see.</summary>
public static class CiGroups
{
    public const string Agents = "agents";
    public const string Projects = "projects";
    public const string Users = "users";

    public static string Run(long runId) => $"run-{runId}";

    public static string Project(string projectName) => $"project-{projectName}";
}
