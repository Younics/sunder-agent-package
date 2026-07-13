using System.Text.RegularExpressions;

namespace Sunder.Agent.Execution.Common;

public static partial class ProcessOutput
{
    public static FormattedProcessOutput Format(
        ProcessRunResult result,
        int maxLength,
        string? timeoutMessage = null,
        bool stripAnsiEscapeSequences = false)
    {
        var output = stripAnsiEscapeSequences
            ? StripAnsiEscapeSequences(result.CombinedOutput)
            : result.CombinedOutput;
        output = Truncate(output, maxLength, out var wasTruncated);
        if (result.TimedOut && !string.IsNullOrWhiteSpace(timeoutMessage))
        {
            output = string.IsNullOrWhiteSpace(output)
                ? timeoutMessage
                : string.Concat(timeoutMessage, Environment.NewLine, output);
        }

        return new FormattedProcessOutput(output, result.WasTruncated || wasTruncated);
    }

    public static string Truncate(string output, int maxLength, out bool wasTruncated)
    {
        wasTruncated = output.Length > maxLength;
        return wasTruncated
            ? output[..maxLength] + Environment.NewLine + "[output truncated]"
            : output;
    }

    public static string StripAnsiEscapeSequences(string output)
        => string.IsNullOrEmpty(output) ? output : AnsiEscapeRegex().Replace(output, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\a]*(?:\a|\x1B\\)|\x1B[@-_]")]
    private static partial Regex AnsiEscapeRegex();
}

public sealed record FormattedProcessOutput(string Content, bool WasTruncated);
