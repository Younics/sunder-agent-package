using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentAttachmentService
{
    private readonly SemaphoreSlim _toolImageAdmission = new(1, 1);

    internal async Task<byte[]> ReadToolImageAsync(AgentAttachmentMetadata metadata, CancellationToken token)
    {
        if (metadata.SizeBytes is < 57 or > 2 * 1024 * 1024) throw new InvalidDataException("Invalid stored tool image size.");
        var path = GetAttachmentContentPath(metadata);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var bytes = new byte[(int)metadata.SizeBytes];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new InvalidDataException("Stored tool image grew beyond its original bytes.");
        return bytes;
    }

    internal async Task<AgentStoredAttachment> StoreToolImageAsync(Guid session, Guid call, byte[] png, CancellationToken token)
    {
        await _toolImageAdmission.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var folder = Path.Combine(_attachmentRootPath, session.ToString("N"));
            var files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*-package-view.png", SearchOption.AllDirectories).Take(65).ToArray() : [];
            if (files.Length >= 64 || files.Sum(path => new FileInfo(path).Length) + png.Length > 32 * 1024 * 1024)
                throw new InvalidOperationException("This session's screenshot storage budget is full; saved screenshots are not silently deleted.");
            return await StoreAttachmentAsync(session, call, new("package-view.png", "image/png", png), token).ConfigureAwait(false);
        }
        finally { _toolImageAdmission.Release(); }
    }
}
