using System.Text;

namespace Sunder.Package.Agent.Mcp;

internal static class McpToolIdentity
{
    private const string StablePrefix = "m1_";
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string CreateStable(string serverId, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var encodedServerId = Encode(serverId);
        return $"{StablePrefix}{encodedServerId.Length}_{encodedServerId}{Encode(toolName)}";
    }

    public static string CreateLegacyAlias(string normalizedServerName, string toolName)
        => $"{normalizedServerName}_{toolName}";

    public static bool TryParseLegacy(
        string toolId,
        IEnumerable<ConfiguredMcpServerRecord> servers,
        out ConfiguredMcpServerRecord? server,
        out string toolName)
    {
        var candidates = servers
            .Where(item => item.IsEnabled && toolId.StartsWith(item.Name + "_", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Server: item, ToolName: toolId[(item.Name.Length + 1)..]))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ToolName))
            .ToArray();
        if (candidates.Length != 1)
        {
            server = null;
            toolName = string.Empty;
            return false;
        }

        server = candidates[0].Server;
        toolName = candidates[0].ToolName;
        return true;
    }

    public static bool TryParseStable(string toolId, out string serverId, out string toolName)
    {
        serverId = string.Empty;
        toolName = string.Empty;
        if (string.IsNullOrWhiteSpace(toolId) || !toolId.StartsWith(StablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lengthSeparator = toolId.IndexOf('_', StablePrefix.Length);
        if (lengthSeparator < 0
            || !int.TryParse(toolId.AsSpan(StablePrefix.Length, lengthSeparator - StablePrefix.Length), out var serverLength)
            || serverLength <= 0)
        {
            return false;
        }

        var payload = toolId[(lengthSeparator + 1)..];
        if (payload.Length <= serverLength
            || !TryDecode(payload[..serverLength], out serverId)
            || !TryDecode(payload[serverLength..], out toolName)
            || string.IsNullOrWhiteSpace(serverId)
            || string.IsNullOrWhiteSpace(toolName))
        {
            serverId = string.Empty;
            toolName = string.Empty;
            return false;
        }

        return true;
    }

    private static string Encode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitCount = 0;
        foreach (var item in bytes)
        {
            buffer = (buffer << 8) | item;
            bitCount += 8;
            while (bitCount >= 5)
            {
                result.Append(Alphabet[(buffer >> (bitCount - 5)) & 31]);
                bitCount -= 5;
            }
        }

        if (bitCount > 0)
        {
            result.Append(Alphabet[(buffer << (5 - bitCount)) & 31]);
        }

        return result.ToString();
    }

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        var bytes = new List<byte>((value.Length * 5) / 8);
        var buffer = 0;
        var bitCount = 0;
        foreach (var character in value)
        {
            var index = Alphabet.IndexOf(char.ToLowerInvariant(character));
            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bytes.Add((byte)(buffer >> (bitCount - 8)));
                bitCount -= 8;
            }
        }

        try
        {
            decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString([.. bytes]);
            return string.Equals(Encode(decoded), value, StringComparison.OrdinalIgnoreCase);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
