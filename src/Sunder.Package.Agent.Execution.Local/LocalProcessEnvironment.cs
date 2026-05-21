using Sunder.Agent.Execution.Common;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalProcessEnvironment
{
    private static readonly string[] MacOsFallbackPathEntries =
    [
        "~/.dotnet",
        "~/.dotnet/tools",
        "/usr/local/share/dotnet",
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        "/usr/sbin",
        "/sbin",
    ];

    private static readonly string[] UnixFallbackPathEntries =
    [
        "~/.dotnet",
        "~/.dotnet/tools",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        "/snap/bin",
    ];

    private static readonly string[] WindowsFallbackPathEntries =
    [
        @"~\.dotnet",
        @"~\.dotnet\tools",
        @"C:\Program Files\dotnet",
    ];

    public static IReadOnlyList<string> BuildEffectivePathEntries(IReadOnlyList<string>? configuredPathEntries)
        => BuildEffectivePathEntries(
            configuredPathEntries,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS());

    internal static IReadOnlyList<string> BuildEffectivePathEntries(
        IReadOnlyList<string>? configuredPathEntries,
        string? inheritedPath,
        string? homeDirectory,
        bool isWindows,
        bool isMacOS)
        => PathEnvironment.BuildPathEntries(
            configuredPathEntries,
            inheritedPath,
            isWindows ? WindowsFallbackPathEntries : isMacOS ? MacOsFallbackPathEntries : UnixFallbackPathEntries,
            homeDirectory,
            isWindows);
}
