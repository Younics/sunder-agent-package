using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal static class RunActivityText
{
    internal static string FormatRetry(AgentRunRetryState retry, DateTimeOffset now)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling((AgentTranscriptTime.Read(retry.RetryAtUtc) - now).TotalSeconds));
        var action = retry.IsContinuation ? "continuing" : "retrying";
        return seconds > 0
            ? $"{retry.Reason} — {action} in {seconds}s · attempt {retry.Attempt}"
            : $"{retry.Reason} — {action} now · attempt {retry.Attempt}";
    }
}
