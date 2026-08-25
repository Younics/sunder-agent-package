namespace Sunder.Package.Agent.TestSupport;

public static class TestPrerequisites
{
    public const string RequireExternalPrerequisitesVariable = "SUNDER_REQUIRE_TEST_PREREQUISITES";

    public static bool ExternalPrerequisitesAreRequired
        => string.Equals(
            Environment.GetEnvironmentVariable(RequireExternalPrerequisitesVariable),
            "1",
            StringComparison.Ordinal);

    public static string? EnvironmentVariableUnavailable(string name, string expectedValue = "1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return string.Equals(Environment.GetEnvironmentVariable(name), expectedValue, StringComparison.Ordinal)
            ? null
            : $"Set {name}={expectedValue} to run this prerequisite-backed test.";
    }

    public static void ThrowIfRequired(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (ExternalPrerequisitesAreRequired)
        {
            throw new InvalidOperationException(
                $"A selected test lane is missing a required prerequisite. {reason}");
        }
    }

}
