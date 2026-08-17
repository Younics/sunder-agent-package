using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexAuthTelemetry
{
    private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(1);
    private readonly IPackageContext _packageContext;
    private readonly string _modeId;
    private readonly TimeSpan _writeTimeout;

    public CodexAuthTelemetry(
        IPackageContext packageContext,
        string modeId,
        TimeSpan? writeTimeout = null)
    {
        _packageContext = packageContext;
        _modeId = modeId;
        _writeTimeout = writeTimeout ?? DefaultWriteTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_writeTimeout, TimeSpan.Zero);
    }

    public ValueTask SessionSavedAsync(string flow, OpenAiCodexSession session)
        => WriteAsync(
            PackageLogLevel.Information,
            "openai.codex.auth.session.saved",
            "Codex auth session was saved.",
            attributes: new Dictionary<string, object?>
            {
                ["auth.flow"] = flow,
                ["auth.expires_at"] = session.ExpiresAtUtc,
            });

    public async ValueTask WriteAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null)
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
            using var timeoutCancellation = new CancellationTokenSource(_writeTimeout);
            var write = _packageContext.Logging.Events
                .WriteAsync(
                    level,
                    eventName,
                    message,
                    mergedAttributes,
                    cancellationToken: timeoutCancellation.Token)
                .AsTask();
            try
            {
                await write.WaitAsync(_writeTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = ObserveAsync(write);
            }
        }
        catch
        {
            // Diagnostics must never interrupt authorization.
        }
    }

    public void WriteObserved(
        PackageLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null)
        => _ = ObserveAsync(WriteAsync(
                level,
                eventName,
                message,
                elapsedMilliseconds,
                attributes)
            .AsTask());

    private static async Task ObserveAsync(Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics are best effort, including writes that outlive their timeout.
        }
    }
}
