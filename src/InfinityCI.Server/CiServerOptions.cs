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

    public string JobsDir => Path.Combine(DataDir, "jobs");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string WorkspacesDir => Path.Combine(DataDir, "workspaces");
    public string DbPath => Path.Combine(DataDir, "infinityci.db");
    public string DbConnectionString => $"Data Source={DbPath}";
}
