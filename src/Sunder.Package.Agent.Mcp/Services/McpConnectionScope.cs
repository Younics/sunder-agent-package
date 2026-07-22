using ModelContextProtocol.Client;

namespace Sunder.Package.Agent.Mcp.Services;

internal readonly record struct McpConnectionScope(string ScopeId)
{
    public static McpConnectionScope Shared { get; } = new("metadata");

    public static McpConnectionScope For(Guid? sessionId, string? workspaceId)
        => sessionId is null
            ? Shared
            : new($"session:{sessionId.Value:N}:workspace:{workspaceId?.Trim().ToLowerInvariant() ?? "none"}");

    public bool IsForSession(Guid sessionId)
        => ScopeId.StartsWith($"session:{sessionId:N}:", StringComparison.OrdinalIgnoreCase);

    public bool TryGetSessionId(out Guid sessionId)
    {
        const string prefix = "session:";
        if (ScopeId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var separator = ScopeId.IndexOf(':', prefix.Length);
            if (separator > prefix.Length
                && Guid.TryParseExact(ScopeId[prefix.Length..separator], "N", out sessionId))
            {
                return true;
            }
        }

        sessionId = default;
        return false;
    }
}

internal sealed class McpClientInvocationLease(McpClient? client, Action release) : IAsyncDisposable
{
    private Action? _release = release;

    public McpClient? Client { get; } = client;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        return ValueTask.CompletedTask;
    }
}
