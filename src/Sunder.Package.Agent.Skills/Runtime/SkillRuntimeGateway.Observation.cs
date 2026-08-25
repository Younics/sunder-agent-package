#if SUNDER_SKILLS_APP
using Microsoft.Extensions.Logging;

namespace Sunder.Package.Agent.Skills.Runtime;

internal sealed partial class SkillAppRuntimeGateway
{
    private async Task ObserveChangesOnceAsync(
        Action markAvailable,
        CancellationToken cancellationToken)
    {
        var connected = false;
        await foreach (var _ in _client.SubscribeChangesAsync(cancellationToken))
        {
            if (!connected)
            {
                connected = true;
                markAvailable();
            }
            SkillsChanged?.Invoke();
        }
    }

    private void LogObservationRetry(Exception exception, TimeSpan retryDelay)
        => _logger.LogWarning(
            "Skills owner change observation failed and will retry in {RetryDelayMilliseconds} ms ({ExceptionType}).",
            retryDelay.TotalMilliseconds,
            exception.GetType().Name);
}
#endif
