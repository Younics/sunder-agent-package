using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolService
{
    internal const string StoredToolImageType = "sunder.stored-tool-image.v1";
    internal static Guid ToolImageOwner(Guid runId, string callId)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes("tool-image\n" + runId + "\n" + callId)).AsSpan(0, 16));

    internal async Task<AgentToolResult> AdoptToolImageAsync(AgentToolResult result, AgentToolExecutionContext context, CancellationToken token)
    {
        if (result.StructuredPayloadJson is not { } payload) return result;
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return result; }
        using var scope = document;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return result;
        if (type.GetString() == StoredToolImageType) throw new InvalidDataException("A tool cannot supply host-owned attachment metadata.");
        if (type.GetString() != "sunder.tool-image.v1") return result;
        if (payload.Length > 3 * 1024 * 1024) throw new InvalidDataException("Tool image envelope exceeds its byte budget.");
        if (_toolAttachmentStore is null || context.SessionId is not { } session || context.RunId is not { } run || string.IsNullOrWhiteSpace(context.ToolCallId))
            return new(result.ToolId, "Screenshot storage unavailable", Content: "Screenshot requires session-owned attachment storage.", IsError: true, ErrorCode: "tool-image-unavailable");
        if (root.EnumerateObject().GroupBy(member => member.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || root.EnumerateObject().Count() != 4 || !root.TryGetProperty("mediaType", out var mediaType) || mediaType.ValueKind != JsonValueKind.String || mediaType.GetString() != "image/png"
            || !root.TryGetProperty("bytes", out var data) || data.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("sha256", out var digest) || digest.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid tool image envelope.");
        var bytes = data.GetBytesFromBase64();
        var (width, height) = ValidateToolPng(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest.GetString()) throw new InvalidDataException("Tool image hash mismatch.");
        var stored = await _toolAttachmentStore.StoreToolImageAsync(session, ToolImageOwner(run, context.ToolCallId), bytes, token).ConfigureAwait(false);
        return result with { Content = JsonSerializer.Serialize(new { image = "package-view", width, height }),
            StructuredPayloadJson = JsonSerializer.Serialize(new { type = StoredToolImageType, attachment = stored.Metadata }) };
    }

    internal static (int Width, int Height) ValidateToolPng(byte[] bytes)
    {
        if (bytes.Length is < 57 or > 2 * 1024 * 1024 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4)) != 13 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Invalid bounded tool PNG.");
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 1 or > 2048 || height is < 1 or > 2048 || (long)width * height > 4 * 1024 * 1024)
            throw new InvalidDataException("Tool image dimensions exceed their budget.");
        var foundData = false; var foundEnd = false;
        for (var offset = 8; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 12) throw new InvalidDataException("Truncated tool PNG.");
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length < 0 || length > bytes.Length - offset - 12) throw new InvalidDataException("Invalid tool PNG chunk.");
            var chunk = bytes.AsSpan(offset + 4, length + 4);
            uint crc = uint.MaxValue;
            foreach (var value in chunk)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + length + 8, 4))) throw new InvalidDataException("Tool PNG checksum mismatch.");
            if (chunk[..4].SequenceEqual("IDAT"u8)) foundData = true;
            if (chunk[..4].SequenceEqual("IEND"u8))
            {
                if (length != 0 || offset + 12 != bytes.Length) throw new InvalidDataException("Invalid tool PNG end.");
                foundEnd = true;
            }
            offset += length + 12;
        }
        if (!foundData || !foundEnd) throw new InvalidDataException("Incomplete tool PNG.");
        return (width, height);
    }

    internal static AgentAttachmentMetadata? ReadStoredToolImage(string? json)
    {
        if (json is null || json.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var metadata = document.RootElement.TryGetProperty("type", out var type) && type.GetString() == StoredToolImageType
                ? document.RootElement.GetProperty("attachment").Deserialize<AgentAttachmentMetadata>() : null;
            return metadata is { MediaType: "image/png", Kind: AgentAttachmentKind.Image, IsText: false, SizeBytes: >= 57 and <= 2 * 1024 * 1024,
                StorageRelativePath: { Length: > 0 and <= 1024 }, Sha256: { Length: 64 } } ? metadata : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
}
