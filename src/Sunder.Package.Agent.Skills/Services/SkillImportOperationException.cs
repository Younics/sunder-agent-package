using Sunder.Package.Agent.Skills.Runtime;

namespace Sunder.Package.Agent.Skills.Services;

// Only source/staging failures are wrapped here. Persistent index/journal errors
// and failed compensation must retain their original provider-fault behavior.
internal sealed class SkillImportOperationException(
    string phase,
    string sourcePath,
    Exception cause,
    int completedCount = 0)
    : SkillOwnerOperationException(
        (completedCount == 0 ? string.Empty : $"Imported {completedCount} skill(s) before the failure. ")
        + $"Could not {phase} '{SafeText(sourcePath, 160)}': {SafeText(cause.Message, 200)}", cause)
{
    public string Phase { get; } = phase;
    public string SourcePath { get; } = SafeText(sourcePath, 512);
    public string FailureMessage { get; } = SafeText(cause.Message, 512);
    public int CompletedCount { get; } = completedCount;

    public static bool IsSourceFailure(Exception error)
        => error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Security.SecurityException or System.Text.DecoderFallbackException
            or HttpRequestException;

    private static string SafeText(string value, int maximum)
        => new(value.Take(maximum).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
}
