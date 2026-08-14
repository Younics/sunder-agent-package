namespace Sunder.Package.Agent.Mcp.Runtime;

internal static class McpManagementPayload
{
    private const int MaximumChunkCharacters = 900_000;

    internal static IReadOnlyList<string> Split(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        var chunks = new List<string>();
        for (var offset = 0; offset < payload.Length;)
        {
            var length = Math.Min(MaximumChunkCharacters, payload.Length - offset);
            if (offset + length < payload.Length
                && char.IsHighSurrogate(payload[offset + length - 1])
                && char.IsLowSurrogate(payload[offset + length]))
            {
                length--;
            }
            chunks.Add(payload.Substring(offset, length));
            offset += length;
        }
        if (chunks.Count > 4)
        {
            throw new InvalidDataException("The MCP management payload exceeds the RPC limit.");
        }
        return chunks;
    }

    internal static string Join(IReadOnlyList<string> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count is 0 or > 4 || chunks.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("The MCP management payload chunks are invalid.");
        }
        return string.Concat(chunks);
    }
}
