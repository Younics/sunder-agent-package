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
        var program = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Sunder.Package.Agent",
            "Program.cs"));
        var lifecycle = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Sunder.Package.Agent",
            "Runtime",
            "AgentWorkerLifecycle.cs"));

        Assert.Contains("new SunderWorkerComposition(serviceProvider, [lifecycle])", program, StringComparison.Ordinal);
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
        var bridge = File.ReadAllText(Path.Combine(builderRoot, "BuilderRpcBridge.cs"));

        Assert.Contains("AgentExecutionFacetIds.ProcessExecution", execution, StringComparison.Ordinal);
        Assert.Contains("AgentExecutionFacetIds.ProcessExecution", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildCommand", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("QuoteWindows", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildShellCommand", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("QuoteWindows", bridge, StringComparison.Ordinal);
    }

    [Fact]
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
    public void PackageModules_MatchRuntimePackageInventoryAndCompositionCoverage()
    {
        var sourceRoot = Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, "src");
        var moduleProjects = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(module => File.ReadAllText(module).Contains("ISunderAppPackageModule", StringComparison.Ordinal))
            .Select(static module => Path.GetDirectoryName(module)!)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var appProjects = AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .Select(static package => package.AppProjectPath)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(moduleProjects);
        Assert.True(
            moduleProjects.SetEquals(appProjects),
            "Every App leaf must expose the one managed module surface exercised by PackageModuleCompositionTests.");
        foreach (var worker in AgentPackageRepositoryInventory.GetRuntimePackageProjects())
        {
            Assert.True(File.Exists(worker.ProgramPath));
            var workerSource = string.Join('\n', Directory.EnumerateFiles(worker.DirectoryPath, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));
            Assert.DoesNotContain(": ISunderRuntimePackageModule", workerSource, StringComparison.Ordinal);
            Assert.DoesNotContain(": ISunderAppPackageModule", workerSource, StringComparison.Ordinal);
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
            if (role is "worker" or "app")
            {
                Assert.False(string.Equals(packageProject, "false", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Assert.False(string.Equals(packageProject, "true", StringComparison.OrdinalIgnoreCase));
            }
            if (role == "worker")
            {
                Assert.Equal(
                    "true",
                    Assert.Single(project.Descendants("SunderPackagePrimaryProject")).Value,
                    ignoreCase: true);
            }
            Assert.Empty(project.Descendants("SunderPackageAggregateProject"));
            Assert.Empty(project.Descendants("SunderPackageWorkerProject"));
            Assert.Empty(project.Descendants("SunderPackageWorkerDirectory"));
            Assert.DoesNotContain("sunder-core", projectText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunder.Package.Build", projectText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AppProjects_UseCanonicalSixRidMatrix()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        const string expected = "win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64";
        var centralTargets = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.targets"));
        var appPropertyGroup = Assert.Single(centralTargets.Descendants("PropertyGroup"), static group =>
            string.Equals(group.Attribute("Condition")?.Value, "'$(SunderPackageRole)' == 'app'", StringComparison.Ordinal));

        Assert.Equal(expected, Assert.Single(appPropertyGroup.Elements("SunderPackageRuntimeIdentifiers")).Value);

        var appProjects = EnumerateProductionProjects()
            .Where(static project => GetProjectRole(project) == "app")
            .ToArray();
        Assert.NotEmpty(appProjects);
        Assert.All(appProjects, project => Assert.Empty(
            XDocument.Load(project).Descendants("SunderPackageRuntimeIdentifiers")));
    }

    [Fact]
    public void AppAndWorkerProjectReferenceClosures_DoNotCrossRoleBoundary()
    {
        var projects = EnumerateProductionProjects()
            .ToDictionary(Path.GetFullPath, StringComparer.OrdinalIgnoreCase);
        var roles = projects.Keys.ToDictionary(
            static project => project,
            GetProjectRole,
            StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects.Keys.Where(project => roles[project] is "app" or "worker"))
        {
            var role = roles[project]!;
            var forbiddenRole = role == "app" ? "worker" : "app";
            var pending = new Stack<string>(GetProjectReferences(project));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryPop(out var referencedProject))
            {
                if (!visited.Add(referencedProject) || !projects.ContainsKey(referencedProject))
                {
                    continue;
                }

                Assert.False(
                    string.Equals(roles[referencedProject], forbiddenRole, StringComparison.Ordinal),
                    $"{role} project '{NormalizeRepositoryPath(project)}' reaches {forbiddenRole} project '{NormalizeRepositoryPath(referencedProject)}'.");
                foreach (var transitiveReference in GetProjectReferences(referencedProject))
                {
                    pending.Push(transitiveReference);
                }
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
    public void ReleaseWorkflowInventory_MatchesRuntimePackages()
    {
        var repositoryRoot = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "sunder-package-release.yml");
        var workflow = File.ReadAllText(workflowPath);
        var smokeWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "sunder-package-smoke.yml"));
        var nugetVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-nuget-package.sh"));
        var coreSdkVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-core-sdk-release.sh"));
        var releaseLockVerifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "verify-release-locks.sh"));
        var nativeBuilder = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "build-native-family.ps1"));
        var nativeAssembler = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "assemble-native-family.sh"));
        var nativeRecorder = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "record-native-leaf-evidence.sh"));
        var nativeSmokeRunner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "run-native-runtime-smoke.sh"));
        var releasePublisher = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "publish-agent-family.sh"));
        var signingTargets = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "Sunder.Agent.ReleaseSigning.targets"));
        var platformSigner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "sign-worker-apphost.ps1"));
        var nativeSmoke = File.ReadAllText(Path.Combine(repositoryRoot, "tests", "fixtures", "AgentNativeReleaseSmokeTests.cs"));
        var registryPublisher = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "publish-registry-package.sh"));
        var familyPromoter = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release", "promote-family-dist-tag.sh"));
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
        Assert.Contains("map(select(.artifactType == \"sunderpkg\")) | sort_by(.key)[]", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget push \"$protocol_path\"", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("--skip-duplicate", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("group: sunder-agent-family-dist-tag-promotion", workflow, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/verify-nuget-package.sh \"$protocol_path\" \"$remote\"", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/verify-core-sdk-release.sh \"$CORE_SDK_VERSION\" \"$EXPECTED_CORE_COMMIT\"", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-core-sdk-release.sh \"$CORE_SDK_VERSION\" resolve", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet restore Sunder.AgentPackage.slnx", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:SunderUseSourceSdk=false -p:RestoreLockedMode=true", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:SunderUseSourceSdk=true", workflow, StringComparison.Ordinal);
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
        Assert.Contains("environment: sunder-agent-release-signing", workflow, StringComparison.Ordinal);
        Assert.Contains("id-token: write", workflow, StringComparison.Ordinal);
        Assert.Contains("AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME", workflow, StringComparison.Ordinal);
        Assert.Contains("APPLE_BUILD_CERTIFICATE_BASE64", workflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_APPLE_SIGNING_CERTIFICATE", workflow, StringComparison.Ordinal);
        Assert.Contains("SUNDER_APPLE_PRIOR_KEYCHAINS", workflow, StringComparison.Ordinal);
        Assert.Contains("restore_user_keychains", workflow, StringComparison.Ordinal);
        Assert.Contains("trap 'cleanup_signing $?' ERR", workflow, StringComparison.Ordinal);
        Assert.True(
            workflow.IndexOf("trap 'cleanup_signing $?' ERR", StringComparison.Ordinal)
            < workflow.IndexOf("base64 --decode", StringComparison.Ordinal),
            "Apple signing cleanup must be registered before certificate decoding starts.");
        Assert.True(
            workflow.LastIndexOf("security list-keychains -d user -s", StringComparison.Ordinal)
            < workflow.LastIndexOf("security delete-keychain", StringComparison.Ordinal),
            "Apple cleanup must restore the prior keychain search list before deleting the temporary keychain.");
        Assert.Contains("SunderPackageWorkerSigningTargets=SignSunderAgentReleaseWorkerAppHost", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains("@(SunderPackageWorkerSigningFile)", signingTargets, StringComparison.Ordinal);
        Assert.Contains("$(SunderPackageWorkerEntryPointRelativePath)", signingTargets, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", platformSigner, StringComparison.Ordinal);
        Assert.Contains("codesign --verify --strict", platformSigner, StringComparison.Ordinal);
        Assert.Contains("-p:BuildProjectReferences=false", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains("-p:SunderPackageUsePrebuiltLeaves=true", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains("-p:SunderPackageComposeRuntimeIdentifiers=$Rid", nativeBuilder, StringComparison.Ordinal);
        Assert.Contains("-p:BuildProjectReferences=false", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("-p:SunderPackageUsePrebuiltLeaves=true", nativeAssembler, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderPackageComposeRuntimeIdentifiers", nativeAssembler, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderAgentUniversalWorkerPackage", nativeBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderAgentUniversalWorkerPackage", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("native-test-evidence.json", nativeAssembler, StringComparison.Ordinal);
        Assert.Contains("Native signed leaves and Runtime lifecycle", workflow, StringComparison.Ordinal);
        Assert.Contains("CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost", nativeSmokeRunner, StringComparison.Ordinal);
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
        Assert.Contains("VerifyCallbackAndAuthAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("VerifyCrashIsolationAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("reinstall: true", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("SchemaMigrations", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("MemorySchemaMigrations", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("AssertProcessIdsStoppedAsync", nativeSmoke, StringComparison.Ordinal);
        Assert.Contains("expectedWorkerProcessCount == 1", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("exactly 15", releaseLockVerifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max_runtime_archive_bytes=$((256 * 1024 * 1024))", workflow, StringComparison.Ordinal);
        Assert.Contains("max_runtime_payload_bytes=$((640 * 1024 * 1024))", workflow, StringComparison.Ordinal);
        Assert.Contains("max_family_archive_bytes=$((4 * 1024 * 1024 * 1024))", workflow, StringComparison.Ordinal);
        Assert.Contains(".sources[0].name == \"sunder-core\" and .sources[0].sha == $core_commit", coreSdkVerifier, StringComparison.Ordinal);
        Assert.Contains("does not resolve to provenance commit", coreSdkVerifier, StringComparison.Ordinal);
        Assert.Contains("commit=\\\"$expected_core_commit\\\"", coreSdkVerifier, StringComparison.Ordinal);
        Assert.Contains("bash \"$nuget_verifier\" \"$expected_package\" \"$published_package\"", coreSdkVerifier, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/publish-registry-package.sh", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("bash scripts/release/promote-family-dist-tag.sh", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("secrets.SUNDER_REGISTRY_PUBLISH_TOKEN", workflow, StringComparison.Ordinal);
        Assert.Contains("secrets.SUNDER_REGISTRY_CLI_TOKEN", workflow, StringComparison.Ordinal);
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
        Assert.Contains("rollback_changed_tags", familyPromoter, StringComparison.Ordinal);
        Assert.Contains("Refusing to regress", familyPromoter, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer $SUNDER_REGISTRY_CLI_TOKEN", familyPromoter, StringComparison.Ordinal);
        Assert.Contains("existing_published_release=true", releasePublisher, StringComparison.Ordinal);
        Assert.Contains("Published release asset '%s' differs from this verified build.", releasePublisher, StringComparison.Ordinal);
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
            Assert.Equal("worker", Assert.Single(canonical.Descendants("SunderPackageRole")).Value);
            Assert.Equal("true", Assert.Single(canonical.Descendants("SunderPackagePrimaryProject")).Value, ignoreCase: true);
            Assert.Equal(package.FamilyId, Assert.Single(canonical.Descendants("SunderPackageFamilyId")).Value);
            var appProject = canonical.Descendants("SunderPackageAppProject").Select(static property => property.Value).SingleOrDefault();
            if (package.AppProjectPath is null)
            {
                Assert.Null(appProject);
            }
            else
            {
                Assert.Equal(
                    package.AppProjectPath,
                    Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(canonicalPath)!,
                        appProject!.Replace('\\', Path.DirectorySeparatorChar))),
                    ignoreCase: true);
            }
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
            Assert.Equal("worker", worker.Role);
            Assert.Equal("true", Assert.Single(project.Descendants("SunderPackagePrimaryProject")).Value, ignoreCase: true);
            Assert.Equal("Exe", Assert.Single(project.Descendants("OutputType")).Value);
            Assert.Equal("true", Assert.Single(project.Descendants("SelfContained")).Value);
            Assert.Equal("true", Assert.Single(project.Descendants("UseAppHost")).Value);
            Assert.Equal("false", Assert.Single(project.Descendants("PublishSingleFile")).Value);
            Assert.Equal(worker.Name, Path.GetFileNameWithoutExtension(worker.ProjectPath));
            Assert.True(File.Exists(worker.ProgramPath));
            var relativeProgramPath = Path.GetRelativePath(worker.DirectoryPath, worker.ProgramPath).Replace('\\', '/');
            Assert.Contains(relativeProgramPath, new[] { "Program.cs", "Worker/Program.cs" });
            var program = File.ReadAllText(worker.ProgramPath);
            Assert.Contains("SunderWorker.RunAsync", program, StringComparison.Ordinal);
            Assert.DoesNotContain("SunderWorkerV3.RunAsync", program, StringComparison.Ordinal);

            var appProperty = project.Descendants("SunderPackageAppProject").Select(static property => property.Value).SingleOrDefault();
            if (worker.AppProjectPath is null)
            {
                Assert.Null(appProperty);
            }
            else
            {
                Assert.Equal(
                    worker.AppProjectPath,
                    Path.GetFullPath(Path.Combine(worker.DirectoryPath, appProperty!.Replace('\\', Path.DirectorySeparatorChar))),
                    ignoreCase: true);
            }

            if (relativeProgramPath == "Worker/Program.cs")
            {
                Assert.Equal("false", Assert.Single(project.Descendants("EnableDefaultCompileItems")).Value, ignoreCase: true);
                Assert.Contains(project.Descendants("Compile"), static item =>
                    string.Equals(item.Attribute("Include")?.Value, "Worker\\**\\*.cs", StringComparison.Ordinal));
                Assert.DoesNotContain(project.Descendants("Compile"), static item =>
                {
                    var include = item.Attribute("Include")?.Value;
                    return include?.StartsWith("App\\", StringComparison.OrdinalIgnoreCase) == true
                           || include?.StartsWith("Shared\\", StringComparison.OrdinalIgnoreCase) == true;
                });
            }
        }

        Assert.Equal(12, workers.Count(static worker => worker.AppProjectPath is not null));
        Assert.Equal(
            [
                "sunder.package.agent.tools.files",
                "sunder.package.agent.tools.shell",
                "sunder.package.agent.tools.web",
            ],
            workers.Where(static worker => worker.AppProjectPath is null).Select(static worker => worker.FamilyId).Order());

        var centralTargets = XDocument.Load(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "Directory.Build.targets"));
        var appRestoreReference = Assert.Single(centralTargets.Descendants("ProjectReference"), static reference =>
            string.Equals(reference.Attribute("Include")?.Value, "$(SunderPackageAppProject)", StringComparison.Ordinal));
        Assert.Contains("'$(SunderPackageInnerBuild)' != 'true'", appRestoreReference.Parent?.Attribute("Condition")?.Value, StringComparison.Ordinal);
        Assert.Equal("false", appRestoreReference.Attribute("ReferenceOutputAssembly")?.Value);
        Assert.Equal("false", appRestoreReference.Attribute("BuildReference")?.Value);
        Assert.Equal("all", appRestoreReference.Attribute("PrivateAssets")?.Value);

        Assert.DoesNotContain(EnumerateProductionProjects(), static project =>
            NormalizeRepositoryPath(project).Contains("/Aggregate/", StringComparison.OrdinalIgnoreCase));

        var protocolProject = System.Xml.Linq.XDocument.Load(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "src",
            "Sunder.Package.Agent.Contracts",
            "Sunder.Package.Agent.Contracts.csproj"));
        Assert.Equal("protocol", Assert.Single(protocolProject.Descendants("SunderPackageRole")).Value);
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
