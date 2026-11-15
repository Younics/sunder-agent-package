using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

internal static partial class HostFileSystemExecutor
{
    // The public entry point owns the availability guard. Direct native tests
    // exercise this same implementation below it, not a different strategy.
    internal static ValueTask<AgentFileMutationResult> WriteFileCoreAsync(
        HostFileSystemPathContext pathContext,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken,
        ILocalFileWriteFaultInjector? faultInjector = null,
        ILocalSecureFileSystemHooks? hooks = null)
        => ValueTask.FromResult(WriteFileCore(pathContext, request, cancellationToken, faultInjector, hooks));

    private static AgentFileMutationResult WriteFileCore(
        HostFileSystemPathContext pathContext,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken,
        ILocalFileWriteFaultInjector? faultInjector,
        ILocalSecureFileSystemHooks? hooks)
    {
        var path = pathContext.ReportedPath;
        using var approvedAuthority = pathContext.ApprovedAuthority;
        LocalSecurePathSession? session = null;
        LocalSecureHandle? writable = null;
        IDisposable? coordination = null;
        var mutationStarted = false;
        var created = false;
        var contentsWritten = false;
        var durable = false;
        var receiptAttempted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Encode before opening/creating parents. Malformed replacement text
            // cannot fail halfway through an otherwise valid user-file edit.
            var encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
            var bytes = encoding.GetBytes(request.Content);
            session = HostSecurePathEngine.OpenAuthorized(pathContext.ConfiguredRoots, pathContext.HostPath,
                approvedAuthority, createParents: true, hooks, cancellationToken);
            coordination = HostMutationCoordinator.Enter(session.Parent.Identity, cancellationToken);
            using var target = session.TryOpenTarget();
            if (target is not null)
            {
                if (target.Handle.Kind != LocalSecureNodeKind.RegularFile)
                    return FileOperation.Failure(path, "The write target is not a regular file.", AgentFileReadErrorCodes.NotAFile);
                if (!request.Overwrite)
                    return FileOperation.Failure(path, "File already exists.", FileOperation.FileExistsErrorCode);
                writable = session.OpenFileForContentUpdate(target.Handle);
                if (!request.Content.StartsWith('\uFEFF') && HasUtf8Bom(writable))
                    bytes = encoding.GetBytes("\uFEFF" + request.Content);
            }
            else if (request.ExpectedContentHash is not null)
            {
                return FileOperation.ContentChanged(path);
            }

            faultInjector?.OnFaultPoint(LocalFileWriteFaultPoint.ContentPrepared);
            session.Checkpoint(LocalSecureFileSystemCheckpoint.BeforeContentWrite);
            cancellationToken.ThrowIfCancellationRequested();
            if (writable is not null)
            {
                session.ValidateContentTarget(writable);
                if (!ExpectedContentMatches(writable, request.ExpectedContentHash, cancellationToken))
                    return FileOperation.ContentChanged(path);
                cancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                // Prove namespace durability access before creation. Existing-file
                // edits do not need parent write/flush access or quarantine slots.
                session.FlushCreatedFileParent();
                cancellationToken.ThrowIfCancellationRequested();
                writable = session.CreateUserFile();
                created = true;
                mutationStarted = true; // Even an empty creation is a side effect.
            }

            // Do not pass cancellation into this finite commit section. Once a
            // write starts, finish its byte/EOF/flush sequence or report partial
            // state. This cannot protect against process death or I/O failures.
            const int chunkSize = 64 * 1024;
            for (var offset = 0; offset < bytes.Length;)
            {
                mutationStarted = true; // A failing native write may write bytes.
                var count = Math.Min(chunkSize, bytes.Length - offset);
                RandomAccess.Write(writable.Handle, bytes.AsSpan(offset, count), offset);
                session.Checkpoint(LocalSecureFileSystemCheckpoint.AfterContentWriteChunk);
                offset += count;
            }
            session.Checkpoint(LocalSecureFileSystemCheckpoint.BeforeContentLength);
            mutationStarted = true;
            if (RandomAccess.GetLength(writable.Handle) != bytes.Length)
                RandomAccess.SetLength(writable.Handle, bytes.Length);
            contentsWritten = true;
            session.Checkpoint(LocalSecureFileSystemCheckpoint.AfterContentLength);
            session.FlushContent(writable);
            session.Checkpoint(LocalSecureFileSystemCheckpoint.AfterContentFileSync);
            if (created)
            {
                session.FlushCreatedFileParent();
                session.Checkpoint(LocalSecureFileSystemCheckpoint.AfterCreatedFileParentSync);
            }
            durable = true;
            CaptureContentAuthority();
            return FileOperation.Written(path, request.Content.Length);
        }
        catch (Exception exception) when (mutationStarted || exception is LocalSecureIncompleteMutationException)
        {
            // Never sanitize, delete, or restore an existing user's file on an
            // incomplete in-place update. Its identity/security object survives;
            // its contents may not. A receipt is observation authority, not proof
            // that the intended next content state was reached.
            if (!receiptAttempted && writable is not null)
            {
                try { CaptureContentAuthority(); }
                catch (Exception) { /* Preserve uncertainty if receipt capture also fails. */ }
            }
            return FileOperation.Failure(path,
                durable
                    ? "File contents were written and flushed, but post-mutation authority could not be confirmed. Inspect the file before retrying."
                    : contentsWritten
                        ? "File contents were written, but completion or durability could not be confirmed. The file may remain modified; inspect it before retrying."
                        : "File mutation did not complete. The file may remain empty or partially modified; inspect it before retrying.",
                FileOperation.StrictMutationRecoveryRequiredErrorCode);
        }
        catch (LocalSecureFileAlreadyExistsException)
        {
            return FileOperation.Failure(path, "File already exists.", FileOperation.FileExistsErrorCode);
        }
        catch (EncoderFallbackException)
        {
            return FileOperation.Failure(path, "Replacement content is not valid UTF-8 text. No file content was changed.", AgentFileReadErrorCodes.BinaryFile);
        }
        catch (DecoderFallbackException)
        {
            return FileOperation.Failure(path, "The existing file is not valid UTF-8 text. No file content was changed.", AgentFileReadErrorCodes.BinaryFile);
        }
        catch (LocalSecureApprovalChangedException exception)
        {
            return request.ExpectedContentHash is not null
                ? FileOperation.ContentChanged(path)
                : FileOperation.Failure(path, exception.Message, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
        catch (InvalidOperationException exception)
        {
            return FileOperation.Failure(path, exception.Message, AgentFileReadErrorCodes.OutsideConfiguredScope);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return FileOperation.Failure(path, exception.Message, AgentFileReadErrorCodes.PathCanonicalizationFailed);
        }
        finally
        {
            try { writable?.Dispose(); }
            finally
            {
                // Releasing an operation's directory lifetime can itself enter
                // mutation gates for exact-owned empty-directory cleanup.
                try { coordination?.Dispose(); }
                finally { session?.Dispose(); }
            }
        }

        void CaptureContentAuthority()
        {
            receiptAttempted = true;
            if (pathContext.PostMutationAuthoritySink is not { } sink) return;
            session!.Checkpoint(LocalSecureFileSystemCheckpoint.BeforeContentReceipt);
            var retained = session.RetainContentTarget(writable!);
            LocalSecureApprovalLease? authority = session.TakePostMutationAuthority(
                retained, retained.Identity, LocalSecureNodeKind.RegularFile);
            try
            {
                // The receipt owns only retained observation/approval authority.
                // Do not invoke its consumer while holding a write reservation
                // or a coordinator which its release/next operation could need.
                writable!.Dispose();
                writable = null;
                coordination?.Dispose();
                coordination = null;
                sink(authority);
                authority = null;
            }
            finally { authority?.Dispose(); }
        }
    }
}
