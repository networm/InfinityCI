namespace InfinityCI.Server;

public sealed class CiServerOptions
{
    public const string SectionName = "InfinityCI";

    public string DataDir { get; set; } = "data";
    public int MaxConcurrentBuilds { get; set; } = 1;

    public string JobsDir => Path.Combine(DataDir, "jobs");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string WorkspacesDir => Path.Combine(DataDir, "workspaces");
    public string DbPath => Path.Combine(DataDir, "infinityci.db");
    public string DbConnectionString => $"Data Source={DbPath}";
}
