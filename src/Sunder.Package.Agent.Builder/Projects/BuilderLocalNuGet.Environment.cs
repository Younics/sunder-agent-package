namespace Sunder.Package.Agent.Builder;

internal static partial class BuilderLocalNuGet
{
    internal static void ConfigureSdkResolverEnvironment(BuilderProjectDriverOptions options,
        IDictionary<string, string?> environment, string? templateHome)
    {
        if (options.NuGetConfigPath is not { } config) return;
        // The NuGet project SDK resolver runs before MSBuild can apply the
        // RestoreConfigFile property. Give it the same selected settings in an
        // isolated CLI/profile home, without writing into the authored project,
        // an external SDK installation or the actual user's NuGet settings.
        var home = templateHome ?? Path.Combine(options.CacheRootPath, "dotnet-cli");
        environment["DOTNET_CLI_HOME"] = home;
        var settings = OperatingSystem.IsWindows()
            ? Path.Combine(options.CacheRootPath, "nuget-profile", "AppData", "Roaming", "NuGet")
            : Path.Combine(home, ".nuget", "NuGet");
        if (OperatingSystem.IsWindows()) environment["APPDATA"] = Path.GetDirectoryName(settings)!;
        CheckPath(home);
        CheckPath(settings);
        BuilderToolchainFiles.CreatePrivateDirectory(home);
        BuilderToolchainFiles.CreatePrivateDirectory(settings);
        CheckPath(config);
        var expected = ReadSettings(config);
        var destination = Path.Combine(settings, "NuGet.Config");
        CheckPath(destination);
        if (!File.Exists(destination))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = OpenNew(temporary)) { output.Write(expected); output.Flush(flushToDisk: true); }
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination)) { /* Verify a concurrent exact writer; never replace it. */ }
            }
            finally { File.Delete(temporary); }
        }
        CheckPath(destination);
        if (!ReadSettings(destination).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Private SDK resolver settings differ from the selected local NuGet feed. Existing settings were preserved.");
    }

    private static byte[] ReadSettings(string path)
    {
        using var input = OpenRead(path);
        if (input.Length is <= 0 or > MaximumStateBytes) throw new InvalidDataException("Private NuGet settings exceed their metadata bound.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new IOException("Private NuGet settings changed during selection.");
        return bytes;
    }
}
