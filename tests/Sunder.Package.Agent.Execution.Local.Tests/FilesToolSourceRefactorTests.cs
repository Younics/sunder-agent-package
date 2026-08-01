using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Package.Agent.Tools.Shell;
using Sunder.Package.Agent.Tools.Web;
using Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Sdk.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class FilesToolSourceRefactorTests : IDisposable
{
    private readonly RegressionTestPackageScope _packageScope = RegressionTestPackageScope.Create();
    private readonly List<RegressionTestExtensionCatalog> _catalogs = [];

    [Fact]
    public void PackageContextConstructor_EnablesScopedInstructionEnforcement()
    {
        var constructor = typeof(FilesToolSource).GetConstructor([typeof(IPackageContext)]);
        var source = new FilesToolSource(_packageScope.Context);

        Assert.NotNull(constructor);
        Assert.True(source.IsScopedInstructionEnforcementEnabled);
    }

    [Theory]
    [InlineData("read", "{\"path\":\"file.txt\",\"offset\":0}", "offset")]
    [InlineData("read", "{\"path\":\"file.txt\",\"limit\":2001}", "limit")]
    [InlineData("read", "{\"path\":\"   \"}", "path")]
    [InlineData("grep", "{\"pattern\":\"\"}", "pattern")]
    [InlineData("apply_patch", "{\"patchText\":\"   \"}", "patchText")]
    public async Task ExecuteAsync_ReportsFocusedParserErrors(string toolId, string argumentsJson, string parameter)
    {
        var (source, _, context) = CreateSource(new MemoryExecutionTarget());

        var result = await source.ExecuteAsync(context, new AgentToolRequest(toolId, argumentsJson));

        Assert.True(result.IsError);
        Assert.Equal("files-arguments-invalid", result.ErrorCode);
        Assert.Contains(parameter, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_DelegatesNormalizedRangeToCapableTarget_AndPreservesLineNumbers()
    {
        var target = new RangedMemoryExecutionTarget(new Dictionary<string, string>
        {
            ["nested/file.txt"] = "one\ntwo\nthree\nfour",
        });
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(
            context,
            new AgentToolRequest("read", "{\"path\":\"nested/file.txt\",\"offset\":2,\"limit\":2}"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal(new AgentFileReadRequest("nested/file.txt", 2, 2), target.LastReadRequest);
        Assert.Equal($"2: two{Environment.NewLine}3: three", result.Content);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public async Task Read_SlicesLegacyWholeFileTarget_ForCompatibility()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["file.txt"] = "one\ntwo\nthree",
        });
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(
            context,
            new AgentToolRequest("read", "{\"path\":\"file.txt\",\"offset\":2,\"limit\":1}"));

        Assert.Equal("2: two", result.Content);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public async Task Read_MapsStructuredTargetFailureToTypedToolError()
    {
        var target = new StructuredReadErrorTarget();
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(
            context,
            new AgentToolRequest("read", "{\"path\":\"missing.txt\"}"));

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.FileNotFound, result.ErrorCode);
        Assert.Contains("File not found", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("1: File not found", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyPatch_PreflightFailure_PerformsNoMutations()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "actual second",
        });
        var (source, _, context) = CreateSource(target);
        var patch = PatchArguments("""
            *** Begin Patch
            *** Update File: first.txt
            @@
            -old first
            +new first
            *** Update File: second.txt
            @@
            -missing second
            +new second
            *** End Patch
            """);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", patch));

        Assert.True(result.IsError);
        Assert.Equal("patch-preflight-failed", result.ErrorCode);
        Assert.Contains("No files were changed", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, target.MutationCallCount);
        Assert.Equal("old first", target.Files["first.txt"]);
        Assert.Equal("actual second", target.Files["second.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_PartialFailure_IsReportedAndSafelyCompensated()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "old second",
        })
        {
            FailMutationCall = 2,
        };
        var (source, _, context) = CreateSource(target);
        var patch = PatchArguments("""
            *** Begin Patch
            *** Update File: first.txt
            @@
            -old first
            +new first
            *** Update File: second.txt
            @@
            -old second
            +new second
            *** End Patch
            """);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", patch));

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Contains("partially applied", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Compensation restored 1 operation", result.Content, StringComparison.Ordinal);
        Assert.Contains("No applied patch changes remain", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, target.MutationCallCount);
        Assert.Equal("old first", target.Files["first.txt"]);
        Assert.Equal("old second", target.Files["second.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_FailedTargetReprobeUnavailable_StillCompensatesPriorOperations()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
        })
        {
            FailMutationCall = 2,
            ReprobeUnavailablePath = "missing/second.txt",
        };
        var (source, _, context) = CreateSource(target);
        var patch = PatchArguments("""
            *** Begin Patch
            *** Update File: first.txt
            @@
            -old first
            +new first
            *** Add File: missing/second.txt
            +new second
            *** End Patch
            """);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", patch));

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Contains("Compensation restored 1 operation", result.Content, StringComparison.Ordinal);
        Assert.Contains("diagnostic probing was unavailable", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing parent/backend unavailable", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may remain partially modified", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, target.MutationCallCount);
        Assert.Equal("old first", target.Files["first.txt"]);
        Assert.DoesNotContain("missing/second.txt", target.Files.Keys);
    }

    [Fact]
    public async Task ApplyPatch_CancellationAfterMutation_CompensatesAndPropagates()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "old second",
        })
        { CancelMutationCall = 2 };
        var (source, _, context) = CreateSource(target);
        var patch = PatchArguments("""
            *** Begin Patch
            *** Update File: first.txt
            @@
            -old first
            +new first
            *** Update File: second.txt
            @@
            -old second
            +new second
            *** End Patch
            """);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", patch)));

        Assert.Equal(3, target.MutationCallCount);
        Assert.Equal("old first", target.Files["first.txt"]);
        Assert.Equal("old second", target.Files["second.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_CancellationAfterMutationWithRollbackConflict_ReturnsPartialResult()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "old second",
        })
        {
            CancelAfterMutationCall = 2,
            BeforeMutationCall = 3,
            BeforeMutation = current => current.Files["second.txt"] = "concurrent rollback conflict",
        };
        var (source, _, context) = CreateSource(target);
        var patch = PatchArguments("""
            *** Begin Patch
            *** Update File: first.txt
            @@
            -old first
            +new first
            *** Update File: second.txt
            @@
            -old second
            +new second
            *** End Patch
            """);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", patch));

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Contains("canceled", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Completed rollbacks: 'first.txt'", result.Content, StringComparison.Ordinal);
        Assert.Contains("Failed rollbacks: 'second.txt'", result.Content, StringComparison.Ordinal);
        Assert.Contains("changed after patch preflight", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, target.MutationCallCount);
        Assert.Equal("old first", target.Files["first.txt"]);
        Assert.Equal("concurrent rollback conflict", target.Files["second.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_CancellationAfterMutationWithProbeFailure_ReturnsPartialResult()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "old second",
        })
        {
            CancelAfterMutationCall = 2,
            ReprobeUnavailablePath = "second.txt",
        };
        var (source, _, context) = CreateSource(target);
        var patch = PatchArguments("""
            *** Begin Patch
            *** Update File: first.txt
            @@
            -old first
            +new first
            *** Update File: second.txt
            @@
            -old second
            +new second
            *** End Patch
            """);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", patch));

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Contains("Completed rollbacks: 'first.txt'", result.Content, StringComparison.Ordinal);
        Assert.Contains("Failed rollbacks: none", result.Content, StringComparison.Ordinal);
        Assert.Contains("operation 'second.txt'", result.Content, StringComparison.Ordinal);
        Assert.Contains("diagnostic probing was unavailable", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing parent/backend unavailable", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, target.MutationCallCount);
        Assert.Equal("old first", target.Files["first.txt"]);
        Assert.Equal("new second", target.Files["second.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_ConcurrentEditAfterPreflight_IsRejectedByTargetCas()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
        })
        {
            BeforeMutationCall = 1,
            BeforeMutation = current => current.Files["first.txt"] = "concurrent edit",
        };
        var (source, _, context) = CreateSource(target);
        var patch = PatchArguments("""
            *** Begin Patch
            *** Update File: first.txt
            @@
            -old first
            +new first
            *** End Patch
            """);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", patch));

        Assert.True(result.IsError);
        Assert.Equal("patch-apply-failed", result.ErrorCode);
        Assert.Contains("changed after patch preflight", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("concurrent edit", target.Files["first.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_FailedTargetThatReachedNextState_IsAlsoCompensated()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "old second",
        })
        {
            FailAfterMutationCall = 2,
        };
        var (source, _, context) = CreateSource(target);
        var patch = PatchArguments("""
            *** Begin Patch
            *** Update File: first.txt
            @@
            -old first
            +new first
            *** Update File: second.txt
            @@
            -old second
            +new second
            *** End Patch
            """);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", patch));

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Contains("Compensation restored 2 operations", result.Content, StringComparison.Ordinal);
        Assert.Equal(4, target.MutationCallCount);
        Assert.Equal("old first", target.Files["first.txt"]);
        Assert.Equal("old second", target.Files["second.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_LinuxAtEmptyPathEperm_UsesPortablePublishFallback()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var targetPath = Path.Combine(root, "target.txt");
        await File.WriteAllTextAsync(targetPath, "old content");
        var (source, context) = await CreateLocalSourceAsync(scope, root);
        var methods = new List<LocalUnixAnonymousPublishMethod>();
        AgentToolResult result;
        using (LocalUnixNative.OverrideAnonymousPublishErrors(method =>
               {
                   methods.Add(method);
                   return method == LocalUnixAnonymousPublishMethod.EmptyPath ? 1 : null;
               }))
        {
            result = await source.ExecuteAsync(
                context,
                new AgentToolRequest("apply_patch", PatchArguments("""
                    *** Begin Patch
                    *** Update File: target.txt
                    @@
                    -old content
                    +new content
                    *** End Patch
                    """)));
        }

        Assert.False(result.IsError, result.Content);
        Assert.Equal("new content", await File.ReadAllTextAsync(targetPath));
        Assert.Equal(
            [LocalUnixAnonymousPublishMethod.EmptyPath, LocalUnixAnonymousPublishMethod.ProcSelfFd],
            methods);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(root),
            path => HostSecurePathEngine.IsTemporaryName(Path.GetFileName(path)));
    }

    [Theory]
    [InlineData("local", "add")]
    [InlineData("local", "update")]
    [InlineData("local", "delete")]
    [InlineData("docker", "add")]
    [InlineData("docker", "update")]
    [InlineData("docker", "delete")]
    public async Task ApplyPatch_StrictTargets_CompensateWithExactPostMutationResource(
        string targetKind,
        string operationKind)
    {
        using var scope = RegressionTestPackageScope.Create();
        var configuredRoot = Path.Combine(scope.RootPath, "configured");
        var targetRoot = Path.Combine(scope.RootPath, "target");
        Directory.CreateDirectory(configuredRoot);
        Directory.CreateDirectory(targetRoot);
        var now = DateTimeOffset.UtcNow;
        var workspaceRoot = targetKind == "local" ? configuredRoot : targetRoot;
        var workspace = new AgentWorkspaceRecord(
            "workspace",
            "Workspace",
            null,
            now,
            now,
            [new AgentWorkspacePathRecord("path", "workspace", workspaceRoot, true, 0, now, now)]);
        IAgentExecutionTarget strictTarget;
        if (targetKind == "local")
        {
            var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
            strictTarget = new LocalExecutionTarget(
                scope.Context,
                configService,
                new LocalShellCatalogService(scope.Context));
        }
        else
        {
            strictTarget = new StrictDockerHostExecutionTarget(targetRoot);
        }

        var target = new FailSecondMutationExecutionTarget(strictTarget);
        var catalog = CreateTargetCatalog(target);
        var source = new FilesToolSource(scope.Context);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            target.Descriptor.TargetId,
            "primary-execution-target",
            true,
            0,
            now,
            now);
        var subjectPath = targetKind == "local"
            ? Path.Combine(targetRoot, "subject.txt")
            : "/workspace/subject.txt";
        var triggerPath = targetKind == "local"
            ? Path.Combine(targetRoot, "trigger.txt")
            : "/workspace/trigger.txt";
        var subjectHostPath = Path.Combine(targetRoot, "subject.txt");
        var triggerHostPath = Path.Combine(targetRoot, "trigger.txt");
        if (operationKind is "update" or "delete")
        {
            await File.WriteAllTextAsync(subjectHostPath, "old subject");
        }
        await File.WriteAllTextAsync(triggerHostPath, "old trigger");
        var firstOperation = operationKind switch
        {
            "add" => $"*** Add File: {subjectPath}\n+new subject",
            "update" => $"*** Update File: {subjectPath}\n@@\n-old subject\n+new subject",
            "delete" => $"*** Delete File: {subjectPath}",
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        };
        var request = new AgentToolRequest(
            "apply_patch",
            PatchArguments($"""
                *** Begin Patch
                {firstOperation}
                *** Update File: {triggerPath}
                @@
                -old trigger
                +new trigger
                *** End Patch
                """));
        var runId = Guid.NewGuid();
        var operation = new AgentResourceOperationContext(
            runId,
            1,
            "patch-call",
            "files.mutate",
            ResourceIndex: -1,
            "workspace-generation",
            "binding-generation",
            "sunder.package.agent.tools.files",
            $"sunder.package.agent.execution.{targetKind}",
            Guid.NewGuid().ToString("N"),
            AuthorityUseCount: 1,
            CanIssueOutsideAuthority: true);
        var planningContext = new AgentToolExecutionContext(
            Guid.NewGuid(),
            "profile",
            workspace,
            binding,
            RunId: runId,
            RunRevision: 1,
            UserTurnId: Guid.NewGuid(),
            ToolCallId: operation.ToolCallId)
        {
            ResourceOperation = operation,
            ExecutionTargetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets),
        };
        var permission = Assert.IsType<AgentPermissionRequest>(
            await source.BuildPermissionRequestAsync(planningContext, request));
        var executionContext = planningContext with
        {
            AllowOutsideConfiguredScope = string.Equals(
                permission.BoundaryId,
                AgentPermissionBoundaryIds.OutsideConfiguredScope,
                StringComparison.Ordinal),
            ApprovedResourceReferences = permission.ResourceReferences,
            ApprovedResourceClaims = permission.ResourceClaims,
            ApprovedResourceCapabilities = permission.ResourceCapabilities,
            ResourceOperation = operation with { CanIssueOutsideAuthority = false },
        };

        var result = await source.ExecuteAsync(executionContext, request);

        Assert.True(result.IsError);
        Assert.True(
            string.Equals(result.ErrorCode, "patch-partial-application", StringComparison.Ordinal),
            $"{result.ErrorCode}: {result.Content}");
        Assert.Contains("Compensation restored 1 operation", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, target.MutationCallCount);
        if (operationKind == "add")
        {
            Assert.False(File.Exists(subjectHostPath));
        }
        else
        {
            Assert.Equal("old subject", await File.ReadAllTextAsync(subjectHostPath));
        }
        Assert.Equal("old trigger", await File.ReadAllTextAsync(triggerHostPath));
    }

    [Fact]
    public async Task ApplyPatchPermission_OverOperationLimit_DoesNotResolveResources()
    {
        var target = new TrackingPlanningExecutionTarget();
        var (source, context) = CreateSourceForTarget(target);
        var request = new AgentToolRequest("apply_patch", PatchArguments(BuildAddPatch(65)));

        var permission = await source.BuildPermissionRequestAsync(context, request);

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.Unknown, permission!.BoundaryId);
        Assert.Equal(0, target.ResolveCallCount);
        Assert.Empty(permission.ResourceCapabilities);
    }

    [Fact]
    public async Task ApplyPatchPermission_LaterResolutionFailure_ReleasesEarlierAuthority()
    {
        var target = new TrackingPlanningExecutionTarget
        {
            ThrowOnResolveCall = 2,
            CapabilitiesPerResource = 2,
        };
        var (source, context) = CreateSourceForTarget(target);
        var request = new AgentToolRequest("apply_patch", PatchArguments(BuildAddPatch(2)));

        var permission = await source.BuildPermissionRequestAsync(context, request);

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.Unknown, permission!.BoundaryId);
        Assert.Equal(["cap-1-0", "cap-1-1"], target.ReleasedCapabilities);
        Assert.Empty(permission.ResourceCapabilities);
    }

    [Fact]
    public async Task ApplyPatchPermission_Cancellation_ReleasesEarlierAuthority()
    {
        var target = new TrackingPlanningExecutionTarget
        {
            CancelOnResolveCall = 2,
            CapabilitiesPerResource = 2,
        };
        var (source, context) = CreateSourceForTarget(target);
        var request = new AgentToolRequest("apply_patch", PatchArguments(BuildAddPatch(2)));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await source.BuildPermissionRequestAsync(context, request));

        Assert.Equal(["cap-1-0", "cap-1-1"], target.ReleasedCapabilities);
    }

    [Fact]
    public async Task ApplyPatchPermission_CapabilityOverflow_ReleasesEveryIssuedCapability()
    {
        var target = new TrackingPlanningExecutionTarget
        {
            CapabilitiesPerResource = 129,
        };
        var (source, context) = CreateSourceForTarget(target);
        var request = new AgentToolRequest("apply_patch", PatchArguments(BuildAddPatch(1)));

        var permission = await source.BuildPermissionRequestAsync(context, request);

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.Unknown, permission!.BoundaryId);
        Assert.Equal(129, target.ReleasedCapabilities.Count);
        Assert.Equal(129, target.ReleasedCapabilities.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(permission.ResourceCapabilities);
    }

    [Fact]
    public async Task ApplyPatchPermission_AtPathLimit_RequestsAtMost128Capabilities()
    {
        var target = new TrackingPlanningExecutionTarget
        {
            CapabilitiesPerResource = 2,
        };
        var (source, context) = CreateSourceForTarget(target);
        context = context with
        {
            ResourceOperation = new AgentResourceOperationContext(
                Guid.NewGuid(),
                1,
                "patch-limit-call",
                "files.mutate",
                ResourceIndex: -1,
                "workspace-generation",
                "binding-generation",
                "test.tools.files",
                "test.execution.target",
                Guid.NewGuid().ToString("N"),
                CanIssueOutsideAuthority: true),
        };
        var request = new AgentToolRequest("apply_patch", PatchArguments(BuildAddPatch(64)));

        var permission = await source.BuildPermissionRequestAsync(context, request);

        Assert.NotNull(permission);
        Assert.Equal(64, target.ResolveCallCount);
        Assert.Equal(128, permission!.ResourceCapabilities.Count);
        Assert.All(target.RequestedAuthorityUseCounts, count => Assert.Equal(2, count));
        target.ReleaseResourceAuthority(permission.ResourceCapabilities);
    }

    [Fact]
    public async Task ApplyPatch_Success_ReleasesUnusedPostMutationAuthority()
    {
        var target = new ReceiptMemoryExecutionTarget();
        var (source, _, context) = CreateSource(target);
        var request = new AgentToolRequest("apply_patch", PatchArguments("""
            *** Begin Patch
            *** Add File: added.txt
            +content
            *** End Patch
            """));

        var result = await source.ExecuteAsync(context, request);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(["post-mutation-capability"], target.ReleasedCapabilities);
    }

    [Fact]
    public async Task Glob_BoundsResultsAndReportsTruncation()
    {
        var target = new MemoryExecutionTarget
        {
            ProcessOutput = string.Join('\n', Enumerable.Range(1, 1005).Select(index =>
                Path.Combine(MemoryExecutionTarget.SearchRoot, $"file-{((index - 1) % 64) + 1:D4}.txt"))),
        };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("glob", "{\"pattern\":\"*.txt\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.True(result.WasTruncated);
        Assert.Contains("truncated", result.Summary, StringComparison.OrdinalIgnoreCase);
        using var payload = JsonDocument.Parse(result.StructuredPayloadJson!);
        Assert.Equal(1000, payload.RootElement.GetArrayLength());
        Assert.DoesNotContain("file-0065.txt", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grep_WhenRipgrepIsMissing_UsesFallbackThatDoesNotFollowDirectorySymlinks()
    {
        var target = new MissingRipgrepExecutionTarget();
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(
            context,
            new AgentToolRequest("grep", "{\"pattern\":\"needle\",\"path\":\"workspace\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Collection(
            target.Commands,
            command => Assert.Equal("rg", command.FileName),
            command =>
            {
                Assert.Equal("grep", command.FileName);
                Assert.Contains("-rIn", command.Arguments);
                Assert.DoesNotContain("-RIn", command.Arguments);
            });
    }

    [Theory]
    [InlineData("-delete")]
    [InlineData("line\nbreak")]
    [InlineData("quote'name")]
    [InlineData("-dash-name")]
    public async Task LocalStructuredGlob_PreservesHostileDirectoryNameWithoutProcessArguments(string directoryName)
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("The fallback fixture uses a POSIX executable shim.");
        }

        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        var searchDirectory = Path.Combine(root, directoryName);
        var sibling = Path.Combine(root, "must-survive.txt");
        var bin = Path.Combine(scope.RootPath, "bin");
        Directory.CreateDirectory(searchDirectory);
        Directory.CreateDirectory(bin);
        await File.WriteAllTextAsync(Path.Combine(searchDirectory, "match.txt"), "match");
        await File.WriteAllTextAsync(sibling, "sentinel");
        var rg = Path.Combine(bin, "rg");
        await File.WriteAllTextAsync(rg, "#!/bin/sh\nexit 127\n");
        File.SetUnixFileMode(rg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var (source, context) = await CreateLocalSourceAsync(scope, root, [bin]);

        var result = await source.ExecuteAsync(
            context,
            new AgentToolRequest("glob", JsonSerializer.Serialize(new { pattern = "**/*.txt", path = directoryName })));

        Assert.False(result.IsError, result.Content);
        using var payload = JsonDocument.Parse(result.StructuredPayloadJson!);
        Assert.Contains(
            payload.RootElement.EnumerateArray(),
            match => match.GetProperty("Path").GetString() == Path.Combine(searchDirectory, "match.txt"));
        Assert.True(File.Exists(sibling));
        Assert.Equal("sentinel", await File.ReadAllTextAsync(sibling));
    }

    [Fact]
    public async Task SearchOutsideApproval_RejectsLinkedPathDuringPermissionPlanning()
    {
        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        var outside = Path.Combine(scope.RootPath, "outside");
        var first = Path.Combine(outside, "first");
        var second = Path.Combine(outside, "second");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(first, "approved.txt"), "approved");
        await File.WriteAllTextAsync(Path.Combine(second, "secret.txt"), "RETARGETED_SEARCH_SECRET");
        var link = Path.Combine(root, "outside-link");
        CreateDirectorySymlinkOrSkip(link, first);
        var (source, baseContext) = await CreateLocalSourceAsync(scope, root);
        var request = new AgentToolRequest("glob", JsonSerializer.Serialize(new { pattern = "**/*.txt", path = link }));
        await Assert.ThrowsAsync<LocalSecurePathException>(async () =>
            await source.BuildPermissionRequestAsync(baseContext, request));
        Assert.Equal("RETARGETED_SEARCH_SECRET", await File.ReadAllTextAsync(Path.Combine(second, "secret.txt")));
    }

    [Fact]
    public async Task SearchTargetWithoutCanonicalBinding_FailsClosedWithoutExecutingShell()
    {
        var target = new UnboundSearchExecutionTarget();
        var (source, context) = CreateSourceForTarget(target);

        var result = await source.ExecuteAsync(
            context,
            new AgentToolRequest("glob", "{\"pattern\":\"*.txt\"}"));

        Assert.True(result.IsError);
        Assert.Equal("files-search-binding-unavailable", result.ErrorCode);
        Assert.False(target.WasExecuted);
    }

    [Fact]
    public async Task Glob_RejectsBackendPathOutsideCanonicalSearchRoot()
    {
        var target = new MemoryExecutionTarget
        {
            ProcessOutput = Path.Combine(Path.GetTempPath(), "outside-search-result.txt") + "\0",
        };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(
            context,
            new AgentToolRequest("glob", "{\"pattern\":\"*.txt\"}"));

        Assert.True(result.IsError);
        Assert.Equal("files-search-result-invalid", result.ErrorCode);
        Assert.DoesNotContain("outside-search-result.txt", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesShellAndWeb_InvalidRequestPresentation_UsesSharedMarkdownShape()
    {
        const string malformedJson = "{";
        var (files, _, _) = CreateSource(new MemoryExecutionTarget());
        var shell = new ShellToolSource();
        var web = new WebFetchTool(new WebFetchService());

        var filesDetail = files.ResolveToolPresentation(PresentationRequest("read", malformedJson))!.DetailMarkdown!;
        var shellDetail = shell.ResolveToolPresentation(PresentationRequest("shell", malformedJson))!.DetailMarkdown!;
        var webDetail = web.ResolveToolPresentation(PresentationRequest("web_fetch", malformedJson))!.DetailMarkdown!;

        Assert.Equal(RawRequestBlock(filesDetail), RawRequestBlock(shellDetail));
        Assert.Equal(RawRequestBlock(filesDetail), RawRequestBlock(webDetail));
        Assert.StartsWith("**Request**", filesDetail, StringComparison.Ordinal);
        Assert.Contains("```json", filesDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("files", false)]
    [InlineData("shell", true)]
    public async Task FilesAndShell_PreserveBackendTruncationContract(string sourceKind, bool expectedError)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["file.txt"] = "content" })
        {
            ResultsTruncated = true,
            ShellTimedOut = true,
        };
        var (files, _, context) = CreateSource(target);
        var result = sourceKind switch
        {
            "files" => await files.ExecuteAsync(context, new AgentToolRequest("read", "{\"path\":\"file.txt\"}")),
            "shell" => await new ShellToolSource().ExecuteAsync(
                context,
                new AgentToolRequest("shell", "{\"command\":\"test\"}")),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind)),
        };

        Assert.Equal(expectedError, result.IsError);
        Assert.True(result.WasTruncated);
        Assert.Equal("memory:memory", result.BackendId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public async Task WebFetch_RejectsOutOfRangeTimeoutBeforeNetworkCall(int timeoutSeconds)
    {
        var result = await new WebFetchTool(new WebFetchService()).ExecuteAsync(
            new AgentToolExecutionContext(null),
            new AgentToolRequest("web_fetch", $$"""{"url":"https://example.com","timeoutSeconds":{{timeoutSeconds}}}"""));

        Assert.True(result.IsError);
        Assert.Equal("web-fetch-args", result.ErrorCode);
    }

    private (FilesToolSource Source, MemoryExecutionTarget Target, AgentToolExecutionContext Context) CreateSource(MemoryExecutionTarget target)
    {
        var catalog = CreateTargetCatalog(target);
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            target.Descriptor.TargetId,
            "primary-execution-target",
            true,
            0,
            now,
            now);
        return (
            new FilesToolSource(_packageScope.Context),
            target,
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding)
            {
                ExecutionTargetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets),
            });
    }

    private (FilesToolSource Source, AgentToolExecutionContext Context) CreateSourceForTarget(IAgentExecutionTarget target)
    {
        var catalog = CreateTargetCatalog(target);
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            target.Descriptor.TargetId,
            "primary-execution-target",
            true,
            0,
            now,
            now);
        return (
            new FilesToolSource(_packageScope.Context),
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding)
            {
                ExecutionTargetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets),
            });
    }

    private async Task<(FilesToolSource Source, AgentToolExecutionContext Context)> CreateLocalSourceAsync(
        RegressionTestPackageScope scope,
        string root,
        IReadOnlyList<string>? pathEntries = null)
    {
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var target = new LocalExecutionTarget(
            scope.Context,
            configService,
            new LocalShellCatalogService(scope.Context));
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord(
            "workspace",
            "Workspace",
            null,
            now,
            now,
            [new AgentWorkspacePathRecord("path", "workspace", root, true, 0, now, now)]);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            AgentRpcContractIds.ExecutionTarget,
            target.Descriptor.TargetId,
            "primary-execution-target",
            true,
            0,
            now,
            now);
        await configService.SaveConfigAsync(
            binding.BindingId,
            new LocalExecutionWorkspaceConfig(null, pathEntries));
        var catalog = CreateTargetCatalog(target);
        return (
            new FilesToolSource(scope.Context),
            new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding)
            {
                ExecutionTargetReference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets),
            });
    }

    private RegressionTestExtensionCatalog CreateTargetCatalog(IAgentExecutionTarget target)
    {
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ExecutionTargets, target);
        _catalogs.Add(catalog);
        return catalog;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs)
        {
            catalog.Dispose();
        }
        _packageScope.Dispose();
    }

    private static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip($"Directory symbolic links are unavailable: {ex.Message}");
        }
    }

    private static string PatchArguments(string patchText)
        => JsonSerializer.Serialize(new { patchText });

    private static string BuildAddPatch(int operationCount)
    {
        var patch = new StringBuilder("*** Begin Patch\n");
        for (var index = 0; index < operationCount; index++)
        {
            patch.Append("*** Add File: file-")
                .Append(index)
                .Append(".txt\n+content\n");
        }
        return patch.Append("*** End Patch").ToString();
    }

    private static AgentToolPresentationRequest PresentationRequest(string toolId, string argumentsJson)
        => new(toolId, argumentsJson, null, null, null, null, false, null, null);

    private static string RawRequestBlock(string markdown)
    {
        var fence = markdown.IndexOf("```json", StringComparison.Ordinal);
        Assert.True(fence >= 0);
        return markdown[fence..];
    }

    private class MemoryExecutionTarget : IAgentProcessExecutionTarget, IAgentFileSearchExecutionTarget
    {
        public static string SearchRoot { get; } = Path.Combine(Path.GetTempPath(), "sunder-memory-search");

        public MemoryExecutionTarget(Dictionary<string, string>? files = null)
        {
            Files = files ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "memory",
            "memory",
            "Memory",
            null,
            SupportsShell: true,
            SupportsFiles: true);

        public Dictionary<string, string> Files { get; }

        public int MutationCallCount { get; private set; }

        public int? FailMutationCall { get; init; }

        public int? CancelMutationCall { get; init; }

        public int? CancelAfterMutationCall { get; init; }

        public int? FailAfterMutationCall { get; init; }

        public string? ReprobeUnavailablePath { get; init; }

        public int? BeforeMutationCall { get; init; }

        public Action<MemoryExecutionTarget>? BeforeMutation { get; init; }

        public string ProcessOutput { get; init; } = string.Empty;

        public bool ResultsTruncated { get; init; }

        public bool ShellTimedOut { get; init; }

        public AgentFileReadRequest? LastReadRequest { get; protected set; }

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("memory", "memory", AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "POSIX sh"));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MutationCallCount > 0
                && string.Equals(path, ReprobeUnavailablePath, StringComparison.Ordinal))
            {
                throw new DirectoryNotFoundException("Scripted missing parent/backend unavailable.");
            }
            return ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, Files.ContainsKey(path)));
        }

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(
                ShellTimedOut ? 124 : 0,
                ProcessOutput,
                ShellTimedOut,
                WasTruncated: ResultsTruncated));

        public virtual ValueTask<AgentShellCommandResult> ExecuteProcessAsync(AgentExecutionTargetContext context, AgentProcessCommandRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(0, ProcessOutput));

        public async ValueTask<AgentFileSearchProcessResult> ExecuteFileSearchProcessAsync(
            AgentExecutionTargetContext context,
            AgentFileSearchProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            var canonicalPath = Path.GetFullPath(
                Path.IsPathRooted(request.Path)
                    ? request.Path
                    : Path.Combine(SearchRoot, request.Path));
            var arguments = request.Command.Arguments.ToList();
            arguments.Insert(request.PathArgumentIndex, canonicalPath);
            var result = await ExecuteProcessAsync(
                context,
                request.Command with { Arguments = arguments },
                cancellationToken);
            return new AgentFileSearchProcessResult(
                result,
                canonicalPath,
                canonicalPath,
                AgentFileSearchPathStyle.Host);
        }

        public virtual ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastReadRequest = request;
            return ValueTask.FromResult(new AgentFileReadResult(request.Path, Files[request.Path], WasTruncated: ResultsTruncated));
        }

        public virtual ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MutationCallCount++;
            if (MutationCallCount == BeforeMutationCall)
            {
                BeforeMutation?.Invoke(this);
            }

            if (MutationCallCount == CancelMutationCall)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (MutationCallCount == FailMutationCall)
            {
                return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Scripted mutation failure.", IsError: true, ErrorCode: "scripted-failure"));
            }

            if (!request.Overwrite && Files.ContainsKey(request.Path))
            {
                return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "File already exists.", IsError: true, ErrorCode: "file-exists"));
            }

            if (request.ExpectedContentHash is not null
                && (!Files.TryGetValue(request.Path, out var current)
                    || !string.Equals(ContentHash(current), request.ExpectedContentHash, StringComparison.OrdinalIgnoreCase)))
            {
                return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "The file changed after patch preflight; no mutation was applied.", IsError: true, ErrorCode: "file-content-changed"));
            }

            Files[request.Path] = request.Content;
            if (MutationCallCount == CancelAfterMutationCall)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (MutationCallCount == FailAfterMutationCall)
            {
                return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Scripted post-mutation failure.", IsError: true, ErrorCode: "scripted-failure"));
            }

            return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Written."));
        }

        public virtual ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MutationCallCount++;
            if (MutationCallCount == BeforeMutationCall)
            {
                BeforeMutation?.Invoke(this);
            }

            if (MutationCallCount == CancelMutationCall)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (MutationCallCount == FailMutationCall)
            {
                return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Scripted mutation failure.", IsError: true, ErrorCode: "scripted-failure"));
            }

            if (request.ExpectedContentHash is not null
                && (!Files.TryGetValue(request.Path, out var current)
                    || !string.Equals(ContentHash(current), request.ExpectedContentHash, StringComparison.OrdinalIgnoreCase)))
            {
                return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "The file changed after patch preflight; no mutation was applied.", IsError: true, ErrorCode: "file-content-changed"));
            }

            var removed = Files.Remove(request.Path);
            if (removed && MutationCallCount == CancelAfterMutationCall)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (removed && MutationCallCount == FailAfterMutationCall)
            {
                return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Scripted post-mutation failure.", IsError: true, ErrorCode: "scripted-failure"));
            }

            return ValueTask.FromResult(removed
                ? new AgentFileMutationResult(request.Path, "Deleted.")
                : new AgentFileMutationResult(request.Path, "Path does not exist.", IsError: true, ErrorCode: "path-not-found"));
        }

        private static string ContentHash(string content)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private sealed class RangedMemoryExecutionTarget(Dictionary<string, string> files)
        : MemoryExecutionTarget(files), IAgentRangedFileExecutionTarget
    {
        public override ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastReadRequest = request;
            var lines = Files[request.Path].Split('\n');
            var offset = request.Offset ?? 1;
            var limit = request.Limit ?? lines.Length;
            var content = string.Join('\n', lines.Skip(offset - 1).Take(limit));
            return ValueTask.FromResult(new AgentFileReadResult(
                request.Path,
                content,
                WasTruncated: offset - 1 + limit < lines.Length));
        }
    }

    private sealed class ReceiptMemoryExecutionTarget
        : MemoryExecutionTarget, IAgentResourceAuthorityExecutionTarget
    {
        public List<string> ReleasedCapabilities { get; } = [];

        public override async ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await base.WriteFileAsync(context, request, cancellationToken);
            return !result.IsError && context.CapturePostMutationResource
                ? result with
                {
                    PostMutationResource = new AgentResolvedResource(
                        "file",
                        request.Path,
                        request.Path,
                        AgentPermissionBoundaryIds.ConfiguredScope,
                        Exists: true)
                    {
                        AuthorityReferences = ["post-mutation-capability"],
                    },
                }
                : result;
        }

        public ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResourceAuthorityValidation(true));

        public void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities)
            => ReleasedCapabilities.AddRange(resourceCapabilities);
    }

    private sealed class StructuredReadErrorTarget : MemoryExecutionTarget
    {
        public override ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentFileReadResult.Failure(
                request.Path,
                AgentFileReadErrorCodes.FileNotFound,
                $"File not found: {request.Path}"));
    }

    private sealed class MissingRipgrepExecutionTarget : MemoryExecutionTarget
    {
        public List<AgentProcessCommandRequest> Commands { get; } = [];

        public override ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
            AgentExecutionTargetContext context,
            AgentProcessCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(request);
            return ValueTask.FromResult(request.FileName == "rg"
                ? new AgentShellCommandResult(127, "rg: command not found")
                : new AgentShellCommandResult(1, string.Empty));
        }
    }

    private sealed class UnboundSearchExecutionTarget : IAgentExecutionTarget
    {
        public bool WasExecuted { get; private set; }

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "unbound",
            "unbound",
            "Unbound",
            null,
            SupportsShell: true,
            SupportsFiles: true);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(
                "unbound",
                "unbound",
                AgentExecutionTargetReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor(
                "sh",
                "sh",
                "/bin/sh",
                AgentShellSyntaxKinds.PosixSh,
                "POSIX sh"));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource(
                "file",
                path,
                path,
                AgentPermissionBoundaryIds.ConfiguredScope,
                Exists: true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            WasExecuted = true;
            return ValueTask.FromResult(new AgentShellCommandResult(0, string.Empty));
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FailSecondMutationExecutionTarget(IAgentExecutionTarget inner)
        : IAgentExecutionTarget, IAgentResourceAuthorityExecutionTarget
    {
        public int MutationCallCount { get; private set; }

        public AgentExecutionTargetDescriptor Descriptor => inner.Descriptor;

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => inner.GetReadinessAsync(context, cancellationToken);

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => inner.GetShellAsync(context, cancellationToken);

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => inner.ResolveFileResourceAsync(context, path, cancellationToken);

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => inner.ExecuteShellAsync(context, request, cancellationToken);

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => inner.ReadFileAsync(context, request, cancellationToken);

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            return MutationCallCount == 2
                ? ValueTask.FromResult(new AgentFileMutationResult(
                    request.Path,
                    "Scripted mutation failure.",
                    IsError: true,
                    ErrorCode: "scripted-failure"))
                : inner.WriteFileAsync(context, request, cancellationToken);
        }

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            return MutationCallCount == 2
                ? ValueTask.FromResult(new AgentFileMutationResult(
                    request.Path,
                    "Scripted mutation failure.",
                    IsError: true,
                    ErrorCode: "scripted-failure"))
                : inner.DeleteFileAsync(context, request, cancellationToken);
        }

        public ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => inner is IAgentResourceAuthorityExecutionTarget authorityTarget
                ? authorityTarget.ValidateResourceAuthorityAsync(context, cancellationToken)
                : ValueTask.FromResult(new AgentResourceAuthorityValidation(true));

        public void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities)
        {
            if (inner is IAgentResourceAuthorityExecutionTarget authorityTarget)
            {
                authorityTarget.ReleaseResourceAuthority(resourceCapabilities);
            }
        }
    }

    private sealed class StrictDockerHostExecutionTarget(string hostRoot)
        : IAgentExecutionTarget, IAgentResourceAuthorityExecutionTarget
    {
        private const string NamespaceFingerprint = "strict-patch-test";
        private readonly DockerFileSystemExecutor _executor = new();
        private readonly DockerExecutionRuntimeConfig _config = new(
            "image",
            "container",
            "/bin/sh",
            [],
            [new DockerExecutionMount(hostRoot, "/workspace")],
            "/workspace");

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "docker",
            "docker",
            "Strict Docker Host",
            null,
            SupportsShell: false,
            SupportsFiles: true);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(
                "docker",
                "docker",
                AgentExecutionTargetReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_executor.ResolveFileResource(
                _config,
                path,
                NamespaceFingerprint,
                cancellationToken,
                context: context));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
        {
            var (authority, _) = CaptureAndValidate(context, request.Path, cancellationToken);
            return _executor.ReadFileAsync(_config, request, authority, cancellationToken);
        }

        public async ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            var (authority, chains) = CaptureAndValidate(context, request.Path, cancellationToken);
            LocalSecureApprovalLease? postMutationAuthority = null;
            try
            {
                var result = await _executor.WriteFileAsync(
                    _config,
                    request,
                    authority,
                    cancellationToken,
                    postMutationAuthoritySink: context.CapturePostMutationResource
                        ? captured => postMutationAuthority = captured
                        : null);
                return AttachPostMutationResource(
                    context,
                    request.Path,
                    chains,
                    result,
                    ref postMutationAuthority);
            }
            finally
            {
                postMutationAuthority?.Dispose();
            }
        }

        public async ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            var (authority, chains) = CaptureAndValidate(context, request.Path, cancellationToken);
            LocalSecureApprovalLease? postMutationAuthority = null;
            try
            {
                var result = await _executor.DeleteFileAsync(
                    _config,
                    request,
                    authority,
                    chains,
                    cancellationToken,
                    postMutationAuthoritySink: context.CapturePostMutationResource
                        ? captured => postMutationAuthority = captured
                        : null);
                return AttachPostMutationResource(
                    context,
                    request.Path,
                    chains,
                    result,
                    ref postMutationAuthority);
            }
            finally
            {
                postMutationAuthority?.Dispose();
            }
        }

        public ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResourceAuthorityValidation(
                context.ApprovedResourceClaims.Count <= 128
                && context.ApprovedResourceClaims.All(claim => string.Equals(
                    claim.NamespaceId,
                    DockerFileSystemExecutor.ClaimNamespacePrefix + NamespaceFingerprint,
                    StringComparison.Ordinal))));

        public void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities)
        {
        }

        private (LocalSecureApprovalLease Authority, IReadOnlyList<DockerMountRootIdentityChain> Chains)
            CaptureAndValidate(
                AgentExecutionTargetContext context,
                string requestedPath,
                CancellationToken cancellationToken)
        {
            var path = DockerPathResolver.ResolveHostBinding(_config, requestedPath);
            var root = HostSecurePathEngine.OpenRoot(path.Mount.HostPath, cancellationToken: cancellationToken);
            var chains = DockerMountRootIdentityChains.Capture([new DockerVerifiedMount(path.Mount, root)]);
            LocalSecureApprovalLease? authority = null;
            try
            {
                authority = HostSecurePathEngine.CaptureFromRoot(
                    root,
                    path.HostPath,
                    cancellationToken: cancellationToken,
                    allowMissingSuffix: true);
                var claim = context.ApprovedResourceClaims.Single(candidate =>
                    string.Equals(
                        candidate.NamespaceId,
                        DockerFileSystemExecutor.ClaimNamespacePrefix + NamespaceFingerprint,
                        StringComparison.Ordinal)
                    && string.Equals(candidate.LogicalPath, path.ContainerPath, StringComparison.Ordinal));
                var exactContext = context with
                {
                    ResourceOperation = context.ResourceOperation is { } operation
                        ? operation with { ResourceIndex = claim.ResourceIndex }
                        : null,
                };
                HostResourceClaim.Validate(
                    claim,
                    DockerFileSystemExecutor.ClaimNamespacePrefix + NamespaceFingerprint,
                    path.ContainerPath,
                    path.Mount.ContainerPath,
                    authority,
                    exactContext);
                return (authority, chains);
            }
            catch
            {
                if (authority is null)
                {
                    root.Dispose();
                }
                else
                {
                    authority.Dispose();
                }
                throw;
            }
        }

        private AgentFileMutationResult AttachPostMutationResource(
            AgentExecutionTargetContext context,
            string path,
            IReadOnlyList<DockerMountRootIdentityChain> chains,
            AgentFileMutationResult result,
            ref LocalSecureApprovalLease? postMutationAuthority)
        {
            if (postMutationAuthority is null)
            {
                return result;
            }

            var authority = postMutationAuthority;
            postMutationAuthority = null;
            return result with
            {
                PostMutationResource = _executor.ResolveFileResource(
                    _config,
                    path,
                    NamespaceFingerprint,
                    CancellationToken.None,
                    authority,
                    chains,
                    context),
            };
        }
    }

    private sealed class TrackingPlanningExecutionTarget
        : IAgentExecutionTarget, IAgentResourceAuthorityExecutionTarget
    {
        public int ResolveCallCount { get; private set; }

        public int? ThrowOnResolveCall { get; init; }

        public int? CancelOnResolveCall { get; init; }

        public int CapabilitiesPerResource { get; init; }

        public List<string> ReleasedCapabilities { get; } = [];

        public List<int?> RequestedAuthorityUseCounts { get; } = [];

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "tracking",
            "tracking",
            "Tracking",
            null,
            SupportsShell: false,
            SupportsFiles: true);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(
                "tracking",
                "tracking",
                AgentExecutionTargetReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCallCount++;
            RequestedAuthorityUseCounts.Add(context.ResourceOperation?.AuthorityUseCount);
            if (ResolveCallCount == CancelOnResolveCall)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (ResolveCallCount == ThrowOnResolveCall)
            {
                throw new InvalidOperationException("Scripted resource-resolution failure.");
            }

            return ValueTask.FromResult(new AgentResolvedResource(
                "file",
                path,
                path,
                AgentPermissionBoundaryIds.ConfiguredScope,
                Exists: false)
            {
                AuthorityReferences = Enumerable.Range(0, CapabilitiesPerResource)
                    .Select(index => $"cap-{ResolveCallCount}-{index}")
                    .ToArray(),
            });
        }

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResourceAuthorityValidation(true));

        public void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities)
            => ReleasedCapabilities.AddRange(resourceCapabilities);
    }
}
