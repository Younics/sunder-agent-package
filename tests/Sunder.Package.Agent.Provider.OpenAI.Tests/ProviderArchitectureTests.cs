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
    public void ProviderProjects_ImportTheSingleSharedSourcePropsFile()
    {
        var sourceDirectory = Path.Combine(GetRepositoryRoot(), "src");
        var providerProjects = Directory.EnumerateDirectories(sourceDirectory, "Sunder.Package.Agent.Provider.*")
            .Where(path => !path.EndsWith(".Shared", StringComparison.Ordinal))
            .SelectMany(path => Directory.EnumerateFiles(path, "*.csproj", SearchOption.TopDirectoryOnly))
            .ToArray();

        Assert.NotEmpty(providerProjects);
        foreach (var projectPath in providerProjects)
        {
            var imports = XDocument.Load(projectPath).Descendants("Import")
                .Select(element => (string?)element.Attribute("Project"));
            Assert.Contains(imports, import => import?.EndsWith(
                "Sunder.Package.Agent.Provider.Shared\\Sunder.Package.Agent.Provider.Shared.props",
                StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public void ProviderAssemblies_PublicTypesStayWithinTheApprovedArchitectureSurface()
    {
        var expectedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Anthropic.AnthropicAgentProvider",
            "Anthropic.AnthropicProviderConfiguration",
            "Anthropic.AnthropicSettingsView",
            "Anthropic.AnthropicSettingsViewModel",
            "Anthropic.AppPackageModule",
            "Anthropic.PackageModule",
            "Gemini.GeminiAgentProvider",
            "Gemini.GeminiEmbeddingProvider",
            "Gemini.GeminiProviderConfiguration",
            "Gemini.GeminiSettingsView",
            "Gemini.GeminiSettingsViewModel",
            "Gemini.AppPackageModule",
            "Gemini.PackageModule",
            "LMStudio.LMStudioAgentProvider",
            "LMStudio.LMStudioEmbeddingProvider",
            "LMStudio.LMStudioProviderConfiguration",
            "LMStudio.LMStudioSettingsLoadState",
            "LMStudio.LMStudioSettingsView",
            "LMStudio.LMStudioSettingsViewModel",
            "LMStudio.AppPackageModule",
            "LMStudio.PackageModule",
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
            "OpenAI.PackageModule",
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
    public void VendorTestProjects_ReferenceOnlyTheirProviderContractsAndTestSupport()
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
                "Sunder.Package.Agent.Contracts",
                "Sunder.Package.Agent.Provider.TestSupport",
            };
            var actualReferences = XDocument.Load(projectPath).Descendants("ProjectReference")
                .Select(element => (string)element.Attribute("Include")!)
                .Select(include => include.Replace('\\', Path.DirectorySeparatorChar))
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                expectedReferences.SetEquals(actualReferences),
                $"{projectName} references [{string.Join(", ", actualReferences.Order())}].");
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

    [GeneratedRegex(@"^\s*public\s+(?:(?:abstract|sealed|static|partial|readonly|ref)\s+)*(?:class|struct|interface|enum|record|delegate)\b")]
    private static partial Regex PublicTypeDeclaration();

    [GeneratedRegex(@"^\s*public\s+(?:(?:abstract|sealed|static|partial|readonly|ref)\s+)*(?:class|struct|interface|enum|record(?:\s+class|\s+struct)?|delegate)\s+([A-Za-z_][A-Za-z0-9_]*)\b")]
    private static partial Regex PublicTypeName();
}
