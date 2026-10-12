using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SolutionPackageInventoryTests
{
    [Fact]
    public void AgentWorker_RegistersGenerationHealthWithSdkLifecycleSupervision()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var program = File.ReadAllText(Path.Combine(CanonicalPackageRoles.Analysis(CanonicalPackageRoles.Source("Agent")), "roles/worker/worker-entry.g.cs"));
        var lifecycle = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Sunder.Package.Agent",
            "Runtime",
            "AgentWorkerLifecycle.cs"));

        Assert.Contains("Sunder.Sdk.Worker.SunderWorker.RunAsync(Create)", program, StringComparison.Ordinal);
        Assert.Contains("Sunder.Sdk.Worker.SunderWorkerComposition", program, StringComparison.Ordinal);
        Assert.Contains("DeferredLifecycle<global::Sunder.Package.Agent.Runtime.AgentWorkerLifecycle>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("OnActivated = lifecycle", program, StringComparison.Ordinal);
        Assert.Contains("ISunderWorkerLifecycleService", lifecycle, StringComparison.Ordinal);
        Assert.Contains("public Task Completion", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderProcessExecution_DoesNotSynthesizeShellCommandStrings()
    {
        var builderRoot = Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Builder");
        var execution = File.ReadAllText(Path.Combine(builderRoot, "BuilderWorkspaceExecutionService.cs"));

        Assert.Contains("AgentExecutionFacetIds.ProcessExecution", execution, StringComparison.Ordinal);
        Assert.Contains("Runtime.ExecuteProcessAsync", execution, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(builderRoot, "Rpc/BuilderExecutionBindings.cs")));
        Assert.DoesNotContain("BuildCommand", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("QuoteWindows", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildShellCommand", execution, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void ProductionProjects_AreInSolutionAndWorkersHaveGeneratedManifestCoverage()
    {
        var runtimePackages = AgentPackageRepositoryInventory.GetRuntimePackageProjects();
        var solutionProjectPaths = AgentPackageRepositoryInventory.GetSolutionProjectPaths();
        var manifestCoveragePaths = GeneratedPackageManifestTests.CoveredProjectPaths;
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var productionProjects = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(Path.GetFullPath)
            .ToArray();

        Assert.NotEmpty(runtimePackages);
        Assert.NotEmpty(productionProjects);
        Assert.Equal(
            runtimePackages.Count,
            runtimePackages.Select(static package => package.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var projectPath in productionProjects)
        {
            Assert.Contains(projectPath, solutionProjectPaths);
        }
        foreach (var package in runtimePackages)
        {
            Assert.All(package.RoleProjectPaths, projectPath => Assert.Contains(projectPath, solutionProjectPaths));
            Assert.True(package.IsWorker);
            Assert.Contains(package.ProjectPath, manifestCoveragePaths);
        }

        Assert.True(
            manifestCoveragePaths.SetEquals(runtimePackages
                .Where(static package => package.IsWorker)
                .Select(static package => package.ProjectPath)),
            "Generated manifest coverage must exactly match the migrated Worker project inventory.");
    }

    [Fact]
    public void TestProjects_AreInSolutionUnlessExplicitlySupportOnly()
    {
        var testsRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "tests");
        var solutionProjects = AgentPackageRepositoryInventory.GetSolutionProjectPaths();
        var testProjects = Directory.EnumerateFiles(testsRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(project => !SupportOnlyTestProjects.Contains(NormalizeRepositoryPath(project)))
            .ToArray();

        Assert.NotEmpty(testProjects);
        foreach (var testProject in testProjects)
        {
            Assert.Contains(Path.GetFullPath(testProject), solutionProjects);
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ExecutableTestProjects_DeclareCompleteGeneratedTaxonomy()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        using var packageInventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "packages.json")));
        var packageIds = packageInventory.RootElement.GetProperty("packages").EnumerateArray()
            .Where(static package => package.GetProperty("artifactType").GetString() == "sunderpkg")
            .Select(static package => package.GetProperty("packageId").GetString()!)
            .Append("agent-family")
            .ToHashSet(StringComparer.Ordinal);
        var allowedCategories = new HashSet<string>(
            ["Unit", "Contract", "Conformance", "Component", "UI", "Artifact", "System", "Release", "Stress"],
            StringComparer.Ordinal);
        var allowedCapabilities = new HashSet<string>(
            ["None", "AvaloniaHeadless", "Docker", "Filesystem", "HTTP", "Native", "PackageArchive", "Process", "SQLite", "Worker"],
            StringComparer.Ordinal);
        var allowedOperatingSystems = new HashSet<string>(["Any", "Linux", "macOS", "Windows"], StringComparer.Ordinal);
        var allowedSizes = new HashSet<string>(["Small", "Medium", "Large"], StringComparer.Ordinal);
        var allowedLanes = new HashSet<string>(["PackageCompatibility"], StringComparer.Ordinal);
        var solutionProjects = AgentPackageRepositoryInventory.GetSolutionProjectPaths();
        var executableTests = solutionProjects
            .Where(static path => Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(11, executableTests.Length);
        foreach (var projectPath in executableTests)
        {
            var project = XDocument.Load(projectPath);
            string RequiredProperty(string name)
                => Assert.Single(project.Descendants(name)).Value;

            Assert.Contains(RequiredProperty("SunderTestOwner"), packageIds);
            Assert.Contains(RequiredProperty("SunderTestCategory"), allowedCategories);
            Assert.Contains(RequiredProperty("SunderTestSize"), allowedSizes);
            Assert.All(
                RequiredProperty("SunderTestCapabilities").Split(';', StringSplitOptions.RemoveEmptyEntries),
                capability => Assert.Contains(capability, allowedCapabilities));
            Assert.All(
                RequiredProperty("SunderTestOS").Split(';', StringSplitOptions.RemoveEmptyEntries),
                operatingSystem => Assert.Contains(operatingSystem, allowedOperatingSystems));
            Assert.Contains(RequiredProperty("SunderTestSourceMode"), new[] { "true", "false" }, StringComparer.Ordinal);
            Assert.Contains(RequiredProperty("SunderTestPackageMode"), new[] { "true", "false" }, StringComparer.Ordinal);
            var lanes = project.Descendants("SunderTestLanes")
                .SelectMany(static property => property.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                .ToArray();
            Assert.All(lanes, lane => Assert.Contains(lane, allowedLanes));
            if (lanes.Length > 0)
            {
                Assert.Equal("true", RequiredProperty("SunderTestPackageMode"), ignoreCase: true);
            }
            Assert.True(
                string.Equals(RequiredProperty("SunderTestSourceMode"), "true", StringComparison.Ordinal)
                || string.Equals(RequiredProperty("SunderTestPackageMode"), "true", StringComparison.Ordinal),
                $"{NormalizeRepositoryPath(projectPath)} is excluded from both source and package test modes.");
        }

        var projectsWithTaxonomy = solutionProjects
            .Where(path => XDocument.Load(path).Descendants("SunderTestOwner").Any())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(
            projectsWithTaxonomy.SetEquals(executableTests),
            "Test taxonomy must have no missing or orphan project entries.");

        var generator = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "generate-test-inventory.sh"));
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "sunder-package-smoke.yml"));
        Assert.Contains("dotnet sln Sunder.AgentPackage.slnx list", generator, StringComparison.Ordinal);
        Assert.Contains("bash scripts/test/generate-test-inventory.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/reports/test-inventory.json", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageModules_MatchRuntimePackageInventoryAndCompositionCoverage()
    {
        var packages = AgentPackageRepositoryInventory.GetRuntimePackageProjects();
        Assert.Equal(12, packages.Count(package => package.HasAppRole));
        foreach (var worker in AgentPackageRepositoryInventory.GetRuntimePackageProjects())
        {
            Assert.True(File.Exists(worker.ProgramPath));
            Assert.DoesNotContain(System.Reflection.Assembly.Load(worker.Name).GetTypes(), type =>
                typeof(Sunder.Sdk.Abstractions.ISunderAppPackageModule).IsAssignableFrom(type));
            if (worker.HasAppRole)
                Assert.Single(System.Reflection.Assembly.Load(worker.AppAssemblyName!).GetTypes(), type =>
                    !type.IsAbstract && typeof(Sunder.Sdk.Abstractions.ISunderAppPackageModule).IsAssignableFrom(type));
        }
    }

    [Fact]
    public void RoleProjects_ExclusivelyUseCentralPackageBuildMode()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var allProjects = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .ToArray();

        foreach (var projectPath in allProjects)
        {
            var project = System.Xml.Linq.XDocument.Load(projectPath);
            var role = project.Descendants("SunderPackageRole")
                .Select(static property => property.Value)
                .SingleOrDefault();
            var projectText = File.ReadAllText(projectPath);
            var packageProject = project.Descendants("SunderPackageProject")
                .Select(static property => property.Value)
                .SingleOrDefault();
            if (project.Descendants("SunderPackageFamilyId").Any() && role is null)
            {
                Assert.False(string.Equals(packageProject, "false", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Assert.False(string.Equals(packageProject, "true", StringComparison.OrdinalIgnoreCase));
            }
            if (role == "worker")
            {
                Assert.Empty(project.Descendants("SunderPackagePrimaryProject"));
            }
            Assert.Empty(project.Descendants("SunderPackageAggregateProject"));
            Assert.Empty(project.Descendants("SunderPackageWorkerProject"));
            Assert.Empty(project.Descendants("SunderPackageWorkerDirectory"));
            Assert.DoesNotContain("sunder-core", projectText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Build", projectText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WorkerProjects_UseCentralThinModuleDefaults()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        const string expectedRids = "win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64";
        const string workerDefaultsFileName = "Sunder.Agent.Worker.props";
        var workerDefaults = XDocument.Load(Path.Combine(repositoryRoot, workerDefaultsFileName));
        var workerPropertyGroup = Assert.Single(workerDefaults.Descendants("PropertyGroup"));
        var expectedValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SunderPackageProject"] = "true",
            ["SunderSdkWorkerReference"] = "true",
            ["CopyLocalLockFileAssemblies"] = "true",
            ["OutputType"] = "Library",
            ["RuntimeIdentifiers"] = expectedRids,
            ["SelfContained"] = "false",
            ["UseAppHost"] = "false",
            ["PublishSingleFile"] = "false",
            ["PublishTrimmed"] = "false",
        };
        foreach (var (property, expected) in expectedValues)
        {
            Assert.Equal(expected, Assert.Single(workerPropertyGroup.Elements(property)).Value, ignoreCase: true);
        }
        Assert.Empty(workerPropertyGroup.Elements("RuntimeIdentifier"));
        Assert.Empty(workerPropertyGroup.Elements("SunderGeneratedPackageManifest"));

        var centralProperties = expectedValues.Keys
            .Append("RuntimeIdentifier")
            .Append("SunderGeneratedPackageManifest")
            .ToArray();
        var workers = AgentPackageRepositoryInventory.GetRuntimePackageProjects().Select(package => package.ProjectPath).ToArray();
        Assert.Equal(15, workers.Length);
        Assert.All(workers, projectPath =>
        {
            var project = XDocument.Load(projectPath);
            if (Path.GetFileNameWithoutExtension(projectPath) == "Sunder.Package.Agent")
            {
                foreach (var (property, expected) in expectedValues.Where(pair => pair.Key != "OutputType"))
                    Assert.Equal(expected, Assert.Single(project.Descendants(property)).Value, ignoreCase: true);
                Assert.DoesNotContain(project.Descendants("OutputType"), item => item.Value == "Exe");
            }
            else
            {
                Assert.All(centralProperties, property => Assert.Empty(project.Descendants(property)));
                Assert.Single(project.Descendants("Import"), import =>
                    import.Attribute("Project")?.Value.EndsWith(workerDefaultsFileName, StringComparison.Ordinal) == true);
            }
        });
    }

    [Fact]
    public void AppProjects_UseCanonicalSixRidMatrix()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        const string expected = "win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64";
        var defaults = XDocument.Load(Path.Combine(repositoryRoot, "Sunder.Agent.Worker.props"));
        Assert.Equal(expected, Assert.Single(defaults.Descendants("RuntimeIdentifiers")).Value);
        var appProjects = AgentPackageRepositoryInventory.GetRuntimePackageProjects().Where(package => package.HasAppRole).ToArray();
        Assert.Equal(12, appProjects.Length);
        Assert.All(appProjects, package =>
        {
            var project = XDocument.Load(package.ProjectPath);
            Assert.Empty(project.Descendants("SunderPackageAppProject"));
            var overrides = project.Descendants("RuntimeIdentifiers").ToArray();
            Assert.All(overrides, value => Assert.Equal(expected, value.Value));
        });
    }

    [Fact]
    public void AppAndWorkerProjectReferenceClosures_DoNotCrossRoleBoundary()
    {
        var packages = AgentPackageRepositoryInventory.GetRuntimePackageProjects();
        Assert.Equal(15, packages.Count);
        foreach (var package in packages)
        {
            foreach (var name in package.HasAppRole ? new[] { package.Name, package.AppAssemblyName! } : [package.Name])
            {
                var references = System.Reflection.Assembly.Load(name).GetReferencedAssemblies();
                Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal)
                    || reference.Name == "Sunder.Agent.Execution.Common");
                if (name.EndsWith(".App", StringComparison.Ordinal))
                    Assert.DoesNotContain(references, reference => reference.Name == "Sunder.Sdk.Worker");
                else Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal)
                    || reference.Name == "Sunder.Sdk.Avalonia");
            }
        }
    }

    [Fact]
    public void ProductionProjects_HaveOneColocatedStandaloneRestoreLock()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var projects = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(Path.GetFullPath)
            .ToArray();
        var projectDirectories = projects
            .Select(static project => Path.GetDirectoryName(project)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var locks = Directory.EnumerateFiles(sourceRoot, "packages.release.lock.json", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(Path.GetFullPath)
            .ToArray();

        Assert.All(projects, project => Assert.True(
            File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.release.lock.json")),
            $"Production project '{project}' has no standalone restore lock."));
        Assert.All(locks, lockPath => Assert.Contains(
            Path.GetDirectoryName(lockPath)!,
            projectDirectories));
    }

    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void ReleaseWorkflowInventory_MatchesRuntimePackages()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "sunder-package-release.yml");
        var workflow = File.ReadAllText(workflowPath);
        var promotionWorkflow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "sunder-agent-promotion.yml"));
        var acceptanceWorkflow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "sunder-agent-production-acceptance.yml"));
        var smokeWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "sunder-package-smoke.yml"));
        var windowsWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".circleci", "config.yml"));
        var testEvidenceGenerator = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "generate-test-duration-report.py"));
        var testEvidenceFixture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "generate-test-duration-report.fixture.sh"));
        var testFileSystemCapabilities = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tests",
            "Sunder.Package.Agent.TestSupport",
            "TestFileSystemCapabilities.cs"));
        var nugetVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-nuget-package.sh"));
        var coreSdkVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-core-sdk-release.sh"));
        var releaseVersionVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "ReleaseVersionVerifier.cs"));
        var releaseVersionFixture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "release", "verify-agent-release-version.fixture.sh"));
        var releaseLockVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-release-locks.sh"));
        var nativeBuilder = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "build-native-family.ps1"));
        var nativeAssembler = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "assemble-native-family.sh"));
        var nativeRecorder = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "record-native-leaf-evidence.sh"));
        var nativeEvidenceVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-native-family-evidence.sh"));
        var nativeImplementation = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "native-family.py"));
        Assert.Contains("native-family.py\" record", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("native-family.py\" assemble", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("native-family.py\" verify", nativeEvidenceVerifier, StringComparison.Ordinal);
        nativeRecorder += nativeImplementation;
        nativeAssembler += nativeImplementation;
        nativeEvidenceVerifier += nativeImplementation;
        var outputAuditor = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "generate-package-output-audit.sh"));
        var nativeSmokeRunner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "run-native-runtime-smoke.sh"));
        var releasePublisher = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "publish-agent-family.sh"));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "scripts", "release", "verify-execution-runtime-availability.sh")));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "scripts", "test", "release", "verify-execution-runtime-availability.fixture.sh")));
        var nativeReleaseFixture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "release", "native-release.fixture.sh"));
        var nativeSmoke = File.ReadAllText(Path.Combine(repositoryRoot, "tests", "fixtures", "AgentNativeReleaseSmokeTests.cs"));
        var registryPublisher = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "publish-registry-package.sh"));
        var familyPromoter = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "agent_cohort.py"));
        var cohortFixture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "release", "agent-release.fixture.py"));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "scripts", "release", "promote-family-dist-tag.sh")));
        var releasePromoter = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "promote-agent-family.sh"));
        var releasePromotionFixture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "release", "promote-agent-family.fixture.sh"));
        var acceptanceArtifactVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-acceptance-artifact.sh"));
        var publicationVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-agent-publication.sh"));
        var publicationVerifierFixture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "release", "verify-agent-publication.fixture.sh"));
        var acceptanceUploader = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "upload-agent-acceptance.sh"));
        var acceptanceDownloader = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "download-agent-acceptance.sh"));
        var acceptanceUploadFixture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test", "release", "upload-agent-acceptance.fixture.sh"));
        var expected = AgentPackageRepositoryInventory.GetRuntimePackageProjects().ToDictionary(
            static package => GetReleaseKey(package.Name),
            StringComparer.Ordinal);
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "packages.json")));
        var releaseProjects = inventory.RootElement.GetProperty("packages").EnumerateArray()
            .Where(static package => package.GetProperty("artifactType").GetString() == "sunderpkg")
            .ToDictionary(
                static package => package.GetProperty("key").GetString()!,
                static package =>
                {
                    var app = package.TryGetProperty("appProjectPath", out var appProject)
                        ? appProject.GetString()
                        : null;
                    return (Project: package.GetProperty("projectPath").GetString()!, App: app);
                },
                StringComparer.Ordinal);

        Assert.Contains("- \"agent/v*\"", workflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REQUIRE_AGENT_PACKAGE_RELEASE: \"1\"", workflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REQUIRE_AGENT_UI_SNAPSHOT: \"1\"", workflow, StringComparison.Ordinal);
        Assert.Contains("Expected exactly 15 unique runtime packages", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("sorted(rows, key=lambda row: row[\"key\"])", nativeAssembler, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("packages.json must contain exactly 15 unique canonical package archives.", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("group: sunder-agent-family-promotion", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("environment: sunder-agent-release-promotion", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("Apply the accepted visibility stage for the immutable Agent family", promotionWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("promote transactionally", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("actions: read", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("contents: write", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("release_id:", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("publication_run_id:", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("publication_run_attempt:", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("acceptance_run_attempt:", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("acceptance_workflow_sha:", promotionWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("acceptance_artifact_id:", promotionWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("acceptance_artifact_digest:", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("actions/download-artifact", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("path: promotion-policy", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("path: release-source", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_AGENT_PUBLICATION_EXPECTED_DRAFT: \"any\"", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("test \"$GITHUB_REF\" = refs/heads/main", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("ACCEPTANCE_EVIDENCE_SHA256", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/download-agent-acceptance.sh", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/verify-acceptance-artifact.sh", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/sunder-agent-production-acceptance.yml", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains("Sunder Agent Production Acceptance", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains("actions/runs/$run_id/attempts/$run_attempt", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains("actions/artifacts/$artifact_id", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains("--paginate --slurp", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains(".event == $event", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains("(.run_attempt | tostring) == $attempt", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains(".digest == $digest", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains("$artifact.created_at | fromdateiso8601", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains(".head_branch == $branch", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains(".head_sha == $workflow_sha", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains(".conclusion == \"success\"", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains("agent-family-acceptance.json", acceptanceUploader, StringComparison.Ordinal);
        Assert.Contains("agent-family-acceptance-$run_id-$run_attempt.json", acceptanceUploader, StringComparison.Ordinal);
        Assert.Contains("will not be replaced", acceptanceUploader, StringComparison.Ordinal);
        Assert.Contains("agent-family-acceptance-$run_id-$run_attempt.json", acceptanceDownloader, StringComparison.Ordinal);
        Assert.Contains("verify-acceptance-artifact.sh", acceptanceDownloader, StringComparison.Ordinal);
        Assert.Contains("sha256:$expected_sha", acceptanceDownloader, StringComparison.Ordinal);
        Assert.Contains("sunder-agent-production-acceptance-$run_id-$run_attempt", acceptanceArtifactVerifier, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  push:", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("environment: sunder-agent-production-acceptance", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("test \"$GITHUB_REF\" = refs/heads/main", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("path: acceptance-policy", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("path: release-source", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("publication_run_id:", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("publication_run_attempt:", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("sunder-agent-publication-evidence-${{ inputs.release_id }}-${{ inputs.publication_run_attempt }}", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/verify-agent-publication.sh", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("eng/execution-runtimes/production.lock.json", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain(".sunderruntime", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_AGENT_ACCEPTANCE_CANDIDATE_ONLY: \"true\"", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/promote-agent-family.sh", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/upload-agent-acceptance.sh", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/download-agent-acceptance.sh", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("name: sunder-agent-production-native-${{ matrix.rid }}-${{ github.run_attempt }}", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("pattern: sunder-agent-production-native-*-${{ github.run_attempt }}", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("name: sunder-agent-production-acceptance", acceptanceWorkflow, StringComparison.Ordinal);
        var nativeAcceptanceStart = acceptanceWorkflow.IndexOf("\n  native-acceptance:", StringComparison.Ordinal);
        var archiveDecisionStart = acceptanceWorkflow.IndexOf("\n  archive-decision:", StringComparison.Ordinal);
        Assert.True(nativeAcceptanceStart >= 0 && archiveDecisionStart > nativeAcceptanceStart);
        var nativeAcceptanceJob = acceptanceWorkflow[nativeAcceptanceStart..archiveDecisionStart];
        Assert.DoesNotContain("contents: write", nativeAcceptanceJob, StringComparison.Ordinal);
        var receiptDownloadStart = nativeAcceptanceJob.IndexOf("      - name: Download the exact cohort receipt for post-visibility scenarios", StringComparison.Ordinal);
        var nativeExecutionStart = nativeAcceptanceJob.IndexOf("      - name: Run approved phase-specific full-family scenarios", StringComparison.Ordinal);
        Assert.True(receiptDownloadStart >= 0 && nativeExecutionStart > receiptDownloadStart);
        var receiptDownload = nativeAcceptanceJob[receiptDownloadStart..nativeExecutionStart];
        Assert.Contains("if: inputs.phase == 'post-visibility'", receiptDownload, StringComparison.Ordinal);
        Assert.Contains("agent_cohort_receipt.py download", receiptDownload, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}", receiptDownload, StringComparison.Ordinal);
        Assert.DoesNotContain("GH_TOKEN", nativeAcceptanceJob.Remove(receiptDownloadStart, nativeExecutionStart - receiptDownloadStart), StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_REGISTRY_CLI_TOKEN", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_REGISTRY_PUBLISH_TOKEN", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token: write", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER_SHA256", acceptanceWorkflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_AGENT_PRODUCTION_ACCEPTANCE_DRIVER_SHA256", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.Contains("--driver-sha256", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.Contains("chmod 0555 \"$driver_snapshot_root\"", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.Contains("\"official-runtime-install-launch\"", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.Contains("\"official-runtime-source-checksum\"", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.Contains("\"ambient-runtime-isolation\"", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.Contains("python3 -B sunder-core/scripts/release/official_runtime_evidence.py \"$receipt\"", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.Contains("--rid \"$RID\" --field runtimeInstallation --sources dotnet", nativeAcceptanceJob, StringComparison.Ordinal);
        Assert.Contains("contents: write", acceptanceWorkflow[archiveDecisionStart..], StringComparison.Ordinal);
        Assert.Contains("--coordinator-sha256", acceptanceWorkflow[archiveDecisionStart..], StringComparison.Ordinal);
        Assert.Contains("--actions-artifact-name", acceptanceWorkflow[archiveDecisionStart..], StringComparison.Ordinal);
        foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
        {
            Assert.Contains($"- rid: {rid}", acceptanceWorkflow, StringComparison.Ordinal);
        }
        Assert.Contains("for script in scripts/dev/*.sh scripts/release/*.sh scripts/test/*.sh scripts/test/release/*.sh; do", workflow, StringComparison.Ordinal);
        Assert.Contains("bash -n \"$script\"", workflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/test/generate-test-duration-report.fixture.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/test/generate-test-duration-report.fixture.sh", smokeWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash ./scripts/test/generate-test-duration-report.fixture.sh", windowsWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/verify-agent-release-version.sh \"$version\" \"$CORE_SDK_VERSION\"", workflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/test/release/verify-agent-release-version.fixture.sh", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-execution-runtime-availability", workflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/test/release/upload-agent-acceptance.fixture.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/test/release/verify-agent-publication.fixture.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/test/release/verify-agent-release-version.fixture.sh", smokeWorkflow, StringComparison.Ordinal);
        Assert.Contains("PackageVersionRange.Parse(familyDependencyRange)", releaseVersionVerifier, StringComparison.Ordinal);
        Assert.Contains("familyRange.IsSatisfiedBy(releaseVersion)", releaseVersionVerifier, StringComparison.Ordinal);
        Assert.Contains("2.0.0-preview.1", releaseVersionFixture, StringComparison.Ordinal);
        Assert.Contains("2.0.1-preview.1", releaseVersionFixture, StringComparison.Ordinal);
        Assert.Contains("for script in scripts/dev/*.sh scripts/release/*.sh scripts/test/*.sh scripts/test/release/*.sh scripts/compatibility/*.sh; do", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash -n \"$script\"", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/promote-agent-family.sh", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("Sunder Agent Family Release", publicationVerifier, StringComparison.Ordinal);
        Assert.Contains("actions/runs/$publication_run_id/attempts/$publication_run_attempt", publicationVerifier, StringComparison.Ordinal);
        Assert.Contains("Publication handoff artifact was not created by the selected run attempt", publicationVerifier, StringComparison.Ordinal);
        Assert.Contains("MALICIOUS_TAG_POLICY_EXECUTED", publicationVerifierFixture, StringComparison.Ordinal);
        Assert.Contains("rerun-artifact-from-different-attempt", publicationVerifierFixture, StringComparison.Ordinal);
        Assert.DoesNotContain("group: sunder-agent-family-dist-tag-promotion", workflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/publish-registry-package.sh", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/verify-core-sdk-release.sh \"$CORE_SDK_VERSION\" \"$EXPECTED_CORE_COMMIT\"", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-core-sdk-release.sh \"$CORE_SDK_VERSION\" resolve", workflow, StringComparison.Ordinal);
        Assert.Contains("release_inputs.py prepare", workflow, StringComparison.Ordinal);
        Assert.Contains("release_inputs.py exec", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build Sunder.AgentPackage.slnx -c Release --no-restore", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:SunderUseSourceSdk=false -p:RestoreLockedMode=true", workflow, StringComparison.Ordinal);
        Assert.Contains("Cold hydrate and build complete source mode offline against exact Core", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:SunderUseSourceSdk=true --logger trx", workflow, StringComparison.Ordinal);
        Assert.Contains("--filter \"Lane=PackageCompatibility&Category!=Artifact\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--mode source", workflow, StringComparison.Ordinal);
        Assert.Contains("--mode package", workflow, StringComparison.Ordinal);
        Assert.Contains("--platform linux", workflow, StringComparison.Ordinal);
        Assert.Contains("--lane PackageCompatibility", workflow, StringComparison.Ordinal);
        Assert.Contains("--exclude-category Artifact", workflow, StringComparison.Ordinal);
        Assert.Contains("Run complete source-mode behavioral suite", windowsWorkflow, StringComparison.Ordinal);
        Assert.Contains("-p:SunderUseSourceSdk=true", windowsWorkflow, StringComparison.Ordinal);
        Assert.Contains("--filter \"Lane=PackageCompatibility&Category!=Artifact\"", windowsWorkflow, StringComparison.Ordinal);
        Assert.Contains("--mode source", windowsWorkflow, StringComparison.Ordinal);
        Assert.Contains("--mode package --platform windows --lane PackageCompatibility", windowsWorkflow, StringComparison.Ordinal);
        Assert.Contains("--platform windows", windowsWorkflow, StringComparison.Ordinal);
        Assert.Contains("--exclude-category Artifact", windowsWorkflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REQUIRE_TEST_PREREQUISITES: \"1\"", workflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REQUIRE_TEST_PREREQUISITES=1", smokeWorkflow, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 4", testEvidenceGenerator, StringComparison.Ordinal);
        Assert.Contains("\"canonicalIdentityExpectation\"", testEvidenceGenerator, StringComparison.Ordinal);
        Assert.Contains("canonical_identity_enforced = platform == canonical_platform", testEvidenceGenerator, StringComparison.Ordinal);
        Assert.Contains("\"skipPolicyEvidence\"", testEvidenceGenerator, StringComparison.Ordinal);
        Assert.Contains("\"expectedSkipEvidence\"", testEvidenceGenerator, StringComparison.Ordinal);
        Assert.Contains("skips differ by exact identity, reason, or owner", testEvidenceGenerator, StringComparison.Ordinal);
        Assert.Contains("write_trx \"$trx\" Passed NotExecuted", testEvidenceFixture, StringComparison.Ordinal);
        Assert.Contains("wrong prerequisite reason", testEvidenceFixture, StringComparison.Ordinal);
        Assert.Contains("replacement.tests.dll expanded", testEvidenceFixture, StringComparison.Ordinal);
        Assert.Contains("canonical linux test identity hashes", testEvidenceFixture, StringComparison.Ordinal);
        Assert.Contains("--platform windows", testEvidenceFixture, StringComparison.Ordinal);
        const string unavailableCapabilityReason = "Required filesystem capability '{capability}' is unavailable.";
        Assert.Contains(unavailableCapabilityReason, testEvidenceGenerator, StringComparison.Ordinal);
        Assert.Contains(unavailableCapabilityReason, testFileSystemCapabilities, StringComparison.Ordinal);
        Assert.Contains("module.filesystem_capability_available = lambda capability: False", testEvidenceFixture, StringComparison.Ordinal);
        Assert.Contains("all(.dependsOn[]; .versionRange == \">=2.0.0 <3.0.0\")", workflow, StringComparison.Ordinal);
        Assert.Contains("all(.dependsOn[]; .versionRange == \">=2.0.0 <3.0.0\")", smokeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet restore \"$project_path\" --force-evaluate -p:SunderUseSourceSdk=true", smokeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet restore \"$project_path\" --force-evaluate -r linux-x64", smokeWorkflow, StringComparison.Ordinal);
        foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
        {
            Assert.Contains($"- rid: {rid}", workflow, StringComparison.Ordinal);
        }
        Assert.Contains("runs-on: ${{ fromJSON(matrix.runner_labels) }}", workflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_AGENT_RUNNER_WIN_ARM64", workflow, StringComparison.Ordinal);
        Assert.Contains("windows-11-arm", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu-24.04-arm", workflow, StringComparison.Ordinal);
        Assert.Contains("macos-15-intel", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("sunder-agent-release-signing", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token: write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("AZURE_TRUSTED_SIGNING", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("APPLE_BUILD_CERTIFICATE", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderPackageWorkerSigningTargets", nativeBuilder, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "scripts", "release", "Sunder.Agent.ReleaseSigning.targets")));
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "scripts", "release", "sign-worker-apphost.ps1")));
        Assert.Contains("expectedEntryPoint", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains(".runtimeconfig.json", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains("modulePath", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("runtimeConfigSha256", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("dependencyManifestSha256", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains(".leaf.runtimeConfigPath, .leaf.runtimeConfigSha256", workflow, StringComparison.Ordinal);
        Assert.Contains(".leaf.dependencyManifestPath, .leaf.dependencyManifestSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("payload/runtime/shared/$logical_path", workflow, StringComparison.Ordinal);
        Assert.Contains("payload/shared/$logical_path", workflow, StringComparison.Ordinal);
        Assert.Contains("SCHEMA = 3", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("SCHEMA = 3", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("schema-version-3 native evidence, Core qualification digests and 15 exact final archives", nativeEvidenceVerifier, StringComparison.Ordinal);
        Assert.Contains("package-output-audit.json", workflow, StringComparison.Ordinal);
        Assert.Contains("package-output-audit.json", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("package-output-audit.json", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("generate-package-output-audit.sh", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("output-audit-tamper", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("phase0BaselineBytes: 1440013254", outputAuditor, StringComparison.Ordinal);
        Assert.Contains("maximumBytes: 360003313", outputAuditor, StringComparison.Ordinal);
        Assert.Contains("test ! -e \"$script_dir/Sunder.Agent.ReleaseSigning.targets\"", nativeReleaseFixture, StringComparison.Ordinal);
        Assert.Contains("-p:BuildProjectReferences=false", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains("-p:SunderPackageUsePrebuiltLeaves=true", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains("-p:SunderPackageComposeRuntimeIdentifiers=$Rid", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains("-p:BuildProjectReferences=false", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("-p:SunderPackageUsePrebuiltLeaves=true", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("SunderPackagePrebuiltRoleInputsFile=", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("\"%3B\".join(RIDS)", nativeAssembler, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderAgentUniversalWorkerPackage", nativeBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderAgentUniversalWorkerPackage", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("native-test-evidence.json", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("Native framework-dependent leaves and Runtime lifecycle", workflow, StringComparison.Ordinal);
        Assert.Contains("CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost", nativeSmokeRunner, StringComparison.Ordinal);
        Assert.Contains("SUNDER_AGENT_BUILDER_SDK_VERSION", nativeSmokeRunner, StringComparison.Ordinal);
        Assert.Contains("SUNDER_AGENT_BUILDER_DOTNET_PATH", nativeSmokeRunner, StringComparison.Ordinal);
        Assert.Contains("WorkerReleaseMetrics.RecordAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("ActivationCompletion", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("CaptureProcessParentsAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("CountWorkerProcessTree", nativeSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain("startup.Ticks / workers.Count", nativeSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain("workerProcessCount: 1", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("currentRidInstalledBytes", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("startupMilliseconds", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("workerProcessCount", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("workerRssBytes", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("VerifyOwnerUnaryAndStreamAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("VerifyBuilderProjectLifecycleAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("executionRuntimes.GetRuntimeRoot(runtimeReceipt)", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("rollForward = \"disable\"", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("kind = \"add-path-entry\"", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("\"new\", \"console\"", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("\"build\", projectRoot", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("\"publish\", projectRoot", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("SUNDER_AGENT_BUILDER_SDK_VERSION: ${{ env.DOTNET_VERSION }}", workflow, StringComparison.Ordinal);
        Assert.Contains("VerifyCallbackAndAuthAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("VerifyCrashIsolationAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("OfficialRuntimeFixture executionRuntimes", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("EnsureDotNetAsync(\"10.0.12\")", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("AssertBindingMatches", nativeSmoke, StringComparison.Ordinal);
        foreach (var retiredRuntimeInput in new[]
                 {
                     "SignedCurrentRidExecutionRuntimeFixture",
                     "ObserveSupportedAsync",
                     "packagingRevision",
                     "LifecyclePolicies",
                     ".sunderruntime",
                     "Sunder.ExecutionRuntime.Build",
                 })
        {
            Assert.DoesNotContain(retiredRuntimeInput, nativeSmoke, StringComparison.Ordinal);
        }
        Assert.Contains("AssertRuntimeLaunch", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("reinstall: true", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("SchemaMigrations", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("MemorySchemaMigrations", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("AssertProcessIdsStoppedAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("(threshold, \"expectedWorkerProcessCount\")", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("source[name] == 1", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("exactly 15", releaseLockVerifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max_runtime_archive_bytes=$((256 * 1024 * 1024))", workflow, StringComparison.Ordinal);
        Assert.Contains("max_runtime_payload_bytes=$((640 * 1024 * 1024))", workflow, StringComparison.Ordinal);
        Assert.Contains("max_family_archive_bytes=$((4 * 1024 * 1024 * 1024))", workflow, StringComparison.Ordinal);
        Assert.Contains(".installedProjection.bytes <= 360003313", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("phase4_family_payload_bytes", workflow, StringComparison.Ordinal);
        Assert.Contains(".sources[0].name == \"sunder-core\" and .sources[0].sha == $core_commit", coreSdkVerifier, StringComparison.Ordinal);
        Assert.Contains("does not resolve to provenance commit", coreSdkVerifier, StringComparison.Ordinal);
        Assert.Contains("commit=\\\"$expected_core_commit\\\"", coreSdkVerifier, StringComparison.Ordinal);
        Assert.Contains("bash \"$nuget_verifier\" \"$expected_package\" \"$published_package\"", coreSdkVerifier, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/publish-registry-package.sh", releasePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-execution-runtime-availability.sh", releasePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v1/execution-runtimes/", releasePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("promote-family-dist-tag.sh", releasePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release edit", releasePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("--draft=false", releasePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("DIST_TAG", releasePublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_REGISTRY_CLI_TOKEN", releasePublisher, StringComparison.Ordinal);
        Assert.Contains(".isDraft == true", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("gh release download", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("Downloaded GitHub draft primary asset differs from tested bytes", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("evidence_names=(SHA256SUMS release-provenance.json release-sbom.spdx.json toolchain.txt)", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("for name in \"${evidence_names[@]}\"", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("Release evidence SHA-256", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("byte-bind all four downloaded release evidence assets", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("secrets.SUNDER_REGISTRY_PUBLISH_TOKEN", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.SUNDER_REGISTRY_CLI_TOKEN", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_REGISTRY_CLI_TOKEN", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_REGISTRY_PUBLISH_TOKEN", promotionWorkflow, StringComparison.Ordinal);
        Assert.Contains("secrets.SUNDER_REGISTRY_CLI_TOKEN", promotionWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", promotionWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.SUNDER_REGISTRY_TOKEN", workflow, StringComparison.Ordinal);
        Assert.Contains("-e 's/\\[/[[]/g'", nugetVerifier, StringComparison.Ordinal);
        Assert.Contains("if ! entry_hash=", nugetVerifier, StringComparison.Ordinal);
        Assert.Contains("signatureValidationMode\" value=\"require", nugetVerifier, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget trust source nuget.org", nugetVerifier, StringComparison.Ordinal);
        Assert.Contains("--source-url https://api.nuget.org/v3/index.json", nugetVerifier, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget verify \"$published_package\" --all --configfile", nugetVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget trust repository", nugetVerifier, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer $SUNDER_REGISTRY_PUBLISH_TOKEN", registryPublisher, StringComparison.Ordinal);
        Assert.Contains("X-Sunder-Expected-Resource-Id: $package_id", registryPublisher, StringComparison.Ordinal);
        Assert.Contains("X-Sunder-Set-Latest: false", registryPublisher, StringComparison.Ordinal);
        Assert.Contains("-F \"package=@$archive\"", registryPublisher, StringComparison.Ordinal);
        Assert.Contains("-F 'setLatest=false'", registryPublisher, StringComparison.Ordinal);
        Assert.Contains(".canonicalArtifact.downloadUrl", registryPublisher, StringComparison.Ordinal);
        Assert.Contains(".sourceArchiveSha256", registryPublisher, StringComparison.Ordinal);
        Assert.Contains("[--verify-only]", registryPublisher, StringComparison.Ordinal);
        Assert.Contains("Immutable Registry package '%s' %s does not exist", registryPublisher, StringComparison.Ordinal);
        Assert.Contains("/api/v1/me/package-cohorts/availability", familyPromoter, StringComparison.Ordinal);
        Assert.Contains("Forward promotion cannot regress/delete a public tag", familyPromoter, StringComparison.Ordinal);
        Assert.Contains("no compensation or new request", familyPromoter, StringComparison.Ordinal);
        Assert.Contains("expectedPackagePolicyRevision", familyPromoter, StringComparison.Ordinal);
        Assert.Contains("def verify_current(", familyPromoter, StringComparison.Ordinal);
        Assert.Contains("test_post_commit_verification_failure_retains_result_without_compensation", cohortFixture, StringComparison.Ordinal);
        Assert.Contains("verification_args=(--verify-only --initial-yanked", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("publish-registry-package.sh\" \"${verification_args[@]}\"", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("agent_cohort.py\" apply", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("agent_cohort.py\" confirm", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("agent_cohort.py\" verify", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("A cohort mutation requires pre-visibility acceptance.", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("Announcement requires post-visibility acceptance.", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("unset SUNDER_REGISTRY_CLI_TOKEN", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REGISTRY_CLI_TOKEN=\"$registry_cli_token\"", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("git/ref/tags/$encoded_tag", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("releases/$accepted_release_id", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("gh api --method PATCH", releasePromoter, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("pre-finalization-assets", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("post-promotion-assets", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REGISTRY_PRE_MUTATION_GUARD_JSON", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("SUNDER_AGENT_PROMOTION_GUARD_ONLY=true", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("test_guard_failure_stops_before_post_and_guard_receives_no_registry_credentials", cohortFixture, StringComparison.Ordinal);
        Assert.Contains("make the promoted family the last remote check before PATCH", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("Archived acceptance evidence has the wrong trusted workflow", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("bash \"$script_dir/verify-acceptance-artifact.sh\"", releasePromoter, StringComparison.Ordinal);
        Assert.Contains(".name == $acceptance[0].draftRelease.title", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("sunder-agent-promotion-receipt:v2", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("publicationRunId", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("promotionPolicy", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("acceptedBodySha256", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("releaseAssetName", releasePromoter, StringComparison.Ordinal);
        Assert.DoesNotContain("artifactDigest", releasePromoter, StringComparison.Ordinal);
        Assert.DoesNotContain("artifactId", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("evidenceSha256", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("[\"assets\",\"body\",\"bodySha256\",\"draft\",\"id\",\"provenanceSha256\",\"title\"]", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("finalized-retry-assets", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("Verified durable promotion receipt", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("verify_registry_packages \"$verification_directory/pre-finalization-assets\"", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("verify_promoted_family", releasePromoter, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet ", releasePromoter, StringComparison.Ordinal);
        Assert.DoesNotContain("PackSunderPackage", releasePromoter, StringComparison.Ordinal);
        Assert.DoesNotContain("SUNDER_REGISTRY_PUBLISH_TOKEN:?", releasePromoter, StringComparison.Ordinal);
        Assert.Contains("run_orchestrator valid", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("wrong-workflow", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("wrong-run", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("wrong-attempt", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("wrong-evidence", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("durable-acceptance-tamper", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("draft-replacement", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("release-title-mismatch", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("release-body-mismatch", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("tag-drift-before-mutation", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("release-drift-before-mutation", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("release-drift-before-finalization", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("tag-drift-before-finalization", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("family-drift-before-finalization", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("runner-loss-retry", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("forged-finalized-receipt", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("first upload, equal and runner-loss retry", acceptanceUploadFixture, StringComparison.Ordinal);
        Assert.Contains("null digest metadata", acceptanceUploadFixture, StringComparison.Ordinal);
        Assert.Contains("mismatched digest metadata", acceptanceUploadFixture, StringComparison.Ordinal);
        Assert.Contains("finalized release", acceptanceUploadFixture, StringComparison.Ordinal);
        Assert.Contains("New Agent acceptance attempt", acceptanceUploadFixture, StringComparison.Ordinal);
        Assert.Contains("test_concurrent_policy_change_during_guard_keeps_original_fence_and_never_compensates", cohortFixture, StringComparison.Ordinal);
        Assert.Contains("test_forward_downgrade_stable_and_numeric_preview_stops_before_mutation", cohortFixture, StringComparison.Ordinal);
        Assert.Contains("asset-tamper", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("registry-mismatch", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("finalized-retry-without-receipt", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("IMMUTABLE_WRITE", releasePromotionFixture, StringComparison.Ordinal);
        Assert.Contains("bash scripts/test/release/promote-agent-family.fixture.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("python3 -B scripts/test/release/agent-release.fixture.py", workflow, StringComparison.Ordinal);
        var preMutationLongCheck = releasePromoter.IndexOf(
            "verify_registry_packages \"$verification_directory/pre-promotion-assets\"",
            StringComparison.Ordinal);
        var preMutationGuard = releasePromoter.IndexOf(
            "pre_mutation_guard_json=",
            StringComparison.Ordinal);
        var familyMutation = releasePromoter.IndexOf(
            "python3 -B \"$script_dir/agent_cohort.py\" apply",
            preMutationGuard,
            StringComparison.Ordinal);
        Assert.True(preMutationLongCheck >= 0 && preMutationLongCheck < preMutationGuard && preMutationGuard < familyMutation);
        var cohortApply = familyPromoter.IndexOf("def apply(", StringComparison.Ordinal);
        var familySnapshot = familyPromoter.IndexOf("observed = current_tags(", cohortApply, StringComparison.Ordinal);
        var familyGuard = familyPromoter.IndexOf("        guard()", familySnapshot, StringComparison.Ordinal);
        var unchangedRequest = familyPromoter.IndexOf("Retained request changed during guard", familyGuard, StringComparison.Ordinal);
        var cohortPost = familyPromoter.IndexOf("status, response, transport = http(", unchangedRequest, StringComparison.Ordinal);
        Assert.True(cohortApply >= 0 && familySnapshot > cohortApply && familyGuard > familySnapshot
                    && unchangedRequest > familyGuard && cohortPost > unchangedRequest);
        var finalGuard = releasePromoter.IndexOf(
            "# Re-fetch handoff, tag, and numeric draft metadata",
            StringComparison.Ordinal);
        var finalHandoffCheck = releasePromoter.IndexOf("verify_acceptance_handoff", finalGuard, StringComparison.Ordinal);
        var finalTagCheck = releasePromoter.IndexOf("verify_source_tag_ref", finalHandoffCheck, StringComparison.Ordinal);
        var finalReleaseCheck = releasePromoter.IndexOf("load_accepted_release true", finalTagCheck, StringComparison.Ordinal);
        var finalPromotedFamilyCheck = releasePromoter.IndexOf(
            "verify_promoted_family",
            finalReleaseCheck,
            StringComparison.Ordinal);
        var finalPatch = releasePromoter.IndexOf(
            "finalize_accepted_release",
            finalPromotedFamilyCheck,
            StringComparison.Ordinal);
        Assert.True(
            finalGuard >= 0
            && finalGuard < finalHandoffCheck
            && finalHandoffCheck < finalTagCheck
            && finalTagCheck < finalReleaseCheck
            && finalReleaseCheck < finalPromotedFamilyCheck
            && finalPromotedFamilyCheck < finalPatch);
        var formerFinalFamilyCheck = releasePromoter.LastIndexOf(
            "verify_promoted_family",
            finalGuard,
            StringComparison.Ordinal);
        Assert.True(formerFinalFamilyCheck >= 0 && formerFinalFamilyCheck < finalGuard);
        foreach (var forbiddenPromotionCommand in new[]
                 {
                     "dotnet restore",
                     "dotnet build",
                     "dotnet pack",
                     "dotnet nuget push",
                     "PackSunderPackage",
                     "publish-agent-family.sh",
                 })
        {
            Assert.DoesNotContain(forbiddenPromotionCommand, promotionWorkflow, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(".materials == [{name: \"sunder-core-sdk\"", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("Download only the exact tested fan-in artifact", workflow, StringComparison.Ordinal);
        Assert.Equal(expected.Count, releaseProjects.Count);
        foreach (var (releaseKey, package) in expected)
        {
            Assert.True(releaseProjects.TryGetValue(releaseKey, out var releaseProject));
            Assert.Equal(NormalizeRepositoryPath(package.ProjectPath), releaseProject.Project);
            Assert.Equal(
                package.AppProjectPath is null ? null : NormalizeRepositoryPath(package.AppProjectPath),
                releaseProject.App);

            var canonicalPath = Path.GetFullPath(Path.Combine(repositoryRoot, releaseProject.Project));
            var canonical = XDocument.Load(canonicalPath);
            Assert.Empty(canonical.Descendants("SunderPackageRole"));
            Assert.Equal(package.FamilyId, Assert.Single(canonical.Descendants("SunderPackageFamilyId")).Value);
            Assert.Empty(canonical.Descendants("SunderPackageAppProject"));
        }
    }

    [Fact]
    public void RuntimeFamilies_UseCanonicalWorkerTopology()
    {
        var expectedFamilies = new HashSet<string>(StringComparer.Ordinal)
        {
            "sunder.package.agent.tools.files",
            "sunder.package.agent.tools.shell",
            "sunder.package.agent.tools.web",
            "sunder.package.agent",
            "sunder.package.agent.builder",
            "sunder.package.agent.execution.docker",
            "sunder.package.agent.execution.local",
            "sunder.package.agent.mcp",
            "sunder.package.agent.skills",
            "sunder.package.agent.subagents",
            "sunder.package.agent.memory.semantic",
            "sunder.package.agent.provider.anthropic",
            "sunder.package.agent.provider.gemini",
            "sunder.package.agent.provider.lmstudio",
            "sunder.package.agent.provider.openai",
        };
        var workers = AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .ToArray();

        Assert.True(expectedFamilies.SetEquals(workers.Select(static worker => worker.FamilyId!)));
        foreach (var worker in workers)
        {
            var project = System.Xml.Linq.XDocument.Load(worker.ProjectPath);
            Assert.Null(worker.Role);
            Assert.Equal(worker.Name, Path.GetFileNameWithoutExtension(worker.ProjectPath));
            Assert.True(File.Exists(worker.ProgramPath));
            var relativeProgramPath = Path.GetRelativePath(worker.DirectoryPath, worker.ProgramPath).Replace('\\', '/');
            Assert.EndsWith("/roles/worker/worker-entry.g.cs", relativeProgramPath, StringComparison.Ordinal);
            var program = File.ReadAllText(worker.ProgramPath);
            Assert.Contains("SunderWorker.RunAsync", program, StringComparison.Ordinal);
            Assert.DoesNotContain("SunderWorkerV3.RunAsync", program, StringComparison.Ordinal);

            Assert.Empty(project.Descendants("SunderPackageAppProject"));
            Assert.DoesNotContain(project.Descendants("Compile"), item =>
                ((string?)item.Attribute("Include"))?.Contains(".Legacy.cs", StringComparison.Ordinal) == true);
        }

        Assert.Equal(12, workers.Count(static worker => worker.HasAppRole));
        Assert.Equal(
            [
                "sunder.package.agent.tools.files",
                "sunder.package.agent.tools.shell",
                "sunder.package.agent.tools.web",
            ],
            workers.Where(static worker => !worker.HasAppRole).Select(static worker => worker.FamilyId).Order());

        var centralTargets = XDocument.Load(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "Directory.Build.targets"));
        Assert.DoesNotContain(centralTargets.Descendants("ProjectReference"), static reference =>
            string.Equals(reference.Attribute("Include")?.Value, "$(SunderPackageAppProject)", StringComparison.Ordinal));

        Assert.DoesNotContain(EnumerateProductionProjects(), static project =>
            NormalizeRepositoryPath(project).Contains("/Aggregate/", StringComparison.OrdinalIgnoreCase));

        Assert.All(workers, worker => Assert.DoesNotContain(GetProjectReferences(worker.ProjectPath),
            reference => Path.GetFileNameWithoutExtension(reference) is "Sunder.Package.Agent.Contracts" or "Sunder.Package.Agent.Protocol"));
    }

    private static readonly IReadOnlySet<string> SupportOnlyTestProjects = new HashSet<string>(
        ["tests/Sunder.Package.Agent.Provider.TestSupport/Sunder.Package.Agent.Provider.TestSupport.csproj"],
        StringComparer.OrdinalIgnoreCase);

    private static string GetReleaseKey(string packageName)
    {
        const string prefix = "Sunder.Package.Agent";
        var suffix = packageName[prefix.Length..].TrimStart('.');
        return string.IsNullOrEmpty(suffix)
            ? "agent"
            : "agent-" + suffix.Replace('.', '-').ToLowerInvariant();
    }

    private static string NormalizeRepositoryPath(string path)
        => Path.GetRelativePath(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path).Replace('\\', '/');

    private static IEnumerable<string> EnumerateProductionProjects()
        => Directory.EnumerateFiles(
                Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src"),
                "*.csproj",
                SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile);

    private static string? GetProjectRole(string projectPath)
        => XDocument.Load(projectPath).Descendants("SunderPackageRole")
            .Select(static property => property.Value)
            .SingleOrDefault();

    private static IEnumerable<string> GetProjectReferences(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        return XDocument.Load(projectPath).Descendants("ProjectReference")
            .Select(static reference => reference.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include) && !include.Contains("$(", StringComparison.Ordinal))
            .Select(include => Path.GetFullPath(Path.Combine(
                projectDirectory,
                include!.Replace('\\', Path.DirectorySeparatorChar))));
    }
}
