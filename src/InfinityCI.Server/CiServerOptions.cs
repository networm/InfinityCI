namespace InfinityCI.Server;

public sealed class CiServerOptions
{
    public const string SectionName = "InfinityCI";

    public string DataDir { get; set; } = "data";
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>Built SPA directory, relative to the content root ("wwwroot" for published single-EXE).</summary>
    public string WebDistDir { get; set; } = "../../web/dist";

    /// <summary>Write the sample workflow when no workflows exist (tests disable this).</summary>
    public bool CreateSampleWorkflow { get; set; } = true;

    /// <summary>Absolute base URL of this server, used in links sent to external systems
    /// (commit-status target_url, notifications); null = links are omitted.</summary>
    public string? PublicOrigin { get; set; }

    public string JobsDir => Path.Combine(DataDir, "jobs");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string WorkspacesDir => Path.Combine(DataDir, "workspaces");
    public string DbPath => Path.Combine(DataDir, "infinityci.db");
    public string DbConnectionString => $"Data Source={DbPath}";
}
