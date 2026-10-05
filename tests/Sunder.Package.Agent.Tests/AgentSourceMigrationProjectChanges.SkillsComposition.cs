using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeSkillsComposition(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Skills/", StringComparison.Ordinal)) return text;
        var file = Path.GetFileName(path);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        var sharedCount = file switch
        {
            "SkillRuntimeAbstractions.cs" => 2,
            "SkillRuntimeContracts.cs" => 5,
            "SkillRuntimeGateway.cs" or "SkillTransferLimits.cs" or "InstalledSkillRecord.cs" => 1,
            _ => 0,
        };
        if (sharedCount != 0)
        {
            Assert.Equal(sharedCount + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        if (file == "SkillSettingsView.axaml.cs")
        {
            const string view = "[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(SkillSettingsViewModel))]\n";
            Assert.Equal(2, text.Split(view, StringSplitOptions.None).Length);
            text = text.Replace(view, "", StringComparison.Ordinal);
        }
        if (file is not ("GitHubSkillClient.cs" or "SkillImportService.cs" or "SkillSettingsView.axaml.cs")) return text;
        const string attribute = "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n";
        Assert.Equal(2, text.Split(attribute, StringSplitOptions.None).Length);
        return text.Replace(attribute, "", StringComparison.Ordinal);
    }
}
