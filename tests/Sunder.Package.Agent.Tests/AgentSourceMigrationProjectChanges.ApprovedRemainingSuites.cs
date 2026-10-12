using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Parent-approved remaining-suite closure. The previous complete graph is
    // authenticated below; no historical capture or oracle is replaced.
    private static string BeforeRemainingSuiteProjectClosure(string relative, string current)
    {
        if (relative == "tests/Sunder.Package.Agent.Presentation.Tests/Sunder.Package.Agent.Presentation.Tests.csproj")
        {
            AssertProjectHash("1f07b91eb383dc977ebb7fc9b50a199a89a144a8e651a2b4b50f0e38925cbdb4",
                ProjectSurfaceSignature(current), relative + " (remaining-suite current graph)");
            var project = XDocument.Parse(current);
            foreach (var name in new[] { "RegressionTestFamilyConsumers.cs", "RegressionTestOwnerInfrastructure.cs", "RegressionTestRuntime.cs" })
            {
                var item = Assert.Single(project.Descendants("Compile"), value =>
                    ((string?)value.Attribute("Include"))?.Replace('\\', '/') == "../Sunder.Package.Agent.Tests/" + name);
                Assert.Equal("TestSupport/" + name, ((string?)item.Attribute("Link"))?.Replace('\\', '/'));
                item.Remove();
            }
            var previousProject = project.ToString(SaveOptions.DisableFormatting);
            AssertProjectHash("b93d9366328e72b26e1c2b87a5c18c76243d30e30ab10b92c57426fe1af4ee7b",
                ProjectSurfaceSignature(previousProject), relative + " (authenticated pre-closure graph)");
            return previousProject;
        }
        if (relative != "tests/RegressionTestHarness.props") return current;
        AssertProjectHash("e1a3bd2065a99ebccfb5fcc25cdb4743d3e48779b1c7541ebf0ea64f74b6e0a7",
            ProjectSurfaceSignature(current), relative + " (remaining-suite current graph)");
        var document = XDocument.Parse(current);
        var includes = document.Descendants("Compile").Select(item => item.Attribute("Include")!).ToArray();
        foreach (var name in new[]
        {
            "RegressionTestGeneratedProviders.cs", "RegressionTestGeneratedProviders.Projection.cs",
            "RegressionTestProviderAdapters.Chat.cs", "RegressionTestProviderAdapters.Runtime.cs",
            "RegressionTestProviderAdapters.Extensions.cs", "RegressionTestProviderAdapters.WorkspaceResolver.cs",
        })
        {
            var path = "$(MSBuildThisFileDirectory)Sunder.Package.Agent.Tests/" + name;
            var include = Assert.Single(includes, value => value.Value.Split(';').Any(part => part.Trim() == path));
            Assert.Single(include.Value.Split(';'), part => part.Trim() == path);
            include.Value = string.Join(';', include.Value.Split(';').Where(part => part.Trim() != path));
        }
        var previous = document.ToString(SaveOptions.DisableFormatting);
        AssertProjectHash("bbd45fd76732eada07066f5efd2bbf8aff6c941d214e8f273edb6044996eb216",
            ProjectSurfaceSignature(previous), relative + " (authenticated pre-closure graph)");
        return previous;
    }
}
