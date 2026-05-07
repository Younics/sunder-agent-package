using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentAttachmentService : IAgentAttachmentContentStore, IAgentSessionDataCleaner
{
    public const int MaxAttachmentsPerMessage = 10;
    public const long MaxAttachmentBytes = 25 * 1024 * 1024;
    public const int MaxTextAttachmentCharacters = 512 * 1024;

    private const int SniffByteCount = 4096;
    private readonly string _attachmentRootPath;

    private static readonly IReadOnlyDictionary<string, string> ExtensionMediaTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".aac"] = "audio/aac",
        [".avi"] = "video/x-msvideo",
        [".bmp"] = "image/bmp",
        [".c"] = "text/x-c",
        [".cc"] = "text/x-c++",
        [".conf"] = "text/plain",
        [".cpp"] = "text/x-c++",
        [".cs"] = "text/x-csharp",
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".diff"] = "text/x-diff",
        [".env"] = "text/plain",
        [".gif"] = "image/gif",
        [".go"] = "text/x-go",
        [".h"] = "text/x-c",
        [".html"] = "text/html",
        [".java"] = "text/x-java-source",
        [".jpeg"] = "image/jpeg",
        [".jpg"] = "image/jpeg",
        [".js"] = "text/javascript",
        [".json"] = "application/json",
        [".jsonl"] = "application/x-ndjson",
        [".jsx"] = "text/javascript",
        [".log"] = "text/plain",
        [".m4a"] = "audio/mp4",
        [".md"] = "text/markdown",
        [".mov"] = "video/quicktime",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".mpeg"] = "video/mpeg",
        [".mpg"] = "video/mpeg",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".patch"] = "text/x-diff",
        [".pdf"] = "application/pdf",
        [".php"] = "text/x-php",
        [".png"] = "image/png",
        [".py"] = "text/x-python",
        [".rb"] = "text/x-ruby",
        [".rs"] = "text/x-rust",
        [".sh"] = "text/x-shellscript",
        [".sql"] = "application/sql",
        [".svg"] = "image/svg+xml",
        [".swift"] = "text/x-swift",
        [".toml"] = "application/toml",
        [".ts"] = "text/typescript",
        [".tsx"] = "text/typescript",
        [".txt"] = "text/plain",
        [".wav"] = "audio/wav",
        [".webm"] = "video/webm",
        [".webp"] = "image/webp",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
    };

    public AgentAttachmentService(IPackageContext packageContext)
    {
        _attachmentRootPath = Path.Combine(packageContext.Storage.DataRootPath, "agent-attachments");
        Directory.CreateDirectory(_attachmentRootPath);
    }

    public string CleanerId => "agent.attachments";

    public void DeleteSessionData(Guid sessionId)
    {
        var sessionDirectory = Path.Combine(_attachmentRootPath, sessionId.ToString("N"));
        if (Directory.Exists(sessionDirectory))
        {
            Directory.Delete(sessionDirectory, recursive: true);
        }
    }

    public async Task<AgentAttachmentUploadRequest> LoadUploadRequestFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Attachment path cannot be empty.");
        }

        var fullPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new InvalidOperationException($"Attachment file '{path}' was not found.");
        }

        if (fileInfo.Length > MaxAttachmentBytes)
        {
            throw new InvalidOperationException($"Attachment '{fileInfo.Name}' is too large. The limit is {FormatByteCount(MaxAttachmentBytes)}.");
        }

        var content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return new AgentAttachmentUploadRequest(fileInfo.Name, InferMediaType(fileInfo.Name, content), content);
    }

    public AgentAttachmentInfo InspectUpload(AgentAttachmentUploadRequest upload)
    {
        var fileName = NormalizeFileName(upload.FileName);
        ValidateContentSize(fileName, upload.Content.Length);
        var mediaType = ResolveMediaType(fileName, upload.MediaType, upload.Content);
        var kind = ResolveKind(mediaType, upload.Content);
        var isText = IsTextAttachment(kind);
        var text = isText ? DecodeText(upload.Content) : null;

        return new AgentAttachmentInfo(
            fileName,
            mediaType,
            kind,
            upload.Content.LongLength,
            isText,
            text?.Length > MaxTextAttachmentCharacters);
    }

    public async Task<AgentStoredAttachment> StoreAttachmentAsync(Guid sessionId, AgentAttachmentUploadRequest upload, CancellationToken cancellationToken = default)
    {
        var info = InspectUpload(upload);
        var attachmentId = Guid.NewGuid();
        var sessionDirectory = Path.Combine(_attachmentRootPath, sessionId.ToString("N"));
        Directory.CreateDirectory(sessionDirectory);

        var fileName = $"{attachmentId:N}-{info.FileName}";
        var fullPath = Path.Combine(sessionDirectory, fileName);
        await File.WriteAllBytesAsync(fullPath, upload.Content, cancellationToken).ConfigureAwait(false);

        var textContent = info.IsText ? DecodeText(upload.Content) : null;
        var wasTruncated = false;
        if (textContent is not null && textContent.Length > MaxTextAttachmentCharacters)
        {
            textContent = textContent[..MaxTextAttachmentCharacters];
            wasTruncated = true;
        }

        var relativePath = Path.Combine(sessionId.ToString("N"), fileName).Replace(Path.DirectorySeparatorChar, '/');
        var metadata = new AgentAttachmentMetadata(
            attachmentId,
            info.FileName,
            info.MediaType,
            info.Kind,
            info.SizeBytes,
            Convert.ToHexString(SHA256.HashData(upload.Content)).ToLowerInvariant(),
            relativePath,
            info.IsText,
            wasTruncated || info.WasTruncated);

        return new AgentStoredAttachment(metadata, textContent);
    }

    public async Task<byte[]> ReadAttachmentBytesAsync(AgentAttachmentMetadata metadata, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveStoredAttachmentPath(metadata.StorageRelativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Attachment '{metadata.FileName}' was not found.", fullPath);
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveStoredAttachmentPath(string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_attachmentRootPath, normalizedRelativePath));
        var rootPath = Path.GetFullPath(_attachmentRootPath);
        if (!fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Attachment storage path escaped the attachment root.");
        }

        return fullPath;
    }

    private static void ValidateContentSize(string fileName, int byteCount)
    {
        if (byteCount == 0)
        {
            throw new InvalidOperationException($"Attachment '{fileName}' is empty.");
        }

        if (byteCount > MaxAttachmentBytes)
        {
            throw new InvalidOperationException($"Attachment '{fileName}' is too large. The limit is {FormatByteCount(MaxAttachmentBytes)}.");
        }
    }

    private static string NormalizeFileName(string fileName)
    {
        var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName.Trim());
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(safeName) ? "attachment" : safeName;
    }

    private static string ResolveMediaType(string fileName, string? declaredMediaType, ReadOnlySpan<byte> bytes)
    {
        var inferred = InferMediaType(fileName, bytes);
        if (IsMeaningfulMediaType(inferred))
        {
            return inferred;
        }

        var normalizedDeclared = NormalizeMediaType(declaredMediaType);
        if (IsMeaningfulMediaType(normalizedDeclared))
        {
            return normalizedDeclared;
        }

        return inferred;
    }

    private static string InferMediaType(string fileName, ReadOnlySpan<byte> bytes)
    {
        if (StartsWith(bytes, "%PDF-"u8))
        {
            return "application/pdf";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47
            && bytes[4] == 0x0D
            && bytes[5] == 0x0A
            && bytes[6] == 0x1A
            && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (StartsWith(bytes, "GIF87a"u8) || StartsWith(bytes, "GIF89a"u8))
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 && StartsWith(bytes, "RIFF"u8) && StartsWith(bytes[8..], "WEBP"u8))
        {
            return "image/webp";
        }

        if (bytes.Length >= 12 && StartsWith(bytes, "RIFF"u8) && StartsWith(bytes[8..], "WAVE"u8))
        {
            return "audio/wav";
        }

        if (bytes.Length >= 4 && StartsWith(bytes, "OggS"u8))
        {
            return "audio/ogg";
        }

        if (bytes.Length >= 3 && StartsWith(bytes, "ID3"u8))
        {
            return "audio/mpeg";
        }

        if (bytes.Length >= 12 && StartsWith(bytes[4..], "ftyp"u8))
        {
            return "video/mp4";
        }

        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension) && ExtensionMediaTypes.TryGetValue(extension, out var mediaType))
        {
            return mediaType;
        }

        return LooksLikeText(bytes) ? "text/plain" : "application/octet-stream";
    }

    private static string NormalizeMediaType(string? mediaType)
        => string.IsNullOrWhiteSpace(mediaType)
            ? "application/octet-stream"
            : mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();

    private static bool IsMeaningfulMediaType(string mediaType)
        => !string.IsNullOrWhiteSpace(mediaType)
           && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(mediaType, "binary/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static AgentAttachmentKind ResolveKind(string mediaType, ReadOnlySpan<byte> bytes)
        => mediaType switch
        {
            _ when mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => AgentAttachmentKind.Image,
            _ when mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => AgentAttachmentKind.Audio,
            _ when mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => AgentAttachmentKind.Video,
            "application/pdf" => AgentAttachmentKind.Pdf,
            _ when IsTextMediaType(mediaType) || LooksLikeText(bytes) => AgentAttachmentKind.Text,
            _ => AgentAttachmentKind.Binary,
        };

    private static bool IsTextAttachment(AgentAttachmentKind kind)
        => kind == AgentAttachmentKind.Text;

    private static bool IsTextMediaType(string mediaType)
        => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "application/ld+json", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "application/sql", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "application/toml", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "application/yaml", StringComparison.OrdinalIgnoreCase)
           || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
           || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes[..Math.Min(bytes.Length, SniffByteCount)];
        if (sample.IsEmpty)
        {
            return false;
        }

        var suspicious = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x09 || b is > 0x0D and < 0x20)
            {
                suspicious++;
            }
        }

        return suspicious <= sample.Length / 100;
    }

    private static string DecodeText(byte[] content)
        => Encoding.UTF8.GetString(content);

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
        => value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static string FormatByteCount(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.#} MB"
            : bytes >= 1024
                ? $"{bytes / 1024d:0.#} KB"
                : $"{bytes} B";
}
