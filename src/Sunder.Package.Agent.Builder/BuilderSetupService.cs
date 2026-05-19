namespace Sunder.Package.Agent.Builder;

public sealed class BuilderSetupService
{
    private const int RequiredSdkMajorVersion = 10;
    private const string DotnetDownloadBaseUrl = "https://builds.dotnet.microsoft.com/dotnet";

    public async Task<IReadOnlyList<BuilderPrerequisiteStatus>> CheckAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken = default)
    {
        var statuses = new List<BuilderPrerequisiteStatus>();
        var sdk = await CheckDotnetSdkAsync(execution, cancellationToken);
        statuses.Add(sdk);
        statuses.Add(sdk.IsInstalled
            ? await CheckTemplateAsync(execution, cancellationToken)
            : new BuilderPrerequisiteStatus(
                BuilderPrerequisiteKind.SunderTemplate,
                "Sunder package template",
                false,
                "Install the .NET SDK before installing the Sunder package template."));
        return statuses;
    }

    public async Task<string> InstallDotnetSdkAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken = default)
    {
        if (execution.IsWindows)
        {
            return await InstallDotnetSdkWindowsAsync(execution, cancellationToken);
        }

        return await InstallDotnetSdkPosixAsync(execution, cancellationToken);
    }

    public async Task<string> InstallTemplateAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken = default)
    {
        var result = await execution.RunProcessAsync("dotnet", ["new", "install", "Sunder.Package.Templates"], cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput) ? "Sunder template installation failed." : result.CombinedOutput);
        }

        return "Installed the Sunder package template.";
    }

    private static async Task<BuilderPrerequisiteStatus> CheckDotnetSdkAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await execution.RunProcessAsync("dotnet", ["--list-sdks"], cancellationToken: cancellationToken);
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

    private static async Task<BuilderPrerequisiteStatus> CheckTemplateAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await execution.RunProcessAsync("dotnet", ["new", "list", "sunder-package"], cancellationToken: cancellationToken);
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

    private static async Task<string> InstallDotnetSdkPosixAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken)
    {
        var home = (await execution.RunShellAsync("printf '%s' \"$HOME\"", timeoutSeconds: 30, cancellationToken: cancellationToken)).CombinedOutput.Trim();
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("Could not resolve executor HOME directory.");
        }

        var installDir = execution.CombinePath(home, ".dotnet");
        var toolsDir = execution.CombinePath(installDir, "tools");
        var command = BuildDotnetSdkPosixInstallCommand(
            installDir,
            toolsDir,
            execution.CombinePath(home, ".sunder-builder"),
            execution.CombinePath(home, ".profile"));
        var result = await execution.RunShellAsync(command, timeoutSeconds: 900, cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput) ? "The .NET SDK installer failed." : result.CombinedOutput);
        }

        await execution.AddPathEntryAsync(installDir, cancellationToken);
        await execution.AddPathEntryAsync(toolsDir, cancellationToken);
        return $"Installed the .NET SDK into {installDir}.";
    }

    private static string BuildDotnetSdkPosixInstallCommand(
        string installDir,
        string toolsDir,
        string workDir,
        string profilePath)
        => $$"""
            set -eu
            install_dir={{BuilderWorkspaceExecution.QuotePosix(installDir)}}
            tools_dir={{BuilderWorkspaceExecution.QuotePosix(toolsDir)}}
            work_dir={{BuilderWorkspaceExecution.QuotePosix(workDir)}}
            profile_path={{BuilderWorkspaceExecution.QuotePosix(profilePath)}}
            channel={{BuilderWorkspaceExecution.QuotePosix($"{RequiredSdkMajorVersion}.0")}}
            base_url={{BuilderWorkspaceExecution.QuotePosix(DotnetDownloadBaseUrl)}}
            archive_path="$work_dir/dotnet-sdk.tar.gz"
            version_path="$work_dir/dotnet-sdk.version"

            cleanup() {
                rm -f "$archive_path" "$version_path" >/dev/null 2>&1 || true
            }
            trap cleanup 0 1 2 15

            mkdir -p "$install_dir" "$tools_dir" "$work_dir"

            install_common_dependencies() {
                if command -v apk >/dev/null 2>&1; then
                    apk add --no-cache ca-certificates curl tar gzip >/dev/null 2>&1 || true
                elif command -v apt-get >/dev/null 2>&1; then
                    DEBIAN_FRONTEND=noninteractive apt-get update >/dev/null 2>&1 || true
                    DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl tar gzip >/dev/null 2>&1 || true
                elif command -v dnf >/dev/null 2>&1; then
                    dnf install -y ca-certificates curl tar gzip >/dev/null 2>&1 || true
                elif command -v microdnf >/dev/null 2>&1; then
                    microdnf install -y ca-certificates curl tar gzip >/dev/null 2>&1 || true
                elif command -v yum >/dev/null 2>&1; then
                    yum install -y ca-certificates curl tar gzip >/dev/null 2>&1 || true
                elif command -v zypper >/dev/null 2>&1; then
                    zypper --non-interactive install ca-certificates curl tar gzip >/dev/null 2>&1 || true
                elif command -v pacman >/dev/null 2>&1; then
                    pacman -Sy --noconfirm --needed ca-certificates curl tar gzip >/dev/null 2>&1 || true
                fi

                if command -v update-ca-certificates >/dev/null 2>&1; then
                    update-ca-certificates >/dev/null 2>&1 || true
                fi
            }

            needs_dependencies=0
            if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
                needs_dependencies=1
            fi
            if ! command -v tar >/dev/null 2>&1; then
                needs_dependencies=1
            fi
            if [ ! -d /etc/ssl/certs ] && [ ! -f /etc/ssl/cert.pem ]; then
                needs_dependencies=1
            fi
            if [ "$needs_dependencies" = "1" ]; then
                install_common_dependencies
            fi

            download() {
                if command -v curl >/dev/null 2>&1; then
                    curl -fL "$1" -o "$2"
                elif command -v wget >/dev/null 2>&1; then
                    wget -O "$2" "$1"
                else
                    printf '%s\n' 'curl or wget is required to install .NET SDK.' >&2
                    exit 127
                fi
            }

            if ! command -v tar >/dev/null 2>&1; then
                printf '%s\n' 'tar is required to install .NET SDK.' >&2
                exit 127
            fi

            kernel=$(uname -s 2>/dev/null || printf '%s' unknown)
            machine=$(uname -m 2>/dev/null || printf '%s' unknown)

            case "$machine" in
                x86_64|amd64) arch=x64 ;;
                aarch64|arm64) arch=arm64 ;;
                armv7l|armv6l) arch=arm ;;
                s390x) arch=s390x ;;
                ppc64le) arch=ppc64le ;;
                riscv64) arch=riscv64 ;;
                *)
                    printf '%s\n' "Unsupported CPU architecture for .NET SDK install: $machine" >&2
                    exit 1
                    ;;
            esac

            case "$kernel" in
                Darwin)
                    os=osx
                    ;;
                Linux)
                    if [ -f /etc/alpine-release ]; then
                        os=linux-musl
                    else
                        ldd_output=$(ldd --version 2>&1 || true)
                        case "$ldd_output" in
                            *musl*|*Musl*|*MUSL*) os=linux-musl ;;
                            *) os=linux ;;
                        esac
                    fi
                    ;;
                *)
                    printf '%s\n' "Unsupported operating system for .NET SDK install: $kernel" >&2
                    exit 1
                    ;;
            esac

            rid="$os-$arch"
            case "$rid" in
                osx-arm|osx-s390x|osx-ppc64le|osx-riscv64)
                    printf '%s\n' "Unsupported .NET SDK runtime identifier: $rid" >&2
                    exit 1
                    ;;
            esac

            latest_url="$base_url/Sdk/$channel/latest.version"
            if ! download "$latest_url" "$version_path"; then
                install_common_dependencies
                download "$latest_url" "$version_path" || {
                    printf '%s\n' "Could not download .NET SDK version metadata from $latest_url" >&2
                    exit 1
                }
            fi

            sdk_version=$(tr -d '\r\n\t ' < "$version_path")
            if [ -z "$sdk_version" ]; then
                printf '%s\n' 'Could not resolve the latest .NET SDK version.' >&2
                exit 1
            fi

            archive_url="$base_url/Sdk/$sdk_version/dotnet-sdk-$sdk_version-$rid.tar.gz"
            printf '%s\n' "Downloading .NET SDK $sdk_version for $rid..."
            if ! download "$archive_url" "$archive_path"; then
                install_common_dependencies
                download "$archive_url" "$archive_path" || {
                    printf '%s\n' "Could not download .NET SDK archive from $archive_url" >&2
                    exit 1
                }
            fi

            tar -xzf "$archive_path" -C "$install_dir"
            if [ ! -x "$install_dir/dotnet" ]; then
                chmod +x "$install_dir/dotnet" >/dev/null 2>&1 || true
            fi

            if ! dotnet_output=$("$install_dir/dotnet" --list-sdks 2>&1); then
                printf '%s\n' "$dotnet_output" >&2
                printf '%s\n' 'The .NET SDK archive was extracted but dotnet could not run. The container may be missing native runtime dependencies for this distro.' >&2
                exit 1
            fi

            case "$install_dir$tools_dir" in
                *[!A-Za-z0-9_./:-]*) ;;
                *)
                    if touch "$profile_path" >/dev/null 2>&1; then
                        profile_content=$(cat "$profile_path" 2>/dev/null || true)
                        case "$profile_content" in
                            *"$install_dir"*) ;;
                            *)
                                {
                                    printf '\nexport DOTNET_ROOT=%s\n' "$install_dir"
                                    printf 'export PATH=%s:%s:$PATH\n' "$install_dir" "$tools_dir"
                                } >> "$profile_path" 2>/dev/null || true
                                ;;
                        esac
                    fi
                    ;;
            esac

            printf '%s\n' "$install_dir"
            """;

    private static async Task<string> InstallDotnetSdkWindowsAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken)
    {
        const string command = """
            $ErrorActionPreference = 'Stop'
            $home = [Environment]::GetFolderPath('UserProfile')
            $installDir = Join-Path $home '.dotnet'
            $toolsDir = Join-Path $installDir 'tools'
            $scriptDir = Join-Path $home '.sunder-builder'
            $scriptPath = Join-Path $scriptDir 'dotnet-install.ps1'
            New-Item -ItemType Directory -Force -Path $installDir, $toolsDir, $scriptDir | Out-Null
            Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $scriptPath
            & $scriptPath -Channel '10.0' -InstallDir $installDir
            $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
            $userPathEntries = $userPath -split ';' | Where-Object { $_ }
            if ($userPathEntries -notcontains $installDir -or $userPathEntries -notcontains $toolsDir) {
                $pathEntries = @($installDir, $toolsDir) + $userPathEntries
                [Environment]::SetEnvironmentVariable('PATH', (($pathEntries | Select-Object -Unique) -join ';'), 'User')
            }
            Write-Output $installDir
            """;
        var result = await execution.RunShellAsync(command, timeoutSeconds: 900, cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput) ? "The .NET SDK installer failed." : result.CombinedOutput);
        }

        var installDir = result.CombinedOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            await execution.AddPathEntryAsync(installDir, cancellationToken);
            await execution.AddPathEntryAsync(execution.CombinePath(installDir, "tools"), cancellationToken);
        }

        return string.IsNullOrWhiteSpace(installDir)
            ? "Installed the .NET SDK."
            : $"Installed the .NET SDK into {installDir}.";
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
