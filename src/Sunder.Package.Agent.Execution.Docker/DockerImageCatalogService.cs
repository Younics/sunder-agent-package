using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerImageCatalogService(IPackageContext packageContext)
{
    private const string ImagesKey = "docker.images:v1";
    private const string InitializedKey = "docker.images.initialized";
    private const int ImageCheckTimeoutSeconds = 30;
    private const int ImagePullTimeoutSeconds = 1800;
    private const int MaxOutputLength = 51200;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    internal static Func<IReadOnlyList<string>, int, CancellationToken, IProgress<string>?, Task<DockerImageProcessResult>>? RunDockerOverride { get; set; }

    public IReadOnlyList<DockerImageDefinition> ListImages()
    {
        var state = LoadState();
        SaveImages(state.Images);
        return state.Images;
    }

    public string? GetDefaultImageReference()
        => ListImages().FirstOrDefault()?.ImageReference;

    public bool ContainsImage(string? imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return false;
        }

        var normalized = NormalizeImageReference(imageReference);
        return ListImages().Any(image => string.Equals(image.ImageReference, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public DockerImageDefinition AddImage(string imageReference)
    {
        var normalized = NormalizeImageReference(imageReference);
        var images = ListImages().ToList();
        var existing = images.FirstOrDefault(image => string.Equals(image.ImageReference, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var image = new DockerImageDefinition(normalized, DockerImageStatus.NotPulled, null, "Image has not been pulled yet.");
        images.Add(image);
        SaveImages(images);
        return image;
    }

    public void DeleteImage(string imageReference)
    {
        var normalized = NormalizeImageReference(imageReference);
        SaveImages(ListImages()
            .Where(image => !string.Equals(image.ImageReference, normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray());
    }

    public async Task<DockerImageReadiness> GetReadinessAsync(
        string? imageReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return new DockerImageReadiness(false, "Configure a Docker image before using Docker execution.", null);
        }

        var normalized = NormalizeImageReference(imageReference);
        var image = ListImages().FirstOrDefault(candidate => string.Equals(candidate.ImageReference, normalized, StringComparison.OrdinalIgnoreCase));
        if (image is null)
        {
            return new DockerImageReadiness(false, $"Docker image '{normalized}' is not configured. Add it in Docker Execution settings before using this workspace.", null);
        }

        var inspect = await RunDockerAsync(["image", "inspect", normalized], ImageCheckTimeoutSeconds, cancellationToken, progress: null).ConfigureAwait(false);
        if (inspect.ExitCode == 0)
        {
            var ready = image with { Status = DockerImageStatus.Ready, LastMessage = "Image is ready." };
            UpdateImage(ready);
            return new DockerImageReadiness(true, $"Docker image '{normalized}' is ready.", ready);
        }

        return new DockerImageReadiness(
            false,
            AppendOutput($"Docker image '{normalized}' is not ready. Pull it in Docker Execution settings before using this workspace.", inspect.Output),
            image);
    }

    public async Task<DockerImageDefinition> RefreshImageAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        var image = AddImage(imageReference);
        var inspect = await RunDockerAsync(["image", "inspect", image.ImageReference], ImageCheckTimeoutSeconds, cancellationToken, progress: null).ConfigureAwait(false);
        var updated = inspect.ExitCode == 0
            ? image with { Status = DockerImageStatus.Ready, LastMessage = "Image is ready." }
            : image with { Status = DockerImageStatus.NotPulled, LastMessage = AppendOutput("Image is not available locally.", inspect.Output) };
        UpdateImage(updated);
        return updated;
    }

    public async Task<IReadOnlyList<DockerImageDefinition>> RefreshImagesAsync(CancellationToken cancellationToken = default)
    {
        var refreshed = new List<DockerImageDefinition>();
        foreach (var image in ListImages())
        {
            refreshed.Add(await RefreshImageAsync(image.ImageReference, cancellationToken).ConfigureAwait(false));
        }

        return refreshed;
    }

    public async Task<DockerImagePullResult> PullImageAsync(
        string imageReference,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var image = AddImage(imageReference);
        UpdateImage(image with { Status = DockerImageStatus.Pulling, LastMessage = "Pulling image..." });

        var pull = await RunDockerAsync(["pull", image.ImageReference], ImagePullTimeoutSeconds, cancellationToken, progress).ConfigureAwait(false);
        if (pull.ExitCode != 0)
        {
            var failed = image with { Status = DockerImageStatus.Failed, LastMessage = AppendOutput("Docker image pull failed.", pull.Output) };
            UpdateImage(failed);
            return new DockerImagePullResult(false, failed.LastMessage ?? "Docker image pull failed.", failed);
        }

        var inspect = await RunDockerAsync(["image", "inspect", image.ImageReference], ImageCheckTimeoutSeconds, cancellationToken, progress: null).ConfigureAwait(false);
        if (inspect.ExitCode == 0)
        {
            var ready = image with
            {
                Status = DockerImageStatus.Ready,
                LastPulledAtUtc = DateTimeOffset.UtcNow,
                LastMessage = "Image is ready."
            };
            UpdateImage(ready);
            return new DockerImagePullResult(true, $"Docker image '{image.ImageReference}' is ready.", ready);
        }

        var unavailable = image with { Status = DockerImageStatus.Failed, LastMessage = AppendOutput("Docker image was pulled but could not be inspected.", inspect.Output) };
        UpdateImage(unavailable);
        return new DockerImagePullResult(false, unavailable.LastMessage ?? "Docker image could not be inspected.", unavailable);
    }

    public void SaveImages(IEnumerable<DockerImageDefinition> images)
    {
        var normalized = images
            .Where(image => !string.IsNullOrWhiteSpace(image.ImageReference))
            .Select(image => image with
            {
                ImageReference = NormalizeImageReference(image.ImageReference),
                Status = image.Status == DockerImageStatus.Pulling ? DockerImageStatus.NotPulled : image.Status,
            })
            .GroupBy(image => image.ImageReference, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(image => image.ImageReference, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        packageContext.Storage.State.SetValueAsync(ImagesKey, JsonSerializer.Serialize(new DockerImageCatalogState(1, normalized), JsonOptions)).GetAwaiter().GetResult();
        packageContext.Storage.State.SetValueAsync(InitializedKey, bool.TrueString).GetAwaiter().GetResult();
    }

    public static string NormalizeImageReference(string imageReference)
    {
        var normalized = imageReference.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Docker image reference cannot be empty.");
        }

        if (normalized.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Docker image reference cannot contain whitespace.");
        }

        return normalized;
    }

    private DockerImageCatalogState LoadState()
    {
        var initialized = bool.TryParse(packageContext.Storage.State.GetValue(InitializedKey), out var parsedInitialized) && parsedInitialized;
        var json = packageContext.Storage.State.GetValue(ImagesKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return initialized
                ? new DockerImageCatalogState(1, [])
                : new DockerImageCatalogState(1,
                [
                    new DockerImageDefinition(
                        DockerExecutionWorkspaceConfigService.DefaultImageReference,
                        DockerImageStatus.NotPulled,
                        null,
                        "Default image has not been pulled yet."),
                ]);
        }

        try
        {
            var state = JsonSerializer.Deserialize<DockerImageCatalogState>(json, JsonOptions) ?? new DockerImageCatalogState(1, []);
            var images = state.Images
                .Where(image => !string.IsNullOrWhiteSpace(image.ImageReference))
                .Select(image => image with
                {
                    ImageReference = NormalizeImageReference(image.ImageReference),
                    Status = image.Status == DockerImageStatus.Pulling ? DockerImageStatus.NotPulled : image.Status,
                })
                .GroupBy(image => image.ImageReference, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(image => image.ImageReference, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new DockerImageCatalogState(1, images);
        }
        catch
        {
            return initialized
                ? new DockerImageCatalogState(1, [])
                : new DockerImageCatalogState(1,
                [
                    new DockerImageDefinition(
                        DockerExecutionWorkspaceConfigService.DefaultImageReference,
                        DockerImageStatus.NotPulled,
                        null,
                        "Default image has not been pulled yet."),
                ]);
        }
    }

    private void UpdateImage(DockerImageDefinition image)
    {
        var images = ListImages().ToList();
        var index = images.FindIndex(candidate => string.Equals(candidate.ImageReference, image.ImageReference, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            images[index] = image;
        }
        else
        {
            images.Add(image);
        }

        SaveImages(images);
    }

    private static async Task<DockerImageProcessResult> RunDockerAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (RunDockerOverride is { } overrideRunner)
        {
            return await overrideRunner(args, timeoutSeconds, cancellationToken, progress).ConfigureAwait(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new DockerImageProcessResult(127, $"Failed to start Docker CLI: {ex.Message}", TimedOut: false, WasTruncated: false);
        }

        var stdoutTask = ReadWithProgressAsync(process.StandardOutput, progress, cancellationToken);
        var stderrTask = ReadWithProgressAsync(process.StandardError, progress, cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new DockerImageProcessResult(124, $"Docker command timed out after {timeoutSeconds} seconds.", TimedOut: true, WasTruncated: false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var output = string.Concat(stdout.Content, stderr.Content);
        var truncatedOutput = TruncateOutput(output, out var wasTruncated);
        return new DockerImageProcessResult(process.ExitCode, truncatedOutput, TimedOut: false, stdout.WasTruncated || stderr.WasTruncated || wasTruncated);
    }

    private static async Task<BoundedProcessOutput> ReadWithProgressAsync(
        StreamReader reader,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder(capacity: buffer.Length);
        var wasTruncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var chunk = new string(buffer, 0, read);
            var progressLine = NormalizeProgressChunk(chunk);
            if (!string.IsNullOrWhiteSpace(progressLine))
            {
                progress?.Report(progressLine);
            }

            var remaining = MaxOutputLength - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            if (read > remaining)
            {
                wasTruncated = true;
            }
        }

        return new BoundedProcessOutput(builder.ToString(), wasTruncated);
    }

    private static string NormalizeProgressChunk(string chunk)
        => chunk.Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;

    private static string TruncateOutput(string output, out bool wasTruncated)
    {
        wasTruncated = output.Length > MaxOutputLength;
        return wasTruncated
            ? output[..MaxOutputLength] + Environment.NewLine + "[output truncated]"
            : output;
    }

    private static string AppendOutput(string message, string? output)
    {
        var trimmed = output?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? message
            : $"{message} {trimmed}";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed record DockerImageCatalogState(int Version, IReadOnlyList<DockerImageDefinition> Images);

    internal sealed record DockerImageProcessResult(int ExitCode, string Output, bool TimedOut, bool WasTruncated);

    private sealed record BoundedProcessOutput(string Content, bool WasTruncated);
}

public sealed record DockerImageDefinition(
    string ImageReference,
    DockerImageStatus Status,
    DateTimeOffset? LastPulledAtUtc,
    string? LastMessage);

public sealed record DockerImageReadiness(bool IsReady, string Message, DockerImageDefinition? Image);

public sealed record DockerImagePullResult(bool Success, string Message, DockerImageDefinition Image);

public enum DockerImageStatus
{
    NotPulled = 0,
    Ready = 1,
    Pulling = 2,
    Failed = 3,
}
