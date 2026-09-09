using System.Diagnostics;

namespace InfinityCI.Core;

/// <summary>
/// Builds OS-appropriate process start info for running a shell command,
/// shared by Server (local executors) and Agent (remote executors).
/// </summary>
public static class ShellResolver
{
    public static ProcessStartInfo CreateStartInfo(
        string command,
        string? shellOverride = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = OperatingSystem.IsWindows()
            ? ResolveWindows(shellOverride, command)
            : ResolveUnix(shellOverride, command);

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        return psi;
    }

    private static ProcessStartInfo ResolveWindows(string? shell, string command) => shell?.Trim().ToLowerInvariant() switch
    {
        null or "" or "cmd" => Cmd(command),
        "powershell" => PowerShell("powershell.exe", command),
        "pwsh" => PowerShell("pwsh", command),
        _ => throw new JobYamlException($"Unsupported shell '{shell}' on Windows. Supported: cmd, powershell, pwsh."),
    };

    private static ProcessStartInfo ResolveUnix(string? shell, string command) => shell?.Trim().ToLowerInvariant() switch
    {
        null or "" or "sh" => Sh("/bin/sh", command),
        "bash" => Sh("/bin/bash", command),
        "pwsh" => PowerShell("pwsh", command),
        _ => throw new JobYamlException($"Unsupported shell '{shell}' on Unix. Supported: sh, bash, pwsh."),
    };

    private static ProcessStartInfo Cmd(string command) => new("cmd.exe")
    {
        // /s strips the outer quotes around the whole command; /d skips AutoRun scripts.
        Arguments = $"/d /s /c \"{command}\"",
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static ProcessStartInfo Sh(string fileName, string command)
    {
        var psi = NewRedirected(fileName);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        return psi;
    }

    private static ProcessStartInfo PowerShell(string fileName, string command)
    {
        var psi = NewRedirected(fileName);
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        return psi;
    }

    private static ProcessStartInfo NewRedirected(string fileName) => new(fileName)
    {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
}
