using Microsoft.Extensions.Logging;
using System.Text.Json;
using Sunder.Package.Agent.Shared.Importing;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.Runtime;

#if SUNDER_SKILLS_WORKER
internal sealed partial class SkillRuntimeHandler
{
    private const int MaximumOrphanedTransferCleanups = 64;

    private static async Task CleanupOrphanedTransferStagingAsync(IPackageContext packageContext)
    {
        var root = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("skill-transfer");
        if (!Directory.Exists(root))
        {
            return;
        }

        var logger = packageContext.Logging.LoggerFactory.CreateLogger<SkillRuntimeHandler>();
        string[] candidates;
        try
        {
            BoundedImportIO.ValidateDirectoryPath(root);
            candidates = Directory.EnumerateFileSystemEntries(root)
                .Take(MaximumOrphanedTransferCleanups + 1)
                .ToArray();
        }
        catch (Exception exception) when (!SkillRuntimeExceptionPolicy.IsFatal(exception))
        {
            logger.LogWarning(
                "Skill transfer staging orphan discovery failed ({ExceptionType}).",
                exception.GetType().Name);
            return;
        }

        foreach (var path in candidates.Take(MaximumOrphanedTransferCleanups))
        {
            var name = Path.GetFileName(path);
            var isDirectory = Directory.Exists(path);
            var transferId = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
            if (transferId.Length != 32
                || transferId.Any(character => !Uri.IsHexDigit(character))
                || !isDirectory && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await DeleteOrphanedTransferPathAsync(path, isDirectory, logger).ConfigureAwait(false);
        }

        if (candidates.Length > MaximumOrphanedTransferCleanups)
        {
            logger.LogWarning(
                "Skill transfer staging orphan cleanup reached its bounded activation limit ({CleanupLimit}).",
                MaximumOrphanedTransferCleanups);
        }
    }

    private static async Task DeleteOrphanedTransferPathAsync(
        string path,
        bool isDirectory,
        ILogger logger)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (isDirectory)
                {
                    BoundedImportIO.ValidateDirectoryPath(path);
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
                return;
            }
            catch (Exception exception) when (!SkillRuntimeExceptionPolicy.IsFatal(exception))
            {
                if (attempt == 3)
                {
                    logger.LogWarning(
                        "Skill transfer staging orphan cleanup failed after bounded retries ({ExceptionType}).",
                        exception.GetType().Name);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static SkillTransferManifest ParseTransferManifest(byte[] bytes)
    {
        if (bytes.Length > SkillTransferLimits.MaxManifestBytes)
        {
            throw new SkillOwnerOperationException("The skill transfer manifest exceeds its bounded limit.");
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<SkillTransferManifest>(bytes)
                ?? throw new SkillOwnerOperationException("The skill transfer manifest is invalid.");
            if (manifest.ChunkCount is < 1 or > SkillTransferLimits.MaxChunkCount
                || manifest.TotalBytes is < 1 or > SkillTransferLimits.MaxArchiveBytes
                || manifest.Sha256 is not { Length: 64 } hash
                || hash.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new SkillOwnerOperationException("The skill transfer exceeds its bounded limits.");
            }
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new SkillOwnerOperationException("The skill transfer manifest is invalid.", exception);
        }
    }

    private async Task DeleteTransferUploadsAsync(string root)
    {
        for (var index = 0; index < SkillTransferLimits.MaxChunkCount; index++)
        {
            await DeleteStoredTransferPathAsync($"{root}/chunks/{index:D4}.bin").ConfigureAwait(false);
        }
        await DeleteStoredTransferPathAsync($"{root}/manifest.json").ConfigureAwait(false);
    }

    private async Task DeleteStoredTransferPathAsync(string path)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await context.Storage.Files.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (!SkillRuntimeExceptionPolicy.IsFatal(exception))
            {
                if (attempt == 3)
                {
                    _logger.LogWarning(
                        "Skill transfer upload cleanup failed after bounded retries ({ExceptionType}).",
                        exception.GetType().Name);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task DeleteTransferPathAsync(string path, bool isDirectory)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (isDirectory)
                {
                    if (Directory.Exists(path))
                    {
                        BoundedImportIO.ValidateDirectoryPath(path);
                        Directory.Delete(path, recursive: true);
                    }
                }
                else
                {
                    File.Delete(path);
                }
                return;
            }
            catch (Exception exception) when (!SkillRuntimeExceptionPolicy.IsFatal(exception))
            {
                if (attempt == 3)
                {
                    _logger.LogWarning(
                        "Skill transfer staging cleanup failed after bounded retries ({ExceptionType}).",
                        exception.GetType().Name);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }
}
#endif
