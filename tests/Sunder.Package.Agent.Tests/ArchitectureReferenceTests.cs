using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias SubagentsWorker;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;

public sealed class ArchitectureReferenceTests
{
    [Fact]
    public void AgentAppComposition_CannotResolveStoreOrCreateAgentDatabase()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var databasePath = packageScope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        var historyDatabasePath = packageScope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/history-search.db");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(packageScope.Context);
        services.AddSingleton<IPackageContext>(packageScope.Context);
        using var rpcState = new RegressionTestRpcState();
        services.AddSingleton<Sunder.Sdk.Rpc.ISunderRpcClient>(
            new RegressionTestRpcClient(rpcState, "younics.ai.agent"));
        var module = (ISunderAppPackageModule)Activator.CreateInstance(
            Assembly.Load("Sunder.Package.Agent.App").GetType("Sunder.Generated.Package.AppModule", true)!)!;
        module.ConfigureAppServices(services, packageScope.Context);
        using var provider = services.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.Null(provider.GetService(typeof(AgentLocalStore)));
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(historyDatabasePath));
    }

    [Fact]
    public void HistorySearch_RemainsAnInternalRuntimeProjection()
    {
        var contractsAssembly = Assembly.Load("Sunder.Package.Agent");
        Assert.DoesNotContain(
            contractsAssembly.ExportedTypes,
            type => type.Name.Contains("HistorySearch", StringComparison.Ordinal));

        Assert.DoesNotContain(
            CanonicalDescriptorInventory.PublicAgent().SelectMany(entry => entry.Descriptor.Definitions.Keys),
            name => name.Contains("HistorySearch", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentDatabase_IsOpenedOnlyByRuntimeComposition()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var databasePath = packageScope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        Assert.False(File.Exists(databasePath));

        using var provider = RegressionTestRuntime.Create(packageScope.Context);

        Assert.NotNull(provider.GetRequiredService<AgentLocalStore>());
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public void AgentAppModule_DoesNotRegisterRuntimeServicesOrExtensions()
    {
        var source = File.ReadAllText(Path.Combine(CanonicalPackageRoles.Analysis(CanonicalPackageRoles.Source("Agent")), "roles/app/app-module.g.cs"));

        Assert.DoesNotContain("AgentLocalStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterExtension", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureRuntimeServices", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StackDependencyContributors_RequireOwnedProviderBindings()
    {
        var contributorTypes = new[]
        {
            typeof(AgentProfileStackContributor),
            typeof(AgentWorkspaceStackContributor),
        };

        foreach (var contributorType in contributorTypes)
        {
            var parameters = Assert.Single(contributorType.GetConstructors()).GetParameters();
            Assert.DoesNotContain(parameters, candidate => candidate.ParameterType.Namespace == "Sunder.Package.Agent.Protocol");
            Assert.All(parameters, candidate =>
            {
                Assert.False(candidate.IsOptional);
                Assert.False(candidate.HasDefaultValue);
            });
        }

        var subagentParameters = Assert.Single(typeof(SubagentStackContributor).GetConstructors()).GetParameters();
        Assert.Equal(4, subagentParameters.Length);
        Assert.DoesNotContain(subagentParameters, parameter => parameter.ParameterType.Name == "ISubagentRuntimeBindings");
        Assert.Contains(subagentParameters, parameter => parameter.ParameterType.Name == "SubagentService");
        Assert.Contains(subagentParameters, parameter => parameter.ParameterType.Name == "IPackageContext");
        Assert.Equal(2, subagentParameters.Count(parameter => parameter.ParameterType.Name == "IServiceProviders`1"));
        Assert.All(subagentParameters, parameter =>
        {
            Assert.False(parameter.IsOptional);
            Assert.False(parameter.HasDefaultValue);
        });
    }

    [Fact]
    public void AgentExtensionPackages_DoNotReferenceBaseAgentImplementationProjectOrPackage()
    {
        var baseAgentProjectPath = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent",
            "Sunder.Package.Agent.csproj");
        var extensionProjects = GetExtensionProjectPaths();

        Assert.NotEmpty(extensionProjects);
        foreach (var projectPath in extensionProjects)
        {
            var projectReferences = GetProjectReferencePaths(projectPath);
            var packageReferences = GetReferenceIncludes(projectPath, "PackageReference");

            Assert.DoesNotContain(baseAgentProjectPath, projectReferences, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Agent", packageReferences, StringComparer.OrdinalIgnoreCase);

            foreach (var sourceFile in Directory.EnumerateFiles(Path.GetDirectoryName(projectPath)!, "*.cs", SearchOption.AllDirectories)
                         .Where(AgentPackageRepositoryInventory.IsSourceFile))
            {
                var source = File.ReadAllText(sourceFile);
                Assert.DoesNotContain("Sunder.Package.Agent.PackageViews", source, StringComparison.Ordinal);
                Assert.DoesNotContain("Sunder.Package.Agent.Services", source, StringComparison.Ordinal);
                Assert.DoesNotContain("Sunder.Package.Agent.Storage", source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AgentExtensionPackages_SelectAgentApiWhileBuilderRemainsIndependent()
    {
        var protocolProjectPath = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "Sunder.Package.Agent.Contracts.csproj");
        var extensionProjects = GetExtensionProjectPaths();

        Assert.NotEmpty(extensionProjects);
        foreach (var projectPath in extensionProjects)
        {
            Assert.DoesNotContain(protocolProjectPath, GetProjectReferencePaths(projectPath), StringComparer.OrdinalIgnoreCase);
            using var locked = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(projectPath)!, "sunder-contracts.lock.json")));
            Assert.True(locked.RootElement.GetProperty("packageArtifacts").TryGetProperty("younics.ai.agent", out _));
            if (Path.GetFileNameWithoutExtension(projectPath) == "Sunder.Package.Agent.Builder")
            {
                // Generated contract imports support optional tools, not a binary
                // reference or a requirement that Agent be installed and live.
                var import = Assert.Single(locked.RootElement.GetProperty("packageImports").EnumerateObject());
                Assert.Equal("younics.ai.agent", import.Name);
                Assert.Equal("optional", import.Value.GetProperty("presence").GetString());
                Assert.Empty(import.Value.GetProperty("requiredContracts").EnumerateArray());
                Assert.Equal(new[] { "runtime" }, import.Value.GetProperty("consumerRoles").EnumerateArray().Select(role => role.GetString()));
                var providers = locked.RootElement.GetProperty("providerImports").EnumerateObject().ToArray();
                Assert.Equal(new[] { "permissions", "tools" }, providers.Select(provider => provider.Name).Order(StringComparer.Ordinal));
                foreach (var provider in providers)
                {
                    Assert.Equal("younics.ai.agent", provider.Value.GetProperty("packageId").GetString());
                    Assert.Equal("optional", provider.Value.GetProperty("presence").GetString());
                    Assert.Equal(provider.Name == "tools" ? "younics.ai.agent.tool.source" : "younics.ai.agent.permission.surface",
                        provider.Value.GetProperty("contractId").GetString());
                }
                Assert.Empty(locked.RootElement.GetProperty("platformImports").EnumerateObject());
            }
        }
    }

    [Fact]
    public void ProductionAssemblies_DoNotReferenceRetiredContractsAssemblyIdentity()
    {
        const string retiredAssemblyName = "Sunder.Package.Agent.Contracts";
        var assemblyNames = AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .SelectMany(static package => package.HasAppRole ? new[] { package.Name, package.AppAssemblyName! } : [package.Name])
            .Distinct(StringComparer.Ordinal);
        foreach (var assemblyName in assemblyNames)
        {
            var assembly = Assembly.Load(assemblyName);
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => reference.Name is retiredAssemblyName or "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        }
    }

    [Fact]
    public void ProductionSourceLinks_AreAllowlistedRepositoryInternalAndDoNotDeclarePublicTypes()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var sourceRootPrefix = sourceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sourceLinks = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(static path => Path.GetExtension(path) is ".csproj" or ".props")
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .SelectMany(projectPath => GetSourceLinkedCompileFiles(projectPath)
                .Select(sourcePath => new SourceLink(projectPath, sourcePath)))
            .OrderBy(static link => link.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(sourceLinks);
        var reviewedPublicLinks = new HashSet<string>(StringComparer.Ordinal)
        {
            "src/Sunder.Package.Agent/Models/Providers/AgentChatClientContext.cs",
            "src/Sunder.Package.Agent/Models/Providers/AgentChatProviderException.cs",
            "src/Sunder.Package.Agent/Models/Providers/AgentChatModelOptionKeys.cs",
            "src/Sunder.Package.Agent/Models/Tools/AgentToolArgumentObject.cs",
            "src/Sunder.Package.Agent/Models/Tools/AgentToolCallRequest.cs",
            "src/Sunder.Package.Agent/Models/Tools/AgentPayloadLimits.cs",
            "src/Sunder.Package.Agent/Models/Tools/AgentPermissionBoundaryIds.cs",
            "src/Sunder.Package.Agent/Models/Tools/AgentFileReadErrorCodes.cs",
            "src/Sunder.Package.Agent/Models/Tools/AgentToolResultErrorCodes.cs",
            "src/Sunder.Package.Agent/Models/Execution/AgentFileSearchErrorCodes.cs",
            "src/Sunder.Package.Agent/Models/Execution/AgentShellSyntaxKinds.cs",
            "src/Sunder.Package.Agent.Provider.Shared/Models/ProviderChatModels.cs",
            "src/Sunder.Package.Agent.Shared/Execution/AgentExecutionFacetIds.cs",
        };
        foreach (var sourceLink in sourceLinks)
        {
            Assert.True(
                sourceLink.DeclarationPath.StartsWith(sourceRootPrefix, StringComparison.OrdinalIgnoreCase),
                $"Source-link declaration '{sourceLink.DeclarationPath}' must stay under the repository src directory.");
            Assert.True(
                sourceLink.SourcePath.StartsWith(sourceRootPrefix, StringComparison.OrdinalIgnoreCase),
                $"Source link '{sourceLink.SourcePath}' must stay under the repository src directory.");

            var declarationOwner = GetSourceOwner(sourceRoot, sourceLink.DeclarationPath);
            var sourceOwner = GetSourceOwner(sourceRoot, sourceLink.SourcePath);
            if (!string.Equals(declarationOwner, sourceOwner, StringComparison.OrdinalIgnoreCase))
            {
                var relative = "src/" + Path.GetRelativePath(sourceRoot, sourceLink.SourcePath).Replace('\\', '/');
                if (reviewedPublicLinks.Contains(relative)) continue; // Current role-local shared algorithms and policy constants.
                if (declarationOwner == "Sunder.Agent.Execution.Common")
                {
                    Assert.Contains(relative, ExecutionCommonDomainSources);
                    continue;
                }
                Assert.Contains(sourceOwner, AllowedCrossFamilySourceOwners);
                Assert.DoesNotMatch(PublicTypeDeclarationPattern, File.ReadAllText(sourceLink.SourcePath));
            }
        }
    }

    [Fact]
    public void FriendAssemblies_MatchReviewedAllowlist()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(AgentPackageRepositoryInventory.IsSourceFile))
        {
            var projectName = Path.GetRelativePath(sourceRoot, sourceFile)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            foreach (Match match in InternalsVisibleToPattern.Matches(File.ReadAllText(sourceFile)))
            {
                actual.Add(projectName + " -> " + match.Groups["assembly"].Value);
            }
        }

        Assert.True(
            actual.SetEquals(AllowedFriendAssemblies),
            $"Friend assembly declarations must match the reviewed allowlist.{Environment.NewLine}Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actual.Order())}");
    }

    [Fact]
    public void AvaloniaProjects_UseReviewedSharedPresentationSourcesAndCompiledBindings()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var packageDefaults = XDocument.Load(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "Sunder.Agent.Worker.props"));
        var defaultBindings = Assert.Single(packageDefaults.Descendants("AvaloniaUseCompiledBindingsByDefault")).Value;
        var providerSettings = XDocument.Load(Path.Combine(sourceRoot, "Sunder.Package.Agent.Provider.Shared/ProviderSettingsSources.props"));
        Assert.Contains(providerSettings.Descendants("AvaloniaResource"), item => item.Attribute("Include")?.Value.Replace('\\', '/')
            .EndsWith("Sunder.Package.Agent.Shared/Presentation/PresentationStyles.axaml", StringComparison.Ordinal) == true);
        var avaloniaProjects = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(projectPath => GetReferenceIncludes(projectPath, "PackageReference")
                .Contains("Avalonia", StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(avaloniaProjects);
        foreach (var projectPath in avaloniaProjects)
        {
            var project = XDocument.Load(projectPath);
            var sharedImports = project.Descendants("Import")
                .Select(static element => element.Attribute("Project")?.Value?.Replace('\\', '/'))
                .Where(static path => path?.EndsWith(
                    "Sunder.Package.Agent.Shared/Sunder.Package.Agent.Shared.props",
                    StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            var directSharedItems = project.Descendants()
                .Where(static element => element.Name.LocalName is "Compile" or "AvaloniaResource")
                .Select(static element => element.Attribute("Include")?.Value)
                .Where(static include => include?.Replace('\\', '/').Contains("Sunder.Package.Agent.Shared/Presentation/", StringComparison.OrdinalIgnoreCase) == true
                    || include?.Replace('\\', '/').Contains("Sunder.Package.Agent.Shared/PackageViews/", StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            var compiledBindings = project.Descendants("AvaloniaUseCompiledBindingsByDefault")
                .Select(static element => element.Value)
                .SingleOrDefault();
            if (compiledBindings is null)
            {
                Assert.Contains(project.Descendants("Import"), item => item.Attribute("Project")?.Value.Replace('\\', '/')
                    .EndsWith("/Sunder.Agent.Worker.props", StringComparison.Ordinal) == true);
                compiledBindings = defaultBindings;
            }

            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            if (NarrowProviderAppProjects.Contains(projectName))
            {
                Assert.Empty(sharedImports);
                Assert.Single(project.Descendants("Import"), item => item.Attribute("Project")?.Value.Replace('\\', '/')
                    .EndsWith("Sunder.Package.Agent.Provider.Shared/ProviderSettingsSources.props", StringComparison.Ordinal) == true);
            }
            else
            {
                Assert.Single(sharedImports);
                if (projectName is "Sunder.Package.Agent" or "Sunder.Package.Agent.Subagents")
                {
                    Assert.All(directSharedItems, include => Assert.EndsWith(
                        "Sunder.Package.Agent.Shared/Presentation/ModelBindingStyles.axaml",
                        include!.Replace('\\', '/'),
                        StringComparison.Ordinal));
                }
                else
                {
                    Assert.Empty(directSharedItems);
                }
            }
            Assert.Equal("true", compiledBindings, ignoreCase: true);
        }
    }

    [Fact]
    public void AgentContracts_RemainFreeOfUiPersistenceProcessAndHostDependencies()
    {
        foreach (var sourceFile in CurrentDomainSources())
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotMatch(ProhibitedContractNamespacePattern, source);
        }
    }

    [Fact]
    public void AgentContracts_DoNotReferenceBaseAgentImplementationNamespaces()
    {
        var sourceFiles = CurrentDomainSources();

        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            var text = File.ReadAllText(sourceFile).Replace('/', '\\');
            Assert.DoesNotContain("Sunder.Package.Agent\\Sunder.Package.Agent.csproj", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Agent.PackageViews", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Agent.Services", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Agent.Storage", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AgentContracts_PublicApiDoesNotExposeSourceLinkedSharedTypes()
    {
        var contractsAssembly = typeof(Sunder.Package.Agent.Contracts.Contracts.IAgentChatProvider).Assembly;
        var prohibitedNamespaces = new[]
        {
            "Sunder.Agent.Execution.Common",
            "Sunder.Package.Agent.Shared",
            "Sunder.Package.Agent.Provider.Shared",
        };

        var exposedTypes = contractsAssembly.ExportedTypes.Where(type => type.Namespace?.StartsWith("Sunder.Package.Agent.Contracts", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(GetSignatureTypes)
                .Append(type))
            .SelectMany(FlattenType)
            .Where(type => type.Namespace is not null)
            .Where(type => prohibitedNamespaces.Any(prefix =>
                type.Namespace!.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct()
            .ToArray();

        Assert.Empty(exposedTypes);
    }

    [Fact]
    public void SunderPackages_DoNotReferenceHostImplementationProjectsOrNamespaces()
    {
        var packagesRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var sourceFiles = EnumerateSourceFiles(packagesRoot, [".cs", ".csproj"]);

        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            var text = File.ReadAllText(sourceFile).Replace('/', '\\');
            Assert.DoesNotContain("Sunder.App", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Runtime.Host", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Host\\Sunder.App", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Host\\Sunder.Runtime.Host", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PackageProjects_DoNotKeepSourceManifests()
    {
        var packagesRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var manifestPaths = Directory.EnumerateFiles(packagesRoot, "sunder-package.json", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(manifestPaths);
    }

    [Fact]
    public void SchemaOwnedSettings_DoNotUseOpaquePackageState()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var settingsOwners = new[]
        {
            "Sunder.Package.Agent.Execution.Docker/App/DockerExecutionSettingsViewModel.cs",
            "Sunder.Package.Agent.Execution.Local/App/LocalExecutionSettingsViewModel.cs",
            "Sunder.Package.Agent.Provider.LMStudio/App/LMStudioSettingsViewModel.cs",
            "Sunder.Package.Agent.Provider.OpenAI/OpenAiSettingsViewModel.cs",
            "Sunder.Package.Agent.Provider.Shared/UtilityModelSettingsState.cs",
        };

        foreach (var relativePath in settingsOwners)
        {
            var source = File.ReadAllText(Path.Combine(sourceRoot, NormalizePath(relativePath)));
            Assert.DoesNotContain("Storage.State", source, StringComparison.Ordinal);
        }

        foreach (var relativePath in settingsOwners.Skip(2))
        {
            var source = File.ReadAllText(Path.Combine(sourceRoot, NormalizePath(relativePath)));
            Assert.Contains(".Settings", source, StringComparison.Ordinal);
        }

        var allSource = string.Join('\n', EnumerateSourceFiles(sourceRoot, [".cs"]).Select(File.ReadAllText));
        Assert.DoesNotContain("IPackageConfiguration", allSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimePackageFamilies_DeclarePackageMetadataInCode()
    {
        var packages = AgentPackageRepositoryInventory.GetRuntimePackageProjects();

        Assert.NotEmpty(packages);
        foreach (var package in packages)
        {
            Assert.True(
                File.Exists(package.MetadataPath),
                $"Runtime package family '{package.FamilyId}' must declare PackageMetadata.cs.");
            Assert.Contains("SunderPackage(", File.ReadAllText(package.MetadataPath), StringComparison.Ordinal);
        }
    }

    private static string[] CurrentDomainSources() => Directory.EnumerateFiles(
            Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src/Sunder.Package.Agent"), "*.cs", SearchOption.AllDirectories)
        .Where(AgentPackageRepositoryInventory.IsSourceFile)
        .Where(path => Regex.IsMatch(File.ReadAllText(path), @"^namespace Sunder\.Package\.Agent\.(?:Contracts(?:\.[\w.]+)?|Protocol);", RegexOptions.Multiline | RegexOptions.CultureInvariant))
        .ToArray();

    private static readonly Regex PublicTypeDeclarationPattern = new(
        @"^\s*public\s+(?:(?:abstract|sealed|static|partial|readonly|ref)\s+)*(?:class|record|struct|interface|enum|delegate)\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex InternalsVisibleToPattern = new(
        "\\[assembly:\\s*InternalsVisibleTo\\(\"(?<assembly>[^\"]+)\"\\)\\]",
        RegexOptions.CultureInvariant);

    private static readonly Regex ProhibitedContractNamespacePattern = new(
        @"\b(?:Avalonia|Microsoft\.Data|Microsoft\.EntityFrameworkCore|Microsoft\.Extensions\.Hosting|Sunder\.App|Sunder\.Runtime\.Host)\b|^\s*using\s+(?:System\.Data|System\.Diagnostics)\s*;|\bSystem\.Diagnostics\.Process\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly IReadOnlySet<string> ProhibitedContractReferenceTokens = new HashSet<string>(
        ["Avalonia", "Dapper", "EntityFramework", "Hosting", "Sqlite", "Sunder.App", "Sunder.Runtime.Host"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> NarrowProviderAppProjects = new HashSet<string>(
        [
            "Sunder.Package.Agent.Provider.Anthropic",
            "Sunder.Package.Agent.Provider.Gemini",
            "Sunder.Package.Agent.Provider.LMStudio",
            "Sunder.Package.Agent.Provider.OpenAI",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> AllowedCrossFamilySourceOwners = new HashSet<string>(
        [
            "Sunder.Package.Agent.Provider.Shared",
            "Sunder.Package.Agent.Shared",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> ExecutionCommonDomainSources = new HashSet<string>(StringComparer.Ordinal)
    {
        "src/Sunder.Package.Agent/Models/AgentWorkspaceRecord.cs",
        "src/Sunder.Package.Agent/Models/Execution/AgentWorkspaceBindingRecord.cs",
        "src/Sunder.Package.Agent/Models/Execution/AgentExecutionTargetContext.cs",
        "src/Sunder.Package.Agent/Models/Execution/AgentExecutionTargetDescriptor.cs",
        "src/Sunder.Package.Agent/Models/Execution/AgentFileSearchModels.cs",
        "src/Sunder.Package.Agent/Models/Execution/AgentScopedInstructionDiscoveryModels.cs",
        "src/Sunder.Package.Agent/Models/Tools/AgentResourceClaim.cs",
        "src/Sunder.Package.Agent/Models/Tools/AgentResolvedResource.cs",
        "src/Sunder.Package.Agent/Models/Tools/AgentFileReadRequest.cs",
        "src/Sunder.Package.Agent/Models/Tools/AgentFileReadResult.cs",
        "src/Sunder.Package.Agent/Models/Tools/AgentFileWriteRequest.cs",
        "src/Sunder.Package.Agent/Models/Tools/AgentFileDeleteRequest.cs",
        "src/Sunder.Package.Agent/Models/Tools/AgentFileMutationResult.cs",
    };

    private static readonly IReadOnlySet<string> AllowedFriendAssemblies = new HashSet<string>(
        [
            "Sunder.Package.Agent -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent -> Sunder.Package.Agent.Presentation.State.Tests",
            "Sunder.Package.Agent -> Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Builder -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Builder -> Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Execution.Docker -> Sunder.Package.Agent.Execution.Local.Tests",
            "Sunder.Package.Agent.Execution.Docker -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Execution.Local -> Sunder.Package.Agent.Execution.Local.Tests",
            "Sunder.Package.Agent.Execution.Local -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Mcp -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Mcp -> Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Memory.Semantic -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Memory.Semantic -> Sunder.Package.Agent.Memory.Semantic.Tests",
            "Sunder.Package.Agent.Memory.Semantic -> Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Provider.Anthropic -> Sunder.Package.Agent.Provider.Anthropic.Tests",
            "Sunder.Package.Agent.Provider.Anthropic -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Provider.Gemini -> Sunder.Package.Agent.Provider.Gemini.Tests",
            "Sunder.Package.Agent.Provider.Gemini -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Provider.LMStudio -> Sunder.Package.Agent.Provider.LMStudio.Tests",
            "Sunder.Package.Agent.Provider.LMStudio -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Provider.OpenAI -> Sunder.Package.Agent.Provider.OpenAI.Tests",
            "Sunder.Package.Agent.Provider.OpenAI -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Provider.OpenAI -> Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Skills -> Sunder.Package.Agent.Skills.Tests",
            "Sunder.Package.Agent.Skills -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Skills -> Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Subagents -> Sunder.Package.Agent.Execution.Local.Tests",
            "Sunder.Package.Agent.Subagents -> Sunder.Package.Agent.Presentation.Tests",
            "Sunder.Package.Agent.Subagents -> Sunder.Package.Agent.Subagents.Tests",
            "Sunder.Package.Agent.Subagents -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Tools.Files -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Tools.Files -> Sunder.Package.Agent.Execution.Local.Tests",
            "Sunder.Package.Agent.Tools.Web -> Sunder.Package.Agent.Tests",
        ],
        StringComparer.Ordinal);

    private static string[] GetExtensionProjectPaths()
        => AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .Where(static package => !string.Equals(package.Name, "Sunder.Package.Agent", StringComparison.OrdinalIgnoreCase))
            .Select(static package => package.ProjectPath)
            .ToArray();

    private static string[] GetProjectReferencePaths(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        return GetReferenceIncludes(projectPath, "ProjectReference")
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, NormalizePath(include))))
            .ToArray();
    }

    private static string[] GetReferenceIncludes(string projectPath, string elementName)
        => XDocument.Load(projectPath)
            .Descendants(elementName)
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => include!)
            .ToArray();

    private static IEnumerable<string> GetSourceLinkedCompileFiles(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var includes = GetReferenceIncludes(projectPath, "Compile")
            .SelectMany(static include => include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(static include => include.StartsWith("..", StringComparison.Ordinal)
                                     || include.Contains("$(MSBuildThisFileDirectory)", StringComparison.Ordinal));

        foreach (var include in includes)
        {
            var expandedInclude = include.Replace(
                "$(MSBuildThisFileDirectory)",
                projectDirectory + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
            var normalizedInclude = NormalizePath(expandedInclude);
            var sourcePattern = Path.GetFullPath(Path.IsPathRooted(normalizedInclude)
                ? normalizedInclude
                : Path.Combine(projectDirectory, normalizedInclude));
            if (!sourcePattern.Contains('*', StringComparison.Ordinal))
            {
                if (File.Exists(sourcePattern))
                {
                    yield return sourcePattern;
                }

                continue;
            }

            var wildcardIndex = sourcePattern.IndexOf('*', StringComparison.Ordinal);
            var separatorIndex = sourcePattern.LastIndexOf(Path.DirectorySeparatorChar, wildcardIndex);
            var directory = sourcePattern[..separatorIndex];
            var pattern = sourcePattern[(separatorIndex + 1)..];
            var searchOption = SearchOption.TopDirectoryOnly;
            var recursivePrefix = "**" + Path.DirectorySeparatorChar;
            if (pattern.StartsWith(recursivePrefix, StringComparison.Ordinal))
            {
                pattern = pattern[recursivePrefix.Length..];
                searchOption = SearchOption.AllDirectories;
            }
            if (!Directory.Exists(directory))
            {
                continue;
            }
            foreach (var sourceFile in Directory.EnumerateFiles(directory, pattern, searchOption))
            {
                yield return Path.GetFullPath(sourceFile);
            }
        }
    }

    private static string GetSourceOwner(string sourceRoot, string path)
        => Path.GetRelativePath(sourceRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

    private static string NormalizeRepositoryPath(string path)
        => Path.GetRelativePath(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path).Replace('\\', '/');

    private static string NormalizePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string[] EnumerateSourceFiles(string root, IReadOnlyCollection<string> extensions)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<Type> GetSignatureTypes(MemberInfo member)
        => member switch
        {
            MethodInfo method => method.GetParameters().Select(static parameter => parameter.ParameterType)
                .Append(method.ReturnType),
            ConstructorInfo constructor => constructor.GetParameters()
                .Select(static parameter => parameter.ParameterType),
            PropertyInfo property => [property.PropertyType],
            FieldInfo field => [field.FieldType],
            EventInfo eventInfo when eventInfo.EventHandlerType is not null => [eventInfo.EventHandlerType],
            _ => [],
        };

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nested in FlattenType(elementType))
            {
                yield return nested;
            }
        }
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in FlattenType(argument))
            {
                yield return nested;
            }
        }
    }

    private sealed record SourceLink(string DeclarationPath, string SourcePath);
}
