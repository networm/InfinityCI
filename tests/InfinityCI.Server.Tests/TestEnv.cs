using System.Diagnostics;

namespace InfinityCI.Server.Tests;

internal static class TestEnv
{
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
