using Microsoft.Extensions.DependencyInjection;
using Octokit;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new GitHubClient(new ProductHeaderValue("Sunder-Agent-Skills")));
        services.AddSingleton<IGitHubSkillClient, OctokitGitHubSkillClient>();
        services.AddSingleton<SkillStore>();
        services.AddSingleton<SkillImportService>();
        services.AddSingleton<SkillsFeature>();
        services.AddTransient<SkillSettingsViewModel>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        var feature = services.GetRequiredService<SkillsFeature>();
        registry.RegisterSettingsView<SkillSettingsView>();
        registry.RegisterExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, feature);
        registry.RegisterExtension(PackageExtensionPoints.ToolSources, feature);
        registry.RegisterExtension(PackageExtensionPoints.SystemPromptContributors, feature);
        registry.RegisterExtension(PackageExtensionPoints.ExecutionResourceProviders, feature);
    }
}
