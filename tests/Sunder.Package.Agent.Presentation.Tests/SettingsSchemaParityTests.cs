using System.Reflection;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Provider.Anthropic;
using Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Provider.LMStudio;
using Sunder.Package.Agent.Provider.OpenAI;
using Sunder.Sdk.Configuration;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class SettingsSchemaParityTests
{
    public static IEnumerable<object[]> HostedSettingsSchemas()
    {
        yield return [LocalExecutionConfiguration.Schema, typeof(LocalExecutionSettingsViewModel)];
        yield return [DockerExecutionConfiguration.Schema, typeof(DockerExecutionSettingsViewModel)];
        yield return [McpPackageConfiguration.Schema, typeof(AgentMcpSettingsViewModel)];
        yield return [AnthropicProviderConfiguration.Schema, typeof(AnthropicSettingsViewModel)];
        yield return [GeminiProviderConfiguration.Schema, typeof(GeminiSettingsViewModel)];
        yield return [LMStudioProviderConfiguration.Schema, typeof(LMStudioSettingsViewModel)];
        yield return [OpenAiProviderConfiguration.Schema, typeof(OpenAiSettingsViewModel)];
    }

    [Theory]
    [MemberData(nameof(HostedSettingsSchemas))]
    public void CustomSettingsView_ExplicitlyOwnsEverySchemaKey(
        PackageConfigurationSchema schema,
        Type viewModelType)
    {
        var property = viewModelType.GetProperty(
            "OwnedConfigurationKeys",
            BindingFlags.Static | BindingFlags.NonPublic);
        var ownedKeys = Assert.IsAssignableFrom<IEnumerable<string>>(property?.GetValue(null));
        var schemaKeys = schema.Sections.SelectMany(section => section.Fields).Select(field => field.Key);

        Assert.Equal(
            schemaKeys.Order(StringComparer.OrdinalIgnoreCase),
            ownedKeys.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequiredParityKeys_AreOwnedByTheirHostedViews()
    {
        Assert.Contains("auth.mode", GetOwnedKeys(typeof(OpenAiSettingsViewModel)));
        Assert.Contains(LocalExecutionConfiguration.TimeoutKey, GetOwnedKeys(typeof(LocalExecutionSettingsViewModel)));
    }

    private static IReadOnlyCollection<string> GetOwnedKeys(Type viewModelType)
        => Assert.IsAssignableFrom<IReadOnlyCollection<string>>(
            viewModelType.GetProperty("OwnedConfigurationKeys", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null));
}
