using System.Diagnostics;
using System.Text;

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

        ApplyColorDefaults(psi);

        return psi;
    }

    /// <summary>
    /// Children see a redirected (non-TTY) stdout, which makes most color-aware
    /// tools (chalk, npm, gcc, ...) turn colors off. The web console renders
    /// ANSI escapes via xterm.js, so default these on unless the workflow set
    /// its own value for the variable.
    /// </summary>
    private static void ApplyColorDefaults(ProcessStartInfo psi)
    {
        var defaults = new (string Key, string Value)[]
        {
            ("FORCE_COLOR", "1"),
            ("CLICOLOR_FORCE", "1"),
            ("COLORTERM", "truecolor"),
            ("TERM", "xterm-256color"),
        };
        foreach (var (key, value) in defaults)
        {
            if (!psi.Environment.ContainsKey(key))
                psi.Environment[key] = value;
        }
    }

    private static ProcessStartInfo ResolveWindows(string? shell, string command) => shell?.Trim().ToLowerInvariant() switch
    {
        null or "" or "cmd" => Cmd(command),
        "powershell" => PowerShell("powershell.exe", command),
        "pwsh" => PowerShell("pwsh", command),
        _ => throw new WorkflowYamlException($"Unsupported shell '{shell}' on Windows. Supported: cmd, powershell, pwsh."),
    };

    private static ProcessStartInfo ResolveUnix(string? shell, string command) => shell?.Trim().ToLowerInvariant() switch
    {
        null or "" or "sh" => Sh("/bin/sh", command),
        "bash" => Sh("/bin/bash", command),
        "pwsh" => PowerShell("pwsh", command),
        _ => throw new WorkflowYamlException($"Unsupported shell '{shell}' on Unix. Supported: sh, bash, pwsh."),
    };

    private static ProcessStartInfo Cmd(string command)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            // /s strips the outer quotes around the whole command; /d skips AutoRun scripts.
            Arguments = $"/d /s /c \"{command}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        ApplyEncoding(psi);
        return psi;
    }

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

    private static ProcessStartInfo NewRedirected(string fileName)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        ApplyEncoding(psi);
        return psi;
    }

    /// <summary>
    /// Children write in the console's code page on Windows (e.g. GBK); decode with
    /// the same encoding so non-ASCII output survives. On Unix everything is UTF-8.
    /// </summary>
    private static void ApplyEncoding(ProcessStartInfo psi)
    {
        var encoding = Encoding.UTF8;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                encoding = Console.OutputEncoding;
            }
            catch
            {
                // No attached console (service context) — fall back to UTF-8.
            }
        }
        psi.StandardOutputEncoding = encoding;
        psi.StandardErrorEncoding = encoding;
    }
}
