using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderSetupService
{
    private const int RequiredSdkMajorVersion = 10;

    public async Task<IReadOnlyList<BuilderPrerequisiteStatus>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var statuses = new List<BuilderPrerequisiteStatus>();
        var sdk = await CheckDotnetSdkAsync(cancellationToken);
        statuses.Add(sdk);

        if (sdk.IsInstalled)
        {
            statuses.Add(await CheckTemplateAsync(cancellationToken));
        }
        else
        {
            statuses.Add(new BuilderPrerequisiteStatus(
                BuilderPrerequisiteKind.SunderTemplate,
                "Sunder package template",
                false,
                "Install the .NET SDK before installing the Sunder package template."));
        }

        return statuses;
    }

    public async Task<string> InstallDotnetSdkAsync(CancellationToken cancellationToken = default)
    {
        var downloadUri = ResolveDotnetSdkDownloadUri();
        var fileName = Path.GetFileName(downloadUri.LocalPath);
        var destination = Path.Combine(Path.GetTempPath(), "sunder-builder", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var httpClient = new HttpClient();
        await using (var source = await httpClient.GetStreamAsync(downloadUri, cancellationToken))
        await using (var target = File.Create(destination))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
            return "Downloaded and launched the .NET SDK installer. Recheck setup after the installer completes.";
        }

        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open") { ArgumentList = { destination } });
            return "Downloaded and opened the .NET SDK installer. Recheck setup after the installer completes.";
        }

        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        var result = await BuilderProcessRunner.RunAsync(
            "bash",
            [destination, "--channel", RequiredSdkMajorVersion + ".0", "--install-dir", installRoot],
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput) ? "The .NET SDK installer failed." : result.CombinedOutput);
        }

        return $"Installed the .NET SDK into {installRoot}. Restart Sunder if dotnet is still not detected.";
    }

    public async Task<string> InstallTemplateAsync(CancellationToken cancellationToken = default)
    {
        var result = await BuilderDotnetTool.RunAsync(["new", "install", "Sunder.Package.Templates"], cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput) ? "Sunder template installation failed." : result.CombinedOutput);
        }

        return "Installed the Sunder package template.";
    }

    private static async Task<BuilderPrerequisiteStatus> CheckDotnetSdkAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await BuilderDotnetTool.RunAsync(["--list-sdks"], cancellationToken: cancellationToken);
            if (result.ExitCode != 0)
            {
                return new BuilderPrerequisiteStatus(BuilderPrerequisiteKind.DotnetSdk, ".NET SDK", false, result.CombinedOutput);
            }

            var installedVersions = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Select(version => version!)
                .ToArray();
            var hasRequiredSdk = installedVersions.Any(version => int.TryParse(version.Split('.')[0], out var major) && major >= RequiredSdkMajorVersion);
            return hasRequiredSdk
                ? new BuilderPrerequisiteStatus(BuilderPrerequisiteKind.DotnetSdk, ".NET SDK", true, $"Detected SDK(s): {string.Join(", ", installedVersions)}")
                : new BuilderPrerequisiteStatus(BuilderPrerequisiteKind.DotnetSdk, ".NET SDK", false, $"Requires .NET SDK {RequiredSdkMajorVersion}.0 or newer. Detected: {string.Join(", ", installedVersions)}");
        }
        catch (Exception ex)
        {
            return new BuilderPrerequisiteStatus(BuilderPrerequisiteKind.DotnetSdk, ".NET SDK", false, ex.Message);
        }
    }

    private static async Task<BuilderPrerequisiteStatus> CheckTemplateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await BuilderDotnetTool.RunAsync(["new", "list", "sunder-package"], cancellationToken: cancellationToken);
            var installed = result.ExitCode == 0 && result.StandardOutput.Contains("sunder-package", StringComparison.OrdinalIgnoreCase);
            return installed
                ? new BuilderPrerequisiteStatus(BuilderPrerequisiteKind.SunderTemplate, "Sunder package template", true, "dotnet new sunder-package is available.")
                : new BuilderPrerequisiteStatus(BuilderPrerequisiteKind.SunderTemplate, "Sunder package template", false, "The dotnet new sunder-package template is not installed.");
        }
        catch (Exception ex)
        {
            return new BuilderPrerequisiteStatus(BuilderPrerequisiteKind.SunderTemplate, "Sunder package template", false, ex.Message);
        }
    }

    private static Uri ResolveDotnetSdkDownloadUri()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows())
        {
            return new Uri($"https://aka.ms/dotnet/{RequiredSdkMajorVersion}.0/dotnet-sdk-win-{architecture}.exe");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new Uri($"https://aka.ms/dotnet/{RequiredSdkMajorVersion}.0/dotnet-sdk-osx-{architecture}.pkg");
        }

        return new Uri("https://dot.net/v1/dotnet-install.sh");
    }
}

public sealed record BuilderPrerequisiteStatus(
    BuilderPrerequisiteKind Kind,
    string Name,
    bool IsInstalled,
    string Detail);

public enum BuilderPrerequisiteKind
{
    DotnetSdk = 0,
    SunderTemplate = 1,
}
