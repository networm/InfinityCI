namespace InfinityCI.Server.Hubs;

/// <summary>SignalR group naming. One group per resource; tabs subscribe independently.</summary>
public static class CiGroups
{
    public const string Dashboard = "dashboard";
    public const string Agents = "agents";

    public static string Run(long runId) => $"run-{runId}";
}
