using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexAuthTelemetry(IPackageContext packageContext, string modeId)
{
    private readonly IPackageContext _packageContext = packageContext;
    private readonly string _modeId = modeId;

    public void SessionSaved(string flow, OpenAiCodexSession session)
        => Write(
            PackageLogLevel.Information,
            "openai.codex.auth.session.saved",
            "Codex auth session was saved.",
            attributes: new Dictionary<string, object?>
            {
                ["auth.flow"] = flow,
                ["auth.expires_at"] = session.ExpiresAtUtc,
            });

    public void Write(
        PackageLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null)
    {
        var mergedAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["auth.mode"] = _modeId,
        };
        if (elapsedMilliseconds is not null)
        {
            mergedAttributes["duration.ms"] = elapsedMilliseconds.Value;
        }

        if (attributes is not null)
        {
            foreach (var attribute in attributes)
            {
                mergedAttributes[attribute.Key] = attribute.Value;
            }
        }

        try
        {
            _packageContext.Logging.Events
                .WriteAsync(level, eventName, message, mergedAttributes, exception)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Diagnostics must never interrupt authorization.
        }
    }
}
