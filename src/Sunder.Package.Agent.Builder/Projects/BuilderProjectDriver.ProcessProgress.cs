using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderProjectDriver
{
    // Process chunks are not lines and stdout/stderr can report concurrently.
    // These messages affect presentation only; they never authorize lock recovery.
    private sealed class ProcessStatusProgress(IBuilderJobReporter reporter, bool watchProgress = false) : IProgress<string>
    {
        private readonly object _gate = new();
        private readonly StringBuilder _line = new();

        public void Report(string value)
        {
            lock (_gate)
            {
                reporter.Report(value);
                foreach (var character in value)
                {
                    if (character is '\r' or '\n')
                    {
                        var line = _line.ToString();
                        _line.Clear();
                        if (line.Contains("package-build.output-lock.waiting:", StringComparison.Ordinal))
                            reporter.Update(new() { Message = "Waiting for a generated-output lock. See command output for ownership details; cancellation remains available." });
                        else if (line.Contains("package-build.output-lock.acquired:", StringComparison.Ordinal))
                            reporter.Update(new() { Message = "Generated-output locks acquired; continuing the command." });
                        else if (watchProgress && line.StartsWith("sunder.package.watch:", StringComparison.Ordinal))
                            ReportWatchProgress(line["sunder.package.watch:".Length..]);
                    }
                    else if (_line.Length < 8192) _line.Append(character);
                }
            }
        }

        private void ReportWatchProgress(string json)
        {
            // Output can include arbitrary project logs. Malformed/unknown diagnostics
            // remain in Output, without affecting the job outcome or any ownership fence.
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("schemaVersion", out var version)
                    || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1
                    || !value.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String) return;
                var update = state.GetString() switch
                {
                    "source-changed" => new BuilderJobUpdate { LastSourceChangeAtUtc = DateTimeOffset.UtcNow },
                    "building" => new BuilderJobUpdate { Phase = BuilderJobPhase.Building },
                    "ready" => new BuilderJobUpdate { Phase = BuilderJobPhase.Watching, LastSuccessfulBuildAtUtc = DateTimeOffset.UtcNow },
                    "failed" => new BuilderJobUpdate { Phase = BuilderJobPhase.BuildFailed },
                    _ => null,
                };
                if (update is not null) reporter.Update(update);
            }
            catch (JsonException) { }
        }
    }
}
