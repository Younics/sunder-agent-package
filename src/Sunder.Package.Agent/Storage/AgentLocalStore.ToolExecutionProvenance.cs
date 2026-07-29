namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static void ValidateOptionalProvenance(string? value, int maxLength, string fieldName)
    {
        if (value is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Tool execution {fieldName} must be nonblank, bounded, and free of surrounding whitespace when supplied.");
        }
    }
}
