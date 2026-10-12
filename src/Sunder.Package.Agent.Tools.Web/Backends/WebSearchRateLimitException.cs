namespace Sunder.Package.Agent.Tools.Web.Backends;

internal sealed class WebSearchRateLimitException(
    string backendId,
    TimeSpan? retryAfter = null,
    DateTimeOffset? retryAt = null) : Exception(
        backendId == "auto"
            ? "Both web search providers are rate limited. Try again after the cooldown or configure an optional search API key."
            : $"{(backendId == "exa" ? "Exa" : "Parallel")} web search is rate limited. Try again after the cooldown or configure an optional API key.")
{
    public string BackendId { get; } = backendId;

    public DateTimeOffset GetRetryAt(DateTimeOffset now)
    {
        if (retryAt is { } date)
        {
            return date > now ? date : now;
        }
        var delay = retryAfter is { } value && value >= TimeSpan.Zero
            ? value : TimeSpan.FromSeconds(60);
        return delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
    }
}
