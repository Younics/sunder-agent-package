using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

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
        services.AddSingleton<Sunder.Sdk.Runtime.IPackageRuntimeClient>(
            Sunder.Sdk.Runtime.NullPackageRuntimeClient.Instance);

        new AppPackageModule().ConfigureAppServices(services, packageScope.Context);
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
        var contractsAssembly = typeof(Sunder.Package.Agent.Contracts.PackageExtensionPoints).Assembly;
        Assert.DoesNotContain(
            contractsAssembly.ExportedTypes,
            type => type.Name.Contains("HistorySearch", StringComparison.Ordinal));

        var contractsRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts");
        Assert.DoesNotContain(
            Directory.EnumerateFiles(contractsRoot, "*.cs", SearchOption.AllDirectories)
                .Where(AgentPackageRepositoryInventory.IsSourceFile),
            sourceFile => File.ReadAllText(sourceFile).Contains("HistorySearch", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentDatabase_IsOpenedOnlyByRuntimeComposition()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var databasePath = packageScope.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        Assert.False(File.Exists(databasePath));

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IPackageContext>(packageScope.Context);
        services.AddSingleton<IPackageExtensionCatalog>(new RegressionTestExtensionCatalog());
        services.AddSingleton<IBackgroundProcessQueue, CompositionBackgroundProcessQueue>();
        new PackageModule().ConfigureRuntimeServices(services, packageScope.Context);

        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public void AgentAppModule_DoesNotRegisterRuntimeServicesOrExtensions()
    {
        var source = File.ReadAllText(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src", "Sunder.Package.Agent", "PackageModule.cs"));
        var appModuleSource = source[(source.IndexOf("public sealed class AppPackageModule", StringComparison.Ordinal))..];

        Assert.DoesNotContain("AgentLocalStore", appModuleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterExtension", appModuleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureRuntimeServices", appModuleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void StackDependencyContributors_RequireOwnedExtensionCatalogs()
    {
        var contributorTypes = new[]
        {
            typeof(AgentProfileStackContributor),
            typeof(AgentWorkspaceStackContributor),
            typeof(SubagentStackContributor),
        };

        foreach (var contributorType in contributorTypes)
        {
            var parameter = Assert.Single(
                Assert.Single(contributorType.GetConstructors()).GetParameters(),
                candidate => candidate.ParameterType == typeof(IPackageExtensionCatalog));
            Assert.False(parameter.IsOptional);
            Assert.False(parameter.HasDefaultValue);
        }
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
    public void AgentExtensionPackages_ReferenceAgentContractsProject()
    {
        var contractsProjectPath = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "Sunder.Package.Agent.Contracts.csproj");
        var extensionProjects = GetExtensionProjectPaths();

        Assert.NotEmpty(extensionProjects);
        foreach (var projectPath in extensionProjects)
        {
            Assert.Contains(contractsProjectPath, GetProjectReferencePaths(projectPath), StringComparer.OrdinalIgnoreCase);
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
        var sourceLinkDeclarations = sourceLinks
            .Select(static link => NormalizeRepositoryPath(link.DeclarationPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(
            sourceLinkDeclarations.SetEquals(AllowedProductionSourceLinkDeclarations),
            "Production source-link declarations must exactly match the reviewed allowlist.");
        foreach (var sourceLink in sourceLinks)
        {
            var declarationPath = NormalizeRepositoryPath(sourceLink.DeclarationPath);
            Assert.Contains(declarationPath, AllowedProductionSourceLinkDeclarations);
            Assert.True(
                sourceLink.SourcePath.StartsWith(sourceRootPrefix, StringComparison.OrdinalIgnoreCase),
                $"Source link '{sourceLink.SourcePath}' must stay under the repository src directory.");

            var source = File.ReadAllText(sourceLink.SourcePath);
            Assert.DoesNotMatch(PublicTypeDeclarationPattern, source);
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
    public void AvaloniaProjects_UseCentralSharedPresentationPropsAndCompiledBindings()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
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
                .Where(static include => include?.Contains("Sunder.Package.Agent.Shared", StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            var compiledBindings = project.Descendants("AvaloniaUseCompiledBindingsByDefault")
                .Select(static element => element.Value)
                .SingleOrDefault();

            Assert.Single(sharedImports);
            Assert.Empty(directSharedItems);
            Assert.Equal("true", compiledBindings, ignoreCase: true);
        }
    }

    [Fact]
    public void AgentContracts_RemainFreeOfUiPersistenceProcessAndHostDependencies()
    {
        var contractsRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts");
        var projectPath = Path.Combine(contractsRoot, "Sunder.Package.Agent.Contracts.csproj");

        var references = GetReferenceIncludes(projectPath, "PackageReference")
            .Concat(GetReferenceIncludes(projectPath, "ProjectReference"));
        Assert.DoesNotContain(references, reference => ProhibitedContractReferenceTokens.Any(
            token => reference.Contains(token, StringComparison.OrdinalIgnoreCase)));
        foreach (var sourceFile in Directory.EnumerateFiles(contractsRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(AgentPackageRepositoryInventory.IsSourceFile))
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotMatch(ProhibitedContractNamespacePattern, source);
        }
    }

    [Fact]
    public void AgentContracts_DoNotReferenceBaseAgentImplementationNamespaces()
    {
        var contractsRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src", "Sunder.Package.Agent.Contracts");
        var sourceFiles = Directory.EnumerateFiles(contractsRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj")
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        var contractsAssembly = typeof(Sunder.Package.Agent.Contracts.PackageExtensionPoints).Assembly;
        var prohibitedNamespaces = new[]
        {
            "Sunder.Agent.Execution.Common",
            "Sunder.Package.Agent.Shared",
            "Sunder.Package.Agent.Provider.Shared",
        };

        var exposedTypes = contractsAssembly.ExportedTypes
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
            "Sunder.Package.Agent.Execution.Docker/DockerExecutionSettingsViewModel.cs",
            "Sunder.Package.Agent.Execution.Local/LocalExecutionSettingsViewModel.cs",
            "Sunder.Package.Agent.Provider.LMStudio/LMStudioSettingsViewModel.cs",
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
    public void PackageProjects_DeclarePackageMetadataInCode()
    {
        var packagesRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var packageProjects = Directory.EnumerateFiles(packagesRoot, "Sunder.Package.*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase))
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(packageProjects);
        foreach (var projectPath in packageProjects)
        {
            var metadataPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "PackageMetadata.cs");
            Assert.True(File.Exists(metadataPath), $"Package project '{projectPath}' must declare PackageMetadata.cs.");
            Assert.Contains("SunderPackage(", File.ReadAllText(metadataPath), StringComparison.Ordinal);
        }
    }

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

    private static readonly IReadOnlySet<string> AllowedProductionSourceLinkDeclarations = new HashSet<string>(
        [
            "src/Sunder.Package.Agent.Provider.Shared/Sunder.Package.Agent.Provider.Shared.props",
            "src/Sunder.Package.Agent.Shared/Sunder.Package.Agent.Shared.props",
            "src/Sunder.Package.Agent.Tools.Files/Sunder.Package.Agent.Tools.Files.csproj",
            "src/Sunder.Package.Agent.Tools.Shell/Sunder.Package.Agent.Tools.Shell.csproj",
            "src/Sunder.Package.Agent.Tools.Web/Sunder.Package.Agent.Tools.Web.csproj",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> AllowedFriendAssemblies = new HashSet<string>(
        [
            "Sunder.Package.Agent -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Contracts -> Sunder.Package.Agent",
            "Sunder.Package.Agent.Contracts -> Sunder.Package.Agent.Subagents",
            "Sunder.Package.Agent.Execution.Docker -> Sunder.Package.Agent.Execution.Local.Tests",
            "Sunder.Package.Agent.Execution.Docker -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Execution.Local -> Sunder.Package.Agent.Execution.Local.Tests",
            "Sunder.Package.Agent.Execution.Local -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Mcp -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Memory.Semantic -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Provider.Anthropic -> Sunder.Package.Agent.Provider.Anthropic.Tests",
            "Sunder.Package.Agent.Provider.Gemini -> Sunder.Package.Agent.Provider.Gemini.Tests",
            "Sunder.Package.Agent.Provider.LMStudio -> Sunder.Package.Agent.Provider.LMStudio.Tests",
            "Sunder.Package.Agent.Provider.OpenAI -> Sunder.Package.Agent.Provider.OpenAI.Tests",
            "Sunder.Package.Agent.Skills -> Sunder.Package.Agent.Skills.Tests",
            "Sunder.Package.Agent.Skills -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Subagents -> Sunder.Package.Agent.Execution.Local.Tests",
            "Sunder.Package.Agent.Subagents -> Sunder.Package.Agent.Tests",
            "Sunder.Package.Agent.Tools.Files -> Sunder.Package.Agent.Tests",
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

            var directory = Path.GetDirectoryName(sourcePattern)!;
            var pattern = Path.GetFileName(sourcePattern);
            foreach (var sourceFile in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                yield return Path.GetFullPath(sourceFile);
            }
        }
    }

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
