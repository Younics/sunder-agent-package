using System.Security.Cryptography;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentAttachmentTransferService : IDisposable
{
    private const int MaximumPendingTransfers = 32;
    private static readonly TimeSpan TransferLifetime = TimeSpan.FromMinutes(15);

    private readonly object _syncRoot = new();
    private readonly string _transferRootPath;
    private readonly Dictionary<string, PendingUpload> _uploads = new(StringComparer.Ordinal);
    private bool _disposed;

    public AgentAttachmentTransferService(IPackageContext packageContext)
    {
        _transferRootPath = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("agent/attachment-transfers");
        Directory.CreateDirectory(_transferRootPath);
        foreach (var path in Directory.EnumerateFiles(_transferRootPath, "*.upload"))
        {
            TryDelete(path);
        }
    }

    private AgentAttachmentUploadHandle BeginUpload(AgentAttachmentUploadDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            RemoveExpiredUploads();
            if (_uploads.Count >= MaximumPendingTransfers)
            {
                throw new AgentAttachmentTransferOperationException("Too many attachment uploads are pending.");
            }

            var transferId = Guid.NewGuid().ToString("N");
            var path = Path.Combine(_transferRootPath, transferId + ".upload");
            using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            _uploads.Add(transferId, new PendingUpload(descriptor, path, DateTimeOffset.UtcNow));
            return new AgentAttachmentUploadHandle(transferId);
        }
    }

    internal async ValueTask<AgentAttachmentUploadHandle> ReceiveUploadAsync(
        AgentAttachmentUploadDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var started = BeginUpload(descriptor);
        var transferId = started.TransferId;
        string path;
        lock (_syncRoot)
        {
            path = GetUpload(transferId).Path;
        }

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var received = 0;
            await using (var destination = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: buffer.Length,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    if (received > descriptor.SizeBytes - read)
                    {
                        throw new AgentAttachmentTransferOperationException(
                            "Attachment content exceeds its declared size.");
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (received != descriptor.SizeBytes
                || !string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentAttachmentTransferOperationException(
                    "Attachment content does not match its declared length and digest.");
            }

            lock (_syncRoot)
            {
                var upload = GetUpload(transferId);
                upload.ReceivedBytes = received;
                upload.IsComplete = true;
                upload.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            return new AgentAttachmentUploadHandle(transferId);
        }
        catch
        {
            AbortUpload(transferId);
            throw;
        }
    }

    private void AbortUpload(string transferId)
    {
        lock (_syncRoot)
        {
            if (_uploads.Remove(transferId, out var upload))
            {
                TryDelete(upload.Path);
            }
        }
    }

    internal bool DiscardUpload(AgentAttachmentUploadHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_syncRoot)
        {
            if (!_uploads.Remove(handle.TransferId, out var upload))
            {
                return false;
            }
            TryDelete(upload.Path);
            return true;
        }
    }

    internal IReadOnlyList<AgentAttachmentUploadRequest> ConsumeUploads(
        IReadOnlyList<AgentAttachmentUploadHandle> handles)
    {
        if (handles.Count == 0)
        {
            return [];
        }
        if (handles.Count > AgentAttachmentService.MaxAttachmentsPerMessage)
        {
            throw new AgentAttachmentTransferOperationException(
                $"A message can include at most {AgentAttachmentService.MaxAttachmentsPerMessage} attachments.");
        }

        PendingUpload[] uploads;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (handles.Select(handle => handle.TransferId).Distinct(StringComparer.Ordinal).Count() != handles.Count)
            {
                throw new AgentAttachmentTransferOperationException("An attachment upload handle was repeated.");
            }

            uploads = handles.Select(handle =>
            {
                var upload = GetUpload(handle.TransferId);
                if (!upload.IsComplete)
                {
                    throw new AgentAttachmentTransferOperationException("Attachment upload is not complete.");
                }
                return upload;
            }).ToArray();

            foreach (var handle in handles)
            {
                _uploads.Remove(handle.TransferId);
            }
        }

        try
        {
            return uploads.Select(upload => new AgentAttachmentUploadRequest(
                    upload.Descriptor.FileName,
                    upload.Descriptor.MediaType,
                    File.ReadAllBytes(upload.Path)))
                .ToArray();
        }
        finally
        {
            foreach (var upload in uploads)
            {
                TryDelete(upload.Path);
            }
        }
    }

    internal AgentAttachmentTransferAdoption BeginAdoption(
        IReadOnlyList<AgentAttachmentUploadHandle> handles)
    {
        if (handles.Count == 0)
        {
            return new AgentAttachmentTransferAdoption([], []);
        }
        if (handles.Count > AgentAttachmentService.MaxAttachmentsPerMessage)
        {
            throw new AgentAttachmentTransferOperationException(
                $"A message can include at most {AgentAttachmentService.MaxAttachmentsPerMessage} attachments.");
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            RemoveExpiredUploads();
            if (handles.Select(handle => handle.TransferId).Distinct(StringComparer.Ordinal).Count() != handles.Count)
            {
                throw new AgentAttachmentTransferOperationException("An attachment upload handle was repeated.");
            }

            var uploads = new List<AgentCompletedAttachmentUpload>(handles.Count);
            var leased = new List<PendingUpload>(handles.Count);
            try
            {
                foreach (var handle in handles)
                {
                    var upload = GetUpload(handle.TransferId);
                    if (!upload.IsComplete)
                    {
                        throw new AgentAttachmentTransferOperationException("Attachment upload is not complete.");
                    }
                    if (upload.IsAdopting)
                    {
                        throw new AgentAttachmentTransferOperationException(
                            "Attachment upload is already being adopted.");
                    }

                    upload.IsAdopting = true;
                    leased.Add(upload);
                    uploads.Add(new AgentCompletedAttachmentUpload(
                        handle.TransferId,
                        upload.Descriptor,
                        upload.Path));
                }
            }
            catch
            {
                foreach (var upload in leased)
                {
                    upload.IsAdopting = false;
                }
                throw;
            }

            return new AgentAttachmentTransferAdoption(handles.ToArray(), uploads);
        }
    }

    internal void CompleteAdoption(AgentAttachmentTransferAdoption adoption)
    {
        var paths = new List<string>(adoption.Handles.Count);
        lock (_syncRoot)
        {
            foreach (var handle in adoption.Handles)
            {
                if (_uploads.TryGetValue(handle.TransferId, out var upload)
                    && upload.IsAdopting)
                {
                    _uploads.Remove(handle.TransferId);
                    paths.Add(upload.Path);
                }
            }
        }

        foreach (var path in paths)
        {
            TryDelete(path);
        }
    }

    internal void ReleaseAdoption(AgentAttachmentTransferAdoption adoption)
    {
        lock (_syncRoot)
        {
            foreach (var handle in adoption.Handles)
            {
                if (_uploads.TryGetValue(handle.TransferId, out var upload))
                {
                    upload.IsAdopting = false;
                }
            }
        }
    }

    public void Dispose()
    {
        PendingUpload[] uploads;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            uploads = _uploads.Values.ToArray();
            _uploads.Clear();
        }

        foreach (var upload in uploads)
        {
            TryDelete(upload.Path);
        }
    }

    private PendingUpload GetUpload(string transferId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(transferId)
            || !_uploads.TryGetValue(transferId, out var upload))
        {
            throw new AgentAttachmentTransferOperationException(
                "Attachment upload handle is invalid or expired.");
        }

        if (DateTimeOffset.UtcNow - upload.UpdatedAtUtc > TransferLifetime)
        {
            RemoveUpload(transferId, upload);
            throw new AgentAttachmentTransferOperationException("Attachment upload handle has expired.");
        }

        return upload;
    }

    private void RemoveExpiredUploads()
    {
        var cutoff = DateTimeOffset.UtcNow - TransferLifetime;
        foreach (var entry in _uploads.Where(entry => entry.Value.UpdatedAtUtc < cutoff).ToArray())
        {
            RemoveUpload(entry.Key, entry.Value);
        }
    }

    private void RemoveUpload(string transferId, PendingUpload upload)
    {
        _uploads.Remove(transferId);
        TryDelete(upload.Path);
    }

    private static void ValidateDescriptor(AgentAttachmentUploadDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.FileName) || descriptor.FileName.Length > 1024)
        {
            throw new AgentAttachmentTransferOperationException("Attachment file name is invalid.");
        }
        if (descriptor.MediaType?.Length > 256)
        {
            throw new AgentAttachmentTransferOperationException("Attachment media type is too long.");
        }
        if (descriptor.SizeBytes is <= 0 or > (int)AgentAttachmentService.MaxAttachmentBytes)
        {
            throw new AgentAttachmentTransferOperationException("Attachment size is invalid.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.Sha256)
            || descriptor.Sha256.Length != 64
            || !descriptor.Sha256.All(Uri.IsHexDigit))
        {
            throw new AgentAttachmentTransferOperationException("Attachment content hash is invalid.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A later package startup sweep gets another chance to remove an orphaned transfer.
        }
    }

    private sealed class PendingUpload(
        AgentAttachmentUploadDescriptor descriptor,
        string path,
        DateTimeOffset updatedAtUtc)
    {
        public AgentAttachmentUploadDescriptor Descriptor { get; } = descriptor;
        public string Path { get; } = path;
        public DateTimeOffset UpdatedAtUtc { get; set; } = updatedAtUtc;
        public int ReceivedBytes { get; set; }
        public bool IsComplete { get; set; }
        public bool IsAdopting { get; set; }
    }
}

internal sealed record AgentCompletedAttachmentUpload(
    string TransferId,
    AgentAttachmentUploadDescriptor Descriptor,
    string SourcePath);

internal sealed record AgentAttachmentTransferAdoption(
    IReadOnlyList<AgentAttachmentUploadHandle> Handles,
    IReadOnlyList<AgentCompletedAttachmentUpload> Uploads);

internal sealed class AgentAttachmentTransferOperationException(string message)
    : InvalidOperationException(message);
