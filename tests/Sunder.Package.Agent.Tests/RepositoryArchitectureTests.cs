using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sunder.Package.Agent.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class RepositoryArchitectureTests
{
    [Fact]
    public void ProductionProjectReferences_FollowSharedAssemblyDirection()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var allowedSharedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sunder.Agent.Execution.Common",
            "Sunder.Package.Agent.Contracts",
        };
        var executionCommonConsumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sunder.Package.Agent.Execution.Docker",
            "Sunder.Package.Agent.Execution.Local",
            "Sunder.Package.Agent.Tools.Files",
            "Sunder.Package.Agent.Tools.Shell",
            "Sunder.Package.Agent.Tools.Web",
        };

        foreach (var projectPath in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var referencedProductionProjects = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(static reference => reference.Attribute("Include")?.Value)
                .Where(static include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(projectPath)!,
                    NormalizePath(include!))))
                .Where(path => path.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(static path => Path.GetFileNameWithoutExtension(path)!)
                .ToArray();

            Assert.DoesNotContain(referencedProductionProjects, referenced => !allowedSharedProjects.Contains(referenced));
            if (!executionCommonConsumers.Contains(projectName))
            {
                Assert.DoesNotContain("Sunder.Agent.Execution.Common", referencedProductionProjects, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ContractSources_AreGroupedByOwnedDomainWithoutChangingShippedNamespaces()
    {
        var contractsRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts");
        var allowedFolders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Execution",
            "Extensions",
            "Models",
            "Providers",
            "Runtime",
            "Tools",
        };
        var sourceFiles = Directory.EnumerateFiles(contractsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .ToArray();

        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(contractsRoot, sourceFile);
            var topLevelFolder = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            Assert.Contains(topLevelFolder, allowedFolders);
        }

        foreach (var folder in allowedFolders)
        {
            Assert.Contains(sourceFiles, path => string.Equals(
                Path.GetRelativePath(contractsRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0],
                folder,
                StringComparison.Ordinal));
        }

        var publicNamespaces = typeof(PackageExtensionPoints).Assembly.ExportedTypes
            .Select(static type => type.Namespace)
            .Where(static value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(publicNamespaces, namespaceName => namespaceName is not
            ("Sunder.Package.Agent.Contracts"
            or "Sunder.Package.Agent.Contracts.Contracts"
            or "Sunder.Package.Agent.Contracts.Models"
            or "Sunder.Package.Agent.Contracts.Services"));
    }

    [Fact]
    public void PackageModuleTypes_ExposeOnlyTheirDeclaredRole()
    {
        foreach (var package in AgentPackageRepositoryInventory.GetRuntimePackageProjects())
        {
            var moduleTypes = Assembly.Load(package.Name).GetTypes()
                .Where(static type => !type.IsAbstract)
                .Where(static type => typeof(ISunderRuntimePackageModule).IsAssignableFrom(type)
                                      || typeof(ISunderAppPackageModule).IsAssignableFrom(type))
                .ToArray();

            Assert.NotEmpty(moduleTypes);
            Assert.True(moduleTypes.Count(typeof(ISunderRuntimePackageModule).IsAssignableFrom) <= 1);
            Assert.True(moduleTypes.Count(typeof(ISunderAppPackageModule).IsAssignableFrom) <= 1);
            foreach (var moduleType in moduleTypes)
            {
                Assert.False(
                    typeof(ISunderRuntimePackageModule).IsAssignableFrom(moduleType)
                    && typeof(ISunderAppPackageModule).IsAssignableFrom(moduleType),
                    $"Package module '{moduleType.FullName}' must own exactly one host role.");

                var declaredMethodNames = moduleType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Select(static method => method.Name)
                    .ToArray();
                if (typeof(ISunderRuntimePackageModule).IsAssignableFrom(moduleType))
                {
                    Assert.DoesNotContain("ConfigureAppServices", declaredMethodNames);
                    Assert.DoesNotContain("RegisterAppContributions", declaredMethodNames);
                }
                else
                {
                    Assert.DoesNotContain("ConfigureRuntimeServices", declaredMethodNames);
                    Assert.DoesNotContain("RegisterRuntimeContributions", declaredMethodNames);
                }
            }
        }
    }

    [Fact]
    public void RepositoryBuildAndPackagePolicy_IsCentralAndConsistent()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var buildProperties = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props"));
        Assert.Equal("enable", GetSingleProperty(buildProperties, "Nullable"));
        Assert.Equal("true", GetSingleProperty(buildProperties, "TreatWarningsAsErrors"));
        Assert.Equal("true", GetSingleProperty(buildProperties, "Deterministic"));
        Assert.Equal("true", GetSingleProperty(buildProperties, "EnableNETAnalyzers"));
        Assert.Equal("1.1.0", GetSingleProperty(buildProperties, "VersionPrefix"));
        Assert.Equal("GPL-3.0-only", GetSingleProperty(buildProperties, "PackageLicenseExpression"));

        var packageProperties = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
        Assert.Equal("true", GetSingleProperty(packageProperties, "ManagePackageVersionsCentrally"));
        Assert.Equal("false", GetSingleProperty(packageProperties, "CentralPackageVersionOverrideEnabled"));
        var sunderPackageVersions = packageProperties.Descendants("PackageVersion")
            .Where(reference => reference.Attribute("Include")?.Value.StartsWith("Sunder.", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(4, sunderPackageVersions.Length);
        Assert.All(sunderPackageVersions, reference => Assert.Equal(
            "[1.1.0,1.2.0)",
            reference.Attribute("Version")?.Value));

        foreach (var package in AgentPackageRepositoryInventory.GetRuntimePackageProjects()
                     .Where(static package => !string.Equals(package.Name, "Sunder.Package.Agent", StringComparison.Ordinal)))
        {
            Assert.Matches(BaseAgentV1DependencyPattern(), File.ReadAllText(package.MetadataPath));
        }

        var editorConfig = File.ReadAllText(Path.Combine(repositoryRoot, ".editorconfig"));
        Assert.Contains("[**/obj/**/*.cs]", editorConfig, StringComparison.Ordinal);
        Assert.Contains("[**/*.g.cs]", editorConfig, StringComparison.Ordinal);
        Assert.Contains("generated_code = true", editorConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionPackages_DoNotOwnNetworkCallbackListeners()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .ToArray();

        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("HttpListener", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TcpListener", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WebApplication.CreateBuilder", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Socket.Listen", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OpenAiInteractiveAuth_IsHostRoutedOnly()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src", "Sunder.Package.Agent.Provider.OpenAI");
        var source = string.Join('\n', Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(File.ReadAllText));

        Assert.Contains("IPackageAuthHandler", source, StringComparison.Ordinal);
        Assert.Contains("IPackageCallbackHandler", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SignInWithBrowserAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CodexAuthCallbackServer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationCode_DoesNotUseUntrackedDispatcherPostsOrNonEventAsyncVoid()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var asyncVoidPattern = new Regex(
            @"async\s+void\s+(?<name>\w+)\s*\(",
            RegexOptions.CultureInvariant);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(AgentPackageRepositoryInventory.IsSourceFile))
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("Dispatcher.UIThread.Post", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_uiDispatcher.Post", source, StringComparison.Ordinal);
            foreach (Match match in asyncVoidPattern.Matches(source))
            {
                Assert.StartsWith("On", match.Groups["name"].Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AgentHotspots_ArePartitionedByExplicitResponsibility()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var requiredFiles = new[]
        {
            "Sunder.Package.Agent/Services/AgentSessionService.Projections.cs",
            "Sunder.Package.Agent/Services/AgentSessionService.Mutations.cs",
            "Sunder.Package.Agent/Services/BehaviorLoops/AgentBehaviorLoopHost.ToolBatch.cs",
            "Sunder.Package.Agent/Services/BehaviorLoops/AgentBehaviorLoopHost.ToolExecution.cs",
            "Sunder.Package.Agent/Services/BehaviorLoops/AgentPermissionSuspensionCoordinator.cs",
            "Sunder.Package.Agent/Storage/AgentLocalStore.Permissions.Queries.cs",
            "Sunder.Package.Agent/Storage/AgentLocalStore.Permissions.Writes.cs",
            "Sunder.Package.Agent/Storage/AgentLocalStore.PermissionRecovery.cs",
            "Sunder.Package.Agent.Shared/PackageViews/TranscriptScrollCoordinator.Anchors.cs",
            "Sunder.Package.Agent.Shared/PackageViews/TranscriptScrollCoordinator.Paging.cs",
            "Sunder.Package.Agent.Shared/PackageViews/TranscriptScrollCoordinator.Lifetime.cs",
            "Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Editor.cs",
            "Sunder.Package.Agent.Subagents/PackageViews/SubsessionsViewModel.Transcript.cs",
            "Sunder.Package.Agent.Mcp/AgentMcpSettingsViewModel.DocumentLoading.cs",
        };

        Assert.All(requiredFiles, relativePath => Assert.True(
            File.Exists(Path.Combine(sourceRoot, NormalizePath(relativePath))),
            $"Missing responsibility boundary '{relativePath}'."));

        var hostSource = File.ReadAllText(Path.Combine(
            sourceRoot,
            "Sunder.Package.Agent",
            "Services",
            "BehaviorLoops",
            "AgentBehaviorLoopHost.cs"));
        Assert.Contains("AgentToolBatchCoordinator", hostSource, StringComparison.Ordinal);
        Assert.Contains("AgentPermissionSuspensionCoordinator", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteToolBatchAsync(", hostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SubagentAppGateway_UsesOnlyNarrowAsyncSubsessionPorts()
    {
        var source = File.ReadAllText(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Subagents",
            "Runtime",
            "SubagentRuntimeBridge.cs"));

        Assert.Contains("ISubsessionSessionReader", source, StringComparison.Ordinal);
        Assert.Contains("ISubsessionCheckpointReader", source, StringComparison.Ordinal);
        Assert.Contains("ISubsessionTranscriptPageReader", source, StringComparison.Ordinal);
        Assert.Contains("ISubsessionChangeNotifications", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SubagentAppRuntimeGateway : ISubagentManagementGateway, IAgentRuntimeCatalog",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;", source, StringComparison.Ordinal);
    }

    private static string GetSingleProperty(XDocument document, string propertyName)
        => Assert.Single(document.Descendants(propertyName)).Value;

    private static string NormalizePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    [GeneratedRegex("SunderPackageDependency\\s*\\(\\s*PackageId\\s*=\\s*\"sunder\\.package\\.agent\"\\s*,\\s*VersionRange\\s*=\\s*\">=1\\.1\\.0 <1\\.2\\.0\"", RegexOptions.CultureInvariant)]
    private static partial Regex BaseAgentV1DependencyPattern();
}
