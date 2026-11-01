

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class RenderedViewWaits
{
    internal static async Task WaitUntilAsync(
        Func<bool> condition,
        Func<string>? timeoutDiagnostic = null)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                var diagnostic = timeoutDiagnostic?.Invoke();
                throw new TimeoutException(
                    string.IsNullOrWhiteSpace(diagnostic)
                        ? "Timed out waiting for view-model state."
                        : $"Timed out waiting for view-model state. {diagnostic}");
            }

            await Task.Delay(10);
        }
    }
}
