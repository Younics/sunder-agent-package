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

    internal AgentAttachmentTransferResult BeginUpload(AgentAttachmentUploadDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            RemoveExpiredUploads();
            if (_uploads.Count >= MaximumPendingTransfers)
            {
                throw new InvalidOperationException("Too many attachment uploads are pending.");
            }

            var transferId = Guid.NewGuid().ToString("N");
            var path = Path.Combine(_transferRootPath, transferId + ".upload");
            using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            _uploads.Add(transferId, new PendingUpload(descriptor, path, DateTimeOffset.UtcNow));
            return new AgentAttachmentTransferResult(
                transferId,
                TotalBytes: descriptor.SizeBytes);
        }
    }

    internal AgentAttachmentTransferResult WriteUploadChunk(
        string transferId,
        int offset,
        byte[] content)
    {
        if (content.Length is <= 0 or > AgentRuntimePayloadLimits.AttachmentUploadChunkBytes)
        {
            throw new InvalidOperationException("Attachment upload chunk size is invalid.");
        }

        lock (_syncRoot)
        {
            var upload = GetUpload(transferId);
            if (upload.IsComplete || offset != upload.ReceivedBytes)
            {
                throw new InvalidOperationException("Attachment upload chunks must be written once and in order.");
            }

            if ((long)offset + content.Length > upload.Descriptor.SizeBytes)
            {
                throw new InvalidOperationException("Attachment upload exceeds its declared size.");
            }

            using (var stream = new FileStream(
                       upload.Path,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None))
            {
                if (stream.Length != offset)
                {
                    throw new InvalidOperationException("Attachment upload storage length is inconsistent.");
                }
                stream.Position = offset;
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            upload.ReceivedBytes += content.Length;
            upload.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return new AgentAttachmentTransferResult(
                transferId,
                upload.ReceivedBytes,
                upload.Descriptor.SizeBytes,
                IsComplete: false);
        }
    }

    internal AgentAttachmentTransferResult CompleteUpload(string transferId)
    {
        lock (_syncRoot)
        {
            var upload = GetUpload(transferId);
            if (upload.ReceivedBytes != upload.Descriptor.SizeBytes)
            {
                throw new InvalidOperationException("Attachment upload is incomplete.");
            }

            using var stream = File.OpenRead(upload.Path);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, upload.Descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                RemoveUpload(transferId, upload);
                throw new InvalidDataException("Attachment upload content hash does not match its descriptor.");
            }

            upload.IsComplete = true;
            upload.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return new AgentAttachmentTransferResult(
                transferId,
                upload.ReceivedBytes,
                upload.Descriptor.SizeBytes,
                IsComplete: true);
        }
    }

    internal void AbortUpload(string transferId)
    {
        lock (_syncRoot)
        {
            if (_uploads.Remove(transferId, out var upload))
            {
                TryDelete(upload.Path);
            }
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
            throw new InvalidOperationException(
                $"A message can include at most {AgentAttachmentService.MaxAttachmentsPerMessage} attachments.");
        }

        PendingUpload[] uploads;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (handles.Select(handle => handle.TransferId).Distinct(StringComparer.Ordinal).Count() != handles.Count)
            {
                throw new InvalidOperationException("An attachment upload handle was repeated.");
            }

            uploads = handles.Select(handle =>
            {
                var upload = GetUpload(handle.TransferId);
                if (!upload.IsComplete)
                {
                    throw new InvalidOperationException("Attachment upload is not complete.");
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
            throw new InvalidOperationException("Attachment upload handle is invalid or expired.");
        }

        if (DateTimeOffset.UtcNow - upload.UpdatedAtUtc > TransferLifetime)
        {
            RemoveUpload(transferId, upload);
            throw new InvalidOperationException("Attachment upload handle has expired.");
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
            throw new InvalidOperationException("Attachment file name is invalid.");
        }
        if (descriptor.MediaType?.Length > 256)
        {
            throw new InvalidOperationException("Attachment media type is too long.");
        }
        if (descriptor.SizeBytes is <= 0 or > (int)AgentAttachmentService.MaxAttachmentBytes)
        {
            throw new InvalidOperationException("Attachment size is invalid.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.Sha256)
            || descriptor.Sha256.Length != 64
            || !descriptor.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Attachment content hash is invalid.");
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
    }
}
