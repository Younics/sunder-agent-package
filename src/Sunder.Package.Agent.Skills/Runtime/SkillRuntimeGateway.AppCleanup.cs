#if SUNDER_SKILLS_APP
using Microsoft.Extensions.Logging;

namespace Sunder.Package.Agent.Skills.Runtime;

internal sealed partial class SkillAppRuntimeGateway
{
    private const string TransferCleanupKeyPrefix = "skills.transfer-cleanup.v1.";
    private const int MaximumPendingTransferCleanups = 64;
    private readonly Task _pendingTransferCleanupTask;

    internal Task PendingTransferCleanup => _pendingTransferCleanupTask;

    internal static string TransferCleanupKey(string transferId)
        => TransferCleanupKeyPrefix + transferId;

    private Task RegisterTransferCleanupAsync(string transferId, CancellationToken cancellationToken)
        => _context.Storage.State.SetValueAsync(
            TransferCleanupKey(transferId),
            "pending",
            cancellationToken);

    private async Task CleanupPendingTransfersAsync()
    {
        IReadOnlyList<string> keys;
        try
        {
            keys = await _context.Storage.State.ListKeysAsync(
                TransferCleanupKeyPrefix,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (!SkillRuntimeExceptionPolicy.IsFatal(exception))
        {
            _logger.LogWarning(
                "Skill transfer orphan discovery failed ({ExceptionType}).",
                exception.GetType().Name);
            return;
        }

        foreach (var key in keys.Take(MaximumPendingTransferCleanups))
        {
            var transferId = key[TransferCleanupKeyPrefix.Length..];
            if (!IsTransferId(transferId))
            {
                _logger.LogWarning("A malformed skill transfer cleanup marker was discarded.");
                await DeleteTransferMarkerByKeyAsync(key).ConfigureAwait(false);
                continue;
            }

            if (await CleanupTransferAsync(transferId).ConfigureAwait(false))
            {
                await DeleteTransferMarkerByKeyAsync(key).ConfigureAwait(false);
            }
        }

        if (keys.Count > MaximumPendingTransferCleanups)
        {
            _logger.LogWarning(
                "Skill transfer orphan cleanup reached its bounded activation limit ({CleanupLimit}).",
                MaximumPendingTransferCleanups);
        }
    }

    private async Task<bool> CleanupTransferAsync(string transferId)
    {
        var root = $"skill-import-transfers/{transferId}";
        var succeeded = true;
        for (var index = 0; index < SkillTransferLimits.MaxChunkCount; index++)
        {
            succeeded &= await DeleteUploadedPathAsync($"{root}/chunks/{index:D4}.bin")
                .ConfigureAwait(false);
        }
        succeeded &= await DeleteUploadedPathAsync($"{root}/manifest.json").ConfigureAwait(false);
        var archive = _context.Storage.RoleLocalWorkspace.GetLocalPath($"skill-transfer/{transferId}.zip");
        succeeded &= await DeleteTemporaryArchiveAsync(archive).ConfigureAwait(false);
        return succeeded;
    }

    private async Task<bool> DeleteUploadedPathAsync(string path)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _context.Storage.Files.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (!SkillRuntimeExceptionPolicy.IsFatal(exception))
            {
                if (attempt == 3)
                {
                    _logger.LogWarning(
                        "Skill transfer upload cleanup failed after bounded retries ({ExceptionType}).",
                        exception.GetType().Name);
                    return false;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        return false;
    }

    private async Task<bool> DeleteTemporaryArchiveAsync(string path)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception exception) when (!SkillRuntimeExceptionPolicy.IsFatal(exception))
            {
                if (attempt == 3)
                {
                    _logger.LogWarning(
                        "Skill transfer archive cleanup failed after bounded retries ({ExceptionType}).",
                        exception.GetType().Name);
                    return false;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        return false;
    }

    private Task DeleteTransferMarkerAsync(string transferId)
        => DeleteTransferMarkerByKeyAsync(TransferCleanupKey(transferId));

    private async Task DeleteTransferMarkerByKeyAsync(string key)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _context.Storage.State.DeleteValueAsync(key, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (!SkillRuntimeExceptionPolicy.IsFatal(exception))
            {
                if (attempt == 3)
                {
                    _logger.LogWarning(
                        "Skill transfer cleanup marker removal failed after bounded retries ({ExceptionType}).",
                        exception.GetType().Name);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransferId(string value)
        => value.Length == 32 && value.All(Uri.IsHexDigit);
}
#endif
