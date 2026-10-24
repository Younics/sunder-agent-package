using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

internal static class AgentRuntimeStartupDiagnostics
{
    internal static async Task LogFailureAsync(
        IPackageEventLogger logger,
        PackageRuntimeGeneration generation,
        string phase,
        Exception error)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var token = deadline.Token;
        try
        {
            // Isolate synchronous logger I/O as well as an incomplete ValueTask.
            // Workers are already signalled; neither kind may delay cancellation.
            await Task.Run(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    // This event survives discarded stdout/stderr. Persist no
                    // exception message, SQL, path or transcript content.
                    var type = error.GetType().FullName ?? error.GetType().Name;
                    var trace = new StackTrace(error, fNeedFileInfo: false).ToString();
                    var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(type + "\n" + trace)));
                    await logger.ErrorAsync("runtime.generation.startup-failed",
                        "Agent Runtime startup failed; the candidate generation was rejected.",
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["runtime.generation.epoch"] = generation.ActivationId.ToString("N"),
                            ["runtime.session_generation"] = generation.SessionGeneration,
                            ["runtime.startup.phase"] = phase,
                            ["exception.type"] = type[..Math.Min(type.Length, 256)],
                            ["exception.fingerprint"] = fingerprint,
                        }, cancellationToken: token).ConfigureAwait(false);
                }
                catch
                {
                    // Observe late logger faults too, if it ignores the deadline.
                }
            }, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics cannot replace the startup failure or bypass cleanup.
        }
    }
}
