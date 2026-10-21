using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SolutionPackageInventoryTests
{
    [Fact]
    public void CurrentPublicContractIdentities_ExcludePrivateAndPlatformApis()
    {
        var contracts = CurrentContractInventory.Contracts.Where(value => value.Exposure == "public").ToArray();
        Assert.Equal(18, contracts.Length);
        Assert.Equal(contracts.Select(contract => contract.ContractId).Order(StringComparer.Ordinal),
            CurrentPublicRpcDescriptors.ContractIds.Order(StringComparer.Ordinal));
        Assert.All(contracts, contract => Assert.Equal(CurrentPublicRpcDescriptors.ContractVersion, contract.Version));
        Assert.False(CurrentPublicRpcDescriptors.IsAgentPublicContract(CurrentPrivateRpcDescriptors.OwnerId));
        Assert.False(CurrentPublicRpcDescriptors.IsAgentPublicContract("sunder.stack.contributor"));
    }

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
        var execution = File.ReadAllText(Path.Combine(builderRoot, "Toolchains/BuilderLocalProcessRunner.cs"));
        var driver = File.ReadAllText(Path.Combine(builderRoot, "Projects/BuilderProjectDriver.cs"));

        Assert.Contains("ArgumentList.Add", execution, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", execution, StringComparison.Ordinal);
        Assert.Contains("selection.CreateProcess(arguments", driver, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(builderRoot, "BuilderWorkspaceExecutionService.cs")));
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
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/sunder-package-release.yml"));
        var ci = File.ReadAllText(Path.Combine(root, ".github/workflows/sunder-package-smoke.yml"));
        var pipeline = File.ReadAllText(Path.Combine(root, "scripts/release/agent_pipeline.py"));
        var native = File.ReadAllText(Path.Combine(root, "scripts/release/native-family.py"));
        var builder = File.ReadAllText(Path.Combine(root, "scripts/release/build-native-family.ps1"));
        var cohort = File.ReadAllText(Path.Combine(root, "scripts/release/agent_cohort.py"));
        Assert.False(File.Exists(Path.Combine(root, ".circleci/config.yml")));
        Assert.Equal(new[] { "sunder-agent-cohort-rollback.yml", "sunder-package-release.yml", "sunder-package-smoke.yml" },
            Directory.EnumerateFiles(Path.Combine(root, ".github/workflows"), "*.yml").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("tags: [\"agent/v*\"]", workflow, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/sunder-package-smoke.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("windows-2022", ci, StringComparison.Ordinal);
        Assert.Contains("scripts/ci/agent_ci.py fixtures", ci, StringComparison.Ordinal);
        Assert.Contains("Lane=PackageCompatibility&Category!=Artifact", ci, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION: \"1\"", ci, StringComparison.Ordinal);
        Assert.Contains("test-evidence-expectations.json", ci, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REQUIRE_AGENT_PACKAGE_RELEASE: \"1\"", workflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_REQUIRE_AGENT_UI_SNAPSHOT: \"1\"", workflow, StringComparison.Ordinal);
        Assert.Contains("release_inputs.py prepare", workflow, StringComparison.Ordinal);
        Assert.Contains("release_inputs.py exec", workflow, StringComparison.Ordinal);
        Assert.Contains("native-family.py assemble", workflow, StringComparison.Ordinal);
        Assert.Contains("SunderPackageUsePrebuiltLeaves=true", native, StringComparison.Ordinal);
        Assert.Contains("SunderPackagePrebuiltRoleInputsFile", native, StringComparison.Ordinal);
        Assert.Contains("SunderPackageRoleInputSourceRoot", builder, StringComparison.Ordinal);
        foreach (var stage in new[] { "stage", "publish", "visibility", "finalize" })
            Assert.Contains("agent_pipeline.py " + stage, workflow, StringComparison.Ordinal);
        Assert.Contains("cohort.apply", pipeline, StringComparison.Ordinal);
        Assert.Contains("cohort.confirm", pipeline, StringComparison.Ordinal);
        Assert.Contains("resolve-package-changes", pipeline, StringComparison.Ordinal);
        Assert.Contains("initial-yanked", pipeline, StringComparison.Ordinal);
        Assert.Contains("/api/v1/me/package-cohorts/availability", cohort, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("ACCEPTANCE_DRIVER", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-m:1", workflow + ci + native + builder, StringComparison.Ordinal);
        foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
            Assert.Contains("rid: " + rid, workflow, StringComparison.Ordinal);

        var expected = AgentPackageRepositoryInventory.GetRuntimePackageProjects().ToDictionary(
            static package => GetReleaseKey(package.Name), StringComparer.Ordinal);
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "packages.json")));
        var rows = inventory.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(expected.Count, rows.Length);
        Assert.Equal(rows.Length, rows.Select(row => row.GetProperty("key").GetString()).Distinct(StringComparer.Ordinal).Count());
        foreach (var row in rows)
        {
            Assert.Equal("sunderpkg", row.GetProperty("artifactType").GetString());
            var package = expected[row.GetProperty("key").GetString()!];
            Assert.Equal(package.FamilyId, row.GetProperty("packageId").GetString());
            Assert.Equal(NormalizeRepositoryPath(package.ProjectPath), row.GetProperty("projectPath").GetString());
            Assert.False(row.TryGetProperty("appProjectPath", out _));
            var canonical = XDocument.Load(package.ProjectPath);
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
            "younics.ai.agent.tools.files",
            "younics.ai.agent.tools.shell",
            "younics.ai.agent.tools.web",
            "younics.ai.agent",
            "younics.sunder.package.maker",
            "younics.ai.agent.execution.docker",
            "younics.ai.agent.execution.local",
            "younics.ai.agent.mcp",
            "younics.ai.agent.skills",
            "younics.ai.agent.subagents",
            "younics.ai.agent.memory.semantic",
            "younics.ai.agent.provider.anthropic",
            "younics.ai.agent.provider.gemini",
            "younics.ai.agent.provider.lmstudio",
            "younics.ai.agent.provider.openai",
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
                "younics.ai.agent.tools.files",
                "younics.ai.agent.tools.shell",
                "younics.ai.agent.tools.web",
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
