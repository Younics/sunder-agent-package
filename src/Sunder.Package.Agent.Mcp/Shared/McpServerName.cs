namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpServerName
{
    internal static string Normalize(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "mcp_server" : value.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        var normalized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "mcp_server" : normalized;
    }
}
