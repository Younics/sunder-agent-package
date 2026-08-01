using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octokit;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Skills;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new GitHubClient(new ProductHeaderValue("Sunder-Agent-Skills")));
        services.AddSingleton<IGitHubSkillClient, OctokitGitHubSkillClient>();
        services.AddSingleton<SkillStore>();
        services.AddSingleton<SkillImportService>();
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton<SkillsFeature>();
        services.AddSingleton<SkillStackContributor>();
        services.AddSingleton<SkillRuntimeHandler>();
        services.AddSingleton<SkillRuntimeChangeStream>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        var feature = services.GetRequiredService<SkillsFeature>();
        registry.RegisterRpcProvider("skills.selectable.capabilities", AgentSelectableCapabilityProviderRpc.CreateHandler(feature));
        registry.RegisterRpcProvider("skills.tools", AgentToolSourceRpc.CreateHandler(feature, services.GetRequiredService<AgentRpcCatalog>()));
        registry.RegisterRpcProvider("skills.prompt.context", AgentPromptContextContributorRpc.CreateHandler(feature, services.GetRequiredService<AgentRpcCatalog>()));
        var stackContributor = services.GetRequiredService<SkillStackContributor>();
        registry.RegisterStackContributor("skills.stack", stackContributor, services);
        registry.RegisterRuntimeOperation(SkillRuntimeOperations.Query, services.GetRequiredService<SkillRuntimeHandler>());
        registry.RegisterRuntimeOperation(SkillRuntimeOperations.Command, services.GetRequiredService<SkillRuntimeHandler>());
        registry.RegisterRuntimeStream(SkillRuntimeOperations.Changes, services.GetRequiredService<SkillRuntimeChangeStream>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<SkillAppRuntimeGateway>();
        services.AddSingletonAlias<ISkillManagementGateway, SkillAppRuntimeGateway>();
        services.AddTransient(provider => new SkillSettingsViewModel(
            provider.GetRequiredService<ISkillManagementGateway>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterSettingsView<SkillSettingsView>();
}
