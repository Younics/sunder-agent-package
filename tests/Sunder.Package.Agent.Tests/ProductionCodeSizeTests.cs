using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class ProductionCodeSizeTests
{
    private const int NewHotspotLineThreshold = 800;

    [Fact]
    public void ProductionFiles_DoNotCreateOrGrowUnreviewedHotspots()
    {
        var baseline = LoadBaseline();
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var violations = new List<string>();
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(static path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(NormalizeRepositoryPath, static path => File.ReadLines(path).Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var (relativePath, recordedLineCount) in baseline)
        {
            if (!sourceFiles.TryGetValue(relativePath, out var lineCount))
            {
                violations.Add($"{relativePath}: stale baseline entry; source file no longer exists");
            }
            else if (lineCount <= NewHotspotLineThreshold)
            {
                violations.Add($"{relativePath}: stale baseline entry; file is now {lineCount} lines and no longer a hotspot");
            }
            else if (lineCount > recordedLineCount)
            {
                violations.Add($"{relativePath}: {lineCount} lines exceeds baseline {recordedLineCount}");
            }
        }

        foreach (var (relativePath, lineCount) in sourceFiles)
        {
            if (baseline.ContainsKey(relativePath))
            {
                continue;
            }

            if (lineCount > NewHotspotLineThreshold)
            {
                violations.Add($"{relativePath}: new hotspot has {lineCount} lines (threshold {NewHotspotLineThreshold})");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Production code-size trend guard failed:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static IReadOnlyDictionary<string, int> LoadBaseline()
    {
        var baselinePath = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "tests",
            "Sunder.Package.Agent.Tests",
            "ProductionCodeSizeBaseline.txt");
        return File.ReadLines(baselinePath)
            .Where(static line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(static line => line.Split('|', 2))
            .ToDictionary(
                static parts => parts[0],
                static parts => int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeRepositoryPath(string path)
        => Path.GetRelativePath(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path).Replace('\\', '/');
}
