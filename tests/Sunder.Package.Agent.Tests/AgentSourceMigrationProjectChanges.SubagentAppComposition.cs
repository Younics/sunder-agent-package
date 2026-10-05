using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    private static string BeforeSubagentAppComposition(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        var file = Path.GetFileName(path);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        var count = normalized.Contains("/src/Sunder.Package.Agent.Subagents/", StringComparison.Ordinal) ? file switch
        {
            "SubagentRuntimeContracts.cs" => 18,
            "SubagentRuntimeBridge.cs" or "SubagentRecord.cs" or "SubagentConstants.cs" or "TranscriptPageCursor.cs" => 1,
            _ => 0,
        } : normalized.Contains("/src/Sunder.Package.Agent.Shared/", StringComparison.Ordinal) ? file switch
        {
            "ProviderModelLoader.cs" => 3,
            "TranscriptTurnTransportProjection.cs" => 1,
            _ => 0,
        } : normalized.EndsWith("/src/Sunder.Package.Agent.Contracts/Tools/IAgentToolPresentationResolver.cs", StringComparison.Ordinal) ? 1 : 0;
        if (count != 0)
        {
            Assert.Equal(count + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        if (normalized.Contains("/src/Sunder.Package.Agent.Shared/", StringComparison.Ordinal)
            && file is "ActivityTicker.cs" or "InlineRenameFocusCoordinator.cs")
            Replace("[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]\n", "");
        if (file == "SubagentRuntimeBridge.cs") Replace("internal sealed partial class SubagentAppRuntimeGateway", "internal sealed class SubagentAppRuntimeGateway");
        if (file is "SubagentsView.axaml.cs" or "SubsessionsView.axaml.cs")
        {
            var sessions = file == "SubsessionsView.axaml.cs";
            var name = sessions ? "Subsessions" : "Subagents";
            Replace($"[Sunder.Sdk.Packaging.SunderPackageView(Sunder.Package.Agent.Subagents.Services.SubagentConstants.{(sessions ? "SubsessionsViewId" : "PackageId")}, \"{name}\",\n"
                + $"    ViewModel = typeof({name}ViewModel), IconPath = \"assets/{(sessions ? "sub-session" : "sub-profile")}-icon.png\", DefaultPlacement = PackageViewPlacement.{(sessions ? "LeftTop" : "RightTop")})]\n", "");
            Replace("    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n", "");
        }
        return text;

        void Replace(string current, string previous)
        {
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            text = text.Replace(current, previous, StringComparison.Ordinal);
        }
    }
}
