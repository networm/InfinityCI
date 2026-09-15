using System.Diagnostics;
using System.Text.RegularExpressions;

namespace InfinityCI.Server.Tests;

internal static class TestEnv
{
    /// <summary>Writes a workflow config in the task-first layout:
    /// {dataDir}/{sanitized workflow name}/workflow.yml (derived from the YAML's name field).</summary>
    public static string WriteWorkflow(string dataDir, string yaml)
    {
        var nameMatch = Regex.Match(yaml, @"^name:\s*(.+)$", RegexOptions.Multiline);
        if (!nameMatch.Success)
            throw new ArgumentException("Workflow YAML is missing a name.");
        var name = nameMatch.Groups[1].Value.Trim();
        var dir = Path.Combine(dataDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workflow.yml"), yaml);
        return name;
    }

    public static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "infinityci-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, int intervalMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return;
            await Task.Delay(intervalMs);
        }
        throw new TimeoutException("Condition was not met within the timeout.");
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, int intervalMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
                return;
            await Task.Delay(intervalMs);
        }
        throw new TimeoutException("Condition was not met within the timeout.");
    }
}
