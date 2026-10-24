using System.Text;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderProjectDriver
{
    // Process chunks are not lines and stdout/stderr can report concurrently.
    // These messages affect presentation only; they never authorize lock recovery.
    private sealed class ProcessStatusProgress(IBuilderJobReporter reporter) : IProgress<string>
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
                    }
                    else if (_line.Length < 8192) _line.Append(character);
                }
            }
        }

    }
}
