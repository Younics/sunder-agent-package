namespace Sunder.Agent.Execution.Common;

public static class BoundedValue
{
    public static int ParseInt32(string? value, int fallback, int minimum, int maximum)
        => TryParseInt32(value, minimum, maximum, out var parsed)
            ? parsed
            : fallback;

    public static bool TryParseInt32(string? value, int minimum, int maximum, out int parsed)
        => int.TryParse(value, out parsed) && parsed >= minimum && parsed <= maximum;

    public static int ParsePositiveInt32Clamped(string? value, int fallback, int maximum)
        => int.TryParse(value, out var parsed) && parsed > 0
            ? Math.Min(parsed, maximum)
            : fallback;

    public static bool IsInRange(int? value, int minimum, int maximum)
        => value is null || value >= minimum && value <= maximum;
}
