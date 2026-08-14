using System.Diagnostics;

namespace Sunder.Agent.Execution.Common;

internal static class ChildProcessEnvironment
{
    internal const string WindowsPathExtensions = ".COM;.EXE;.BAT;.CMD";

    internal static void ApplyBase(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var temporaryPath = FirstNonEmpty(
            Environment.GetEnvironmentVariable("TMPDIR"),
            Environment.GetEnvironmentVariable("TMP"),
            Environment.GetEnvironmentVariable("TEMP"),
            Path.GetTempPath());
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userName = Environment.UserName;
        var language = Environment.GetEnvironmentVariable("LANG");
        var locale = Environment.GetEnvironmentVariable("LC_ALL");

        startInfo.Environment.Clear();
        Set(startInfo, "TMPDIR", temporaryPath);
        Set(startInfo, "TMP", temporaryPath);
        Set(startInfo, "TEMP", temporaryPath);

        if (OperatingSystem.IsWindows())
        {
            var systemRoot = ResolveWindowsDirectory();
            Set(startInfo, "SystemRoot", systemRoot);
            Set(startInfo, "WINDIR", systemRoot);
            Set(startInfo, "ComSpec", ResolveWindowsCommandProcessor());
            Set(startInfo, "PATHEXT", WindowsPathExtensions);
            Set(startInfo, "USERPROFILE", userProfile);
            Set(startInfo, "USERNAME", userName);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                Set(startInfo, "HOMEDRIVE", Path.GetPathRoot(userProfile));
                var root = Path.GetPathRoot(userProfile);
                Set(startInfo, "HOMEPATH", root is null ? null : userProfile[root.Length..]);
            }
            Set(
                startInfo,
                "APPDATA",
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            Set(
                startInfo,
                "LOCALAPPDATA",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        else
        {
            Set(startInfo, "HOME", userProfile);
            Set(startInfo, "USER", userName);
            Set(startInfo, "LOGNAME", userName);
            Set(startInfo, "LANG", language ?? "C.UTF-8");
            Set(startInfo, "LC_ALL", locale ?? "C.UTF-8");
        }
    }

    internal static string ResolveWindowsCommandProcessor()
    {
        var systemRoot = ResolveWindowsDirectory();
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "cmd.exe"
            : Path.Combine(systemRoot, "System32", "cmd.exe");
    }

    internal static IReadOnlyList<string> GetWindowsExecutableDirectories()
    {
        var systemRoot = ResolveWindowsDirectory();
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            return [];
        }
        return
        [
            Path.Combine(systemRoot, "System32"),
            Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0"),
        ];
    }

    private static string ResolveWindowsDirectory()
        => FirstNonEmpty(
               Environment.GetFolderPath(Environment.SpecialFolder.Windows),
               Environment.GetEnvironmentVariable("SystemRoot"))
           ?? string.Empty;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static void Set(ProcessStartInfo startInfo, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            startInfo.Environment[name] = value;
        }
    }
}
