extern alias AnthropicApp;
extern alias GeminiApp;
extern alias LMStudioApp;
extern alias OpenAiApp;
extern alias DockerApp;
extern alias LocalApp;
extern alias DockerWorker;
extern alias LocalWorker;

using System.Reflection;
using DockerApp::Sunder.Package.Agent.Execution.Docker;
using LocalApp::Sunder.Package.Agent.Execution.Local;
using AnthropicApp::Sunder.Package.Agent.Provider.Anthropic;
using GeminiApp::Sunder.Package.Agent.Provider.Gemini;
using LMStudioApp::Sunder.Package.Agent.Provider.LMStudio;
using OpenAiApp::Sunder.Package.Agent.Provider.OpenAI;
using Sunder.Sdk.Settings;
using Xunit;
using LocalExecutionConfiguration = LocalWorker::Sunder.Package.Agent.Execution.Local.LocalExecutionConfiguration;
using DockerExecutionConfiguration = DockerWorker::Sunder.Package.Agent.Execution.Docker.DockerExecutionConfiguration;
using AnthropicSettingsViewModel = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsViewModel;
using GeminiSettingsViewModel = GeminiApp::Sunder.Package.Agent.Provider.Gemini.GeminiSettingsViewModel;
using LMStudioSettingsViewModel = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsViewModel;
using OpenAiSettingsViewModel = OpenAiApp::Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsViewModel;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class SettingsSchemaParityTests
{
    public static IEnumerable<object[]> HostedSettingsSchemas()
    {
        yield return [LocalExecutionConfiguration.Schema, typeof(LocalExecutionSettingsViewModel)];
        yield return [DockerExecutionConfiguration.Schema, typeof(DockerExecutionSettingsViewModel)];
        yield return [AnthropicProviderConfiguration.Schema, typeof(AnthropicSettingsViewModel)];
        yield return [GeminiProviderConfiguration.Schema, typeof(GeminiSettingsViewModel)];
        yield return [LMStudioProviderConfiguration.Schema, typeof(LMStudioSettingsViewModel)];
        yield return [OpenAiProviderConfiguration.Schema, typeof(OpenAiSettingsViewModel)];
    }

    [Theory]
    [MemberData(nameof(HostedSettingsSchemas))]
    public void CustomSettingsView_ExplicitlyOwnsEverySchemaKey(
        PackageSettingsSchema schema,
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
