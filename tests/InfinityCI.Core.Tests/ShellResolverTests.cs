using System.Diagnostics;
using InfinityCI.Core;
using Xunit;

namespace InfinityCI.Core.Tests;

public class ShellResolverTests
{
    [Fact]
    public void CreateStartInfo_DefaultShell_UsesPlatformShell()
    {
        var psi = ShellResolver.CreateStartInfo("echo hello");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", Path.GetFileName(psi.FileName));
            Assert.Contains("echo hello", psi.Arguments);
        }
        else
        {
            Assert.Equal("/bin/sh", psi.FileName);
            Assert.Equal("echo hello", psi.ArgumentList[1]);
        }

        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_Environment_MergedIntoProcess()
    {
        var env = new Dictionary<string, string> { ["MY_VAR"] = "my-value" };

        var psi = ShellResolver.CreateStartInfo("echo hi", environment: env);

        Assert.Equal("my-value", psi.Environment["MY_VAR"]);
    }

    [Fact]
    public void CreateStartInfo_WorkingDirectory_IsApplied()
    {
        var psi = ShellResolver.CreateStartInfo("echo hi", workingDirectory: "/tmp");

        Assert.Equal("/tmp", psi.WorkingDirectory);
    }

    [Fact]
    public void CreateStartInfo_UnknownShell_Throws()
    {
        Assert.Throws<JobYamlException>(() => ShellResolver.CreateStartInfo("echo hi", shellOverride: "definitely-not-a-shell"));
    }
}
