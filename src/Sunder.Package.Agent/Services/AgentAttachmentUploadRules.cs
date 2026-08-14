using System.Globalization;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

internal static class AgentAttachmentUploadRules
{
    internal const int MaxAttachmentsPerMessage = 10;
    internal const long MaxAttachmentBytes = 25 * 1024 * 1024;
    internal const int MaxTextAttachmentCharacters = 512 * 1024;

    private const int SniffByteCount = 4096;
    private const int MaxDisplayFileNameCharacters = 180;

    private static readonly IReadOnlyDictionary<string, string> ExtensionMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

    internal static async Task<AgentAttachmentUploadRequest> LoadFromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
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
            throw new InvalidOperationException(
                $"Attachment '{fileInfo.Name}' is too large. The limit is {FormatByteCount(MaxAttachmentBytes)}.");
        }

        var content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return new AgentAttachmentUploadRequest(fileInfo.Name, InferMediaType(fileInfo.Name, content), content);
    }

    internal static AgentAttachmentInfo Inspect(AgentAttachmentUploadRequest upload)
    {
        var fileName = NormalizeFileName(upload.FileName);
        ValidateContentSize(fileName, upload.Content.Length);
        var mediaType = ResolveMediaType(fileName, upload.MediaType, upload.Content);
        var kind = ResolveKind(mediaType, upload.Content);
        var isText = kind == AgentAttachmentKind.Text;
        var text = isText ? DecodeText(upload.Content) : null;

        return new AgentAttachmentInfo(
            fileName,
            mediaType,
            kind,
            upload.Content.LongLength,
            isText,
            text?.Length > MaxTextAttachmentCharacters);
    }

    internal static string NormalizeDisplayFileName(string? fileName)
        => NormalizeFileName(fileName ?? "attachment");

    internal static string NormalizeDisplayMediaType(string? mediaType)
        => NormalizeMediaType(mediaType);

    internal static string DecodeText(byte[] content)
        => Encoding.UTF8.GetString(content);

    private static void ValidateContentSize(string fileName, int byteCount)
    {
        if (byteCount == 0)
        {
            throw new InvalidOperationException($"Attachment '{fileName}' is empty.");
        }

        if (byteCount > MaxAttachmentBytes)
        {
            throw new InvalidOperationException(
                $"Attachment '{fileName}' is too large. The limit is {FormatByteCount(MaxAttachmentBytes)}.");
        }
    }

    private static string NormalizeFileName(string fileName)
    {
        var rawName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName.Trim();
        var safeName = Path.GetFileName(rawName.Replace('\\', '/'));
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(safeName.Length);
        foreach (var rune in safeName.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var isInvalidFileNameCharacter = rune.Value <= char.MaxValue
                                             && invalidCharacters.Contains((char)rune.Value);
            builder.Append(category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate
                || isInvalidFileNameCharacter
                    ? '_'
                    : rune.ToString());
        }

        safeName = builder.ToString();
        return string.IsNullOrWhiteSpace(safeName)
            ? "attachment"
            : TruncateDisplayFileName(safeName);
    }

    private static string TruncateDisplayFileName(string fileName)
    {
        var runes = fileName.EnumerateRunes().ToArray();
        if (runes.Length <= MaxDisplayFileNameCharacters)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        var extensionRunes = extension.EnumerateRunes().ToArray();
        if (extensionRunes.Length is > 0 and < MaxDisplayFileNameCharacters)
        {
            var stemRunes = Path.GetFileNameWithoutExtension(fileName).EnumerateRunes().ToArray();
            return string.Concat(stemRunes.Take(MaxDisplayFileNameCharacters - extensionRunes.Length))
                   + extension;
        }

        return string.Concat(runes.Take(MaxDisplayFileNameCharacters));
    }

    private static string ResolveMediaType(
        string fileName,
        string? declaredMediaType,
        ReadOnlySpan<byte> bytes)
    {
        var inferred = InferMediaType(fileName, bytes);
        if (IsMeaningfulMediaType(inferred))
        {
            return inferred;
        }

        var normalizedDeclared = NormalizeMediaType(declaredMediaType);
        return IsMeaningfulMediaType(normalizedDeclared) ? normalizedDeclared : inferred;
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
        if (!string.IsNullOrWhiteSpace(extension)
            && ExtensionMediaTypes.TryGetValue(extension, out var mediaType))
        {
            return mediaType;
        }

        return LooksLikeText(bytes) ? "text/plain" : "application/octet-stream";
    }

    private static string NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return "application/octet-stream";
        }

        var normalized = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var slash = normalized.IndexOf('/');
        return normalized.Length <= 127
               && slash > 0
               && slash < normalized.Length - 1
               && slash == normalized.LastIndexOf('/')
               && normalized.Where((_, index) => index != slash).All(IsMediaTypeTokenCharacter)
            ? normalized
            : "application/octet-stream";
    }

    private static bool IsMediaTypeTokenCharacter(char character)
        => char.IsAsciiLetterOrDigit(character)
           || character is '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-';

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
        foreach (var value in sample)
        {
            if (value == 0)
            {
                return false;
            }
            if (value < 0x09 || value is > 0x0D and < 0x20)
            {
                suspicious++;
            }
        }

        return suspicious <= sample.Length / 100;
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
        => value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static string FormatByteCount(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.#} MB"
            : bytes >= 1024
                ? $"{bytes / 1024d:0.#} KB"
                : $"{bytes} B";
}
