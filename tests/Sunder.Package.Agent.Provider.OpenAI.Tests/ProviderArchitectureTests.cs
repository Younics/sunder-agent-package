using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed partial class ProviderArchitectureTests
{
    [Fact]
    public void ProviderSharedSource_DeclaresNoPublicTypes()
    {
        var sharedDirectory = Path.Combine(GetRepositoryRoot(), "src", "Sunder.Package.Agent.Provider.Shared");
        var violations = Directory.EnumerateFiles(sharedDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, lineNumber: index + 1)))
            .Where(item => PublicTypeDeclaration().IsMatch(item.line))
            .Select(item => $"{Path.GetFileName(item.path)}:{item.lineNumber}: {item.line.Trim()}")
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ProviderPackages_LinkTheSharedProviderSources()
    {
        var sourceDirectory = Path.Combine(GetRepositoryRoot(), "src");
        var providerDirectories = Directory.EnumerateDirectories(sourceDirectory, "Sunder.Package.Agent.Provider.*")
            .Where(path => !path.EndsWith(".Shared", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(providerDirectories);
        foreach (var providerDirectory in providerDirectories)
        {
            var sharedSources = Directory.EnumerateFiles(providerDirectory, "*.csproj", SearchOption.AllDirectories)
                .SelectMany(projectPath => XDocument.Load(projectPath).Descendants())
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => include?.Contains("Sunder.Package.Agent.Provider.Shared", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.NotEmpty(sharedSources);
        }
    }

    [Fact]
    public void ProviderApps_GeneratedRolePlansUseExactSharedSourceAllowlists()
    {
        var repositoryRoot = GetRepositoryRoot();
        var agentSharedRoot = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            "src",
            "Sunder.Package.Agent.Shared"));
        var agentSharedPrefix = agentSharedRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var expectedByProvider = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Anthropic"] = ProviderSettingsSharedSources,
            ["Gemini"] = ProviderSettingsSharedSources,
            ["LMStudio"] = ProviderSettingsSharedSources,
            ["OpenAI"] = ProviderSettingsSharedSources.Concat(
                [
                    "Presentation/PackageCallbackFlowRunner.cs",
                    "Presentation/PresentationOperationObserver.cs",
                ])
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
        };

        foreach (var provider in expectedByProvider)
        {
            var projectPath = Path.Combine(
                repositoryRoot,
                "src",
                $"Sunder.Package.Agent.Provider.{provider.Key}",
                $"Sunder.Package.Agent.Provider.{provider.Key}.csproj");
            var project = XDocument.Load(projectPath);
            var broadCompileIncludes = project.Descendants("Compile")
                .Select(static element => element.Attribute("Include")?.Value)
                .Where(static include => include is not null
                                         && include.Contains("Sunder.Package.Agent.Shared", StringComparison.OrdinalIgnoreCase)
                                         && include.Contains('*'))
                .ToArray();

            Assert.Empty(broadCompileIncludes);

            var projectRoot = Path.GetDirectoryName(projectPath)!;
            var configuration = typeof(ProviderArchitectureTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(projectRoot, "obj", configuration,
                "net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc/package-role-plan.v1.json")));
            Assert.True(plan.RootElement.GetProperty("hasApp").GetBoolean());
            var appCompile = plan.RootElement.GetProperty("appCompile").EnumerateArray().Select(item => item.GetProperty("path").GetString()!).ToArray();
            Assert.NotEmpty(appCompile);
            var duplicatePaths = appCompile
                .GroupBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.True(
                duplicatePaths.Length == 0,
                $"{provider.Key} App links duplicate Compile sources: [{string.Join(", ", duplicatePaths)}].");

            var actualSharedSources = plan.RootElement.GetProperty("types").EnumerateArray()
                .Where(type => type.GetProperty("roles").GetString() is "app" or "shared")
                .SelectMany(type => type.GetProperty("sources").EnumerateArray())
                .Select(source => Path.GetFullPath(source.GetString()!, projectRoot))
                .Where(path => path.StartsWith(agentSharedPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(agentSharedRoot, path).Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.True(
                provider.Value.SetEquals(actualSharedSources),
                $"{provider.Key} App shared Compile sources were [{string.Join(", ", actualSharedSources.Order(StringComparer.OrdinalIgnoreCase))}].");
        }
    }

    [Fact]
    public void ProviderSettingsViewModels_DoNotAccessRuntimeSecretsDirectly()
    {
        var sourceDirectory = Path.Combine(GetRepositoryRoot(), "src");
        var settingsViewModels = Directory.EnumerateDirectories(sourceDirectory, "Sunder.Package.Agent.Provider.*")
            .Where(path => !path.EndsWith(".Shared", StringComparison.Ordinal))
            .SelectMany(path => Directory.EnumerateFiles(path, "*SettingsViewModel.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, settingsViewModels.Length);
        foreach (var path in settingsViewModels)
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("ProviderCredentialAccessor", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".Secrets", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProviderAssemblies_PublicTypesStayWithinTheApprovedArchitectureSurface()
    {
        var expectedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Anthropic.AnthropicAgentProvider",
            "Anthropic.AnthropicCredentialProtocol",
            "Anthropic.AnthropicProviderConfiguration",
            "Anthropic.AnthropicSettingsView",
            "Anthropic.AnthropicSettingsViewModel",
            "Anthropic.AppPackageModule",
            "Gemini.GeminiAgentProvider",
            "Gemini.GeminiCredentialProtocol",
            "Gemini.GeminiEmbeddingProvider",
            "Gemini.GeminiProviderConfiguration",
            "Gemini.GeminiSettingsView",
            "Gemini.GeminiSettingsViewModel",
            "Gemini.AppPackageModule",
            "LMStudio.LMStudioAgentProvider",
            "LMStudio.LMStudioCredentialProtocol",
            "LMStudio.LMStudioEmbeddingProvider",
            "LMStudio.LMStudioProviderConfiguration",
            "LMStudio.LMStudioSettingsLoadState",
            "LMStudio.LMStudioSettingsView",
            "LMStudio.LMStudioSettingsViewModel",
            "LMStudio.AppPackageModule",
            "OpenAI.ApiKeyAuthStrategy",
            "OpenAI.AppPackageModule",
            "OpenAI.CodexConnectedAuthStrategy",
            "OpenAI.CodexConnectedTransport",
            "OpenAI.CodexResponseContinuationStore",
            "OpenAI.OpenAiAgentProvider",
            "OpenAI.OpenAiAuthModeOption",
            "OpenAI.OpenAiCodexSession",
            "OpenAI.OpenAiEmbeddingProvider",
            "OpenAI.OpenAiPackageAuthHandler",
            "OpenAI.OpenAiProviderConfiguration",
            "OpenAI.OpenAiSettingsView",
            "OpenAI.OpenAiSettingsViewModel",
        };
        var providerRoot = Path.Combine(GetRepositoryRoot(), "src");
        var actualTypes = Directory.EnumerateDirectories(providerRoot, "Sunder.Package.Agent.Provider.*")
            .Where(path => !path.EndsWith(".Shared", StringComparison.Ordinal))
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                               && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .SelectMany(file => File.ReadLines(file)
                    .Select(line => PublicTypeName().Match(line))
                    .Where(match => match.Success)
                    .Select(match => $"{Path.GetFileName(path)["Sunder.Package.Agent.Provider.".Length..]}.{match.Groups[1].Value}")))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            expectedTypes.SetEquals(actualTypes),
            $"Provider public type surface changed. Actual: [{string.Join(", ", actualTypes.Order())}]. "
            + "Update this architecture allowlist deliberately; it is not a binary API compatibility baseline.");
    }

    [Fact]
    public void VendorTestProjects_ReferenceOnlyTheirCanonicalRolesAndTestSupport()
    {
        var testsDirectory = Path.Combine(GetRepositoryRoot(), "tests");
        var testProjects = Directory.EnumerateDirectories(testsDirectory, "Sunder.Package.Agent.Provider.*.Tests")
            .SelectMany(path => Directory.EnumerateFiles(path, "*.csproj", SearchOption.TopDirectoryOnly))
            .ToArray();

        Assert.NotEmpty(testProjects);
        foreach (var projectPath in testProjects)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var vendor = projectName["Sunder.Package.Agent.Provider.".Length..^".Tests".Length];
            var expectedReferences = new HashSet<string>(StringComparer.Ordinal)
            {
                $"Sunder.Package.Agent.Provider.{vendor}",
                "Sunder.Package.Agent.Provider.TestSupport",
                "Sunder.Package.Agent.TestSupport",
                "Sunder.Package.Agent.TestSupport.Avalonia",
            };
            var references = XDocument.Load(projectPath).Descendants("ProjectReference").ToArray();
            var actualReferences = references
                .Select(element => (string)element.Attribute("Include")!)
                .Select(include => include.Replace('\\', Path.DirectorySeparatorChar))
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                expectedReferences.SetEquals(actualReferences),
                $"{projectName} references [{string.Join(", ", actualReferences.Order())}].");
            var roles = references.Where(reference => ((string)reference.Attribute("Include")!)
                .Replace('\\', '/').EndsWith($"/Sunder.Package.Agent.Provider.{vendor}.csproj", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, roles.Length);
            Assert.All(roles, role => Assert.Equal("GetSunderPackageRoleReference", (string?)role.Attribute("Targets")));
            var worker = Assert.Single(roles, role => (string?)role.Attribute("AdditionalProperties") == "SunderPackageReferenceRole=worker");
            var app = Assert.Single(roles, role => (string?)role.Attribute("AdditionalProperties") == "SunderPackageReferenceRole=app");
            Assert.Contains("global", ((string)worker.Attribute("Aliases")!).Split(','));
            Assert.DoesNotContain("global", ((string)app.Attribute("Aliases")!).Split(','));
        }
    }

    [Fact]
    public void ProviderPackages_DoNotReimplementSharedResponseAndLifecycleScaffolding()
    {
        var sourceDirectory = Path.Combine(GetRepositoryRoot(), "src");
        var providerFiles = Directory.EnumerateDirectories(sourceDirectory, "Sunder.Package.Agent.Provider.*")
            .Where(path => !path.EndsWith(".Shared", StringComparison.Ordinal))
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var forbiddenFragments = new[]
        {
            "new ChatResponseUpdate",
            "new UsageContent",
            "\"provider.request.start\"",
            "\"provider.stream.first_event\"",
            "\"provider.stream.canceled\"",
            "\"provider.stream.failed\"",
        };
        var violations = providerFiles
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, lineNumber: index + 1)))
            .Where(item => forbiddenFragments.Any(fragment => item.line.Contains(fragment, StringComparison.Ordinal)))
            .Select(item => $"{Path.GetRelativePath(sourceDirectory, item.path)}:{item.lineNumber}: {item.line.Trim()}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Provider response/lifecycle scaffolding belongs in Provider.Shared:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ProviderSharedSource_UsesCompositionRatherThanAProviderBaseClass()
    {
        var sharedDirectory = Path.Combine(GetRepositoryRoot(), "src", "Sunder.Package.Agent.Provider.Shared");
        var declarations = Directory.EnumerateFiles(sharedDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(File.ReadLines)
            .Where(line => line.Contains("abstract class", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(declarations);
    }

    private static string GetRepositoryRoot()
    {
        var startPaths = new[]
            {
                Environment.GetEnvironmentVariable("SUNDER_AGENT_REPOSITORY_ROOT"),
                AppContext.BaseDirectory,
                Environment.CurrentDirectory,
            }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var startPath in startPaths)
        {
            for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Sunder.AgentPackage.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Sunder Agent package repository root from SUNDER_AGENT_REPOSITORY_ROOT, the test output, or the working directory.");
    }

    private static readonly IReadOnlySet<string> ProviderSettingsSharedSources = new HashSet<string>(
        [
            "PackageViews/SunderComboBox.cs",
            "Presentation/CapabilitySelectionState.cs",
            "Presentation/ModelBindingEditorState.cs",
            "Presentation/OperationState.cs",
            "Presentation/PresentationDispatcher.cs",
            "Presentation/PresentationExceptionPolicy.cs",
            "Presentation/PresentationTaskScope.cs",
            "Presentation/ProviderModelLoader.cs",
            "Presentation/TimedStatusController.cs",
        ],
        StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*public\s+(?:(?:abstract|sealed|static|partial|readonly|ref)\s+)*(?:class|struct|interface|enum|record|delegate)\b")]
    private static partial Regex PublicTypeDeclaration();

    [GeneratedRegex(@"^\s*public\s+(?:(?:abstract|sealed|static|partial|readonly|ref)\s+)*(?:class|struct|interface|enum|record(?:\s+class|\s+struct)?|delegate)\s+([A-Za-z_][A-Za-z0-9_]*)\b")]
    private static partial Regex PublicTypeName();
}
