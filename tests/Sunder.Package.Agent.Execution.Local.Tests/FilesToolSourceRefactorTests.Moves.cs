using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class FilesToolSourceRefactorTests
{
    [Theory]
    [InlineData("", "\uFEFFone\r\nlast\n", "\uFEFFone\r\nlast\n")]
    [InlineData("@@\n one", "\uFEFFone\r\nlast\n", "\uFEFFone\r\nlast\n")]
    [InlineData("@@\n-one\n+two", "\uFEFFone\r\nlast\n", "\uFEFFtwo\r\nlast\n")]
    [InlineData("", "", "")]
    public async Task ApplyPatch_Move_PreservesTextAndSupportsOptionalEdits(string hunk, string original, string expected)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = original });
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest(hunk));

        Assert.False(result.IsError, result.Content);
        Assert.False(target.Files.ContainsKey("old.txt"));
        Assert.Equal(expected, target.Files["nested/new.txt"]);
        Assert.Equal(2, target.MutationCallCount);
        Assert.Contains("Moved old.txt to nested/new.txt", result.Content, StringComparison.Ordinal);
        Assert.Contains("non-atomic", result.Content, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(result.PresentationPayloadJson!);
        Assert.Equal("nested/new.txt", Assert.Single(payload.RootElement.GetProperty("Files").EnumerateArray()).GetProperty("Path").GetString());
    }

    [Fact]
    public async Task ApplyPatch_Move_PermissionIncludesSourceAndDestination()
    {
        var (source, _, context) = CreateSource(new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" }));

        var permission = await source.BuildPermissionRequestAsync(context, MoveRequest());

        Assert.NotNull(permission);
        Assert.True(permission.IsMutation);
        Assert.Equal(["old.txt", "nested/new.txt"], permission.ResourceReferences);
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("one")]
    public async Task ApplyPatch_Move_ExistingDestinationRejectsWholePatchBeforeMutation(string destination)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one", ["nested/new.txt"] = destination });
        var (source, _, context) = CreateSource(target);
        var request = new AgentToolRequest("apply_patch", PatchArguments(
            "*** Begin Patch\n*** Add File: first.txt\n+first\n*** Update File: old.txt\n*** Move to: nested/new.txt\n*** End Patch"));

        var result = await source.ExecuteAsync(context, request);

        Assert.True(result.IsError);
        Assert.Equal(0, target.MutationCallCount);
        Assert.Equal("one", target.Files["old.txt"]);
        Assert.Equal(destination, target.Files["nested/new.txt"]);
        Assert.False(target.Files.ContainsKey("first.txt"));
        Assert.Contains("already exists", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyPatch_Move_DestinationCreatedDuringExecutionIsNeverOverwrittenOrDeletedEvenWithIdenticalText()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" })
        { BeforeMutationCall = 1, BeforeMutation = current => current.Files["nested/new.txt"] = "one" };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest());

        Assert.True(result.IsError);
        Assert.Equal(1, target.MutationCallCount);
        Assert.Equal("one", target.Files["old.txt"]);
        Assert.Equal("one", target.Files["nested/new.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_Move_RejectedSourceRemovalSafelyCompensatesTheDestination()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" })
        { FailMutationCall = 2 };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest("@@\n-one\n+two"));

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Equal("one", target.Files["old.txt"]);
        Assert.False(target.Files.ContainsKey("nested/new.txt"));
        Assert.Contains("Applied patch file changes were restored", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("No applied patch changes remain", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, target.MutationCallCount);
    }

    [Fact]
    public async Task ApplyPatch_Move_FailedSourceRestorationRetainsDestinationForRecovery()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" })
        {
            CancelMutationCall = 3,
            BeforeMutationCall = 3,
            BeforeMutation = current => current.Files["old.txt"] = "concurrent replacement",
        };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveThenAddRequest());

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Equal("concurrent replacement", target.Files["old.txt"]);
        Assert.Equal("two", target.Files["nested/new.txt"]);
        Assert.Contains("retained for recovery", result.Content, StringComparison.Ordinal);
        Assert.Equal(4, target.MutationCallCount);
    }

    [Fact]
    public async Task ApplyPatch_Move_NativeTargetPreservesBomAndMixedEndings()
    {
        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        var (source, context) = await CreateLocalSourceAsync(scope, root);
        var bytes = Encoding.UTF8.GetBytes("\uFEFFone\r\nlast\n");
        await File.WriteAllBytesAsync(Path.Combine(root, "old.txt"), bytes);

        var result = await source.ExecuteAsync(context, MoveRequest());

        Assert.False(result.IsError, result.Content);
        Assert.False(File.Exists(Path.Combine(root, "old.txt")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "nested/new.txt")));
    }

    [Fact]
    public async Task ApplyPatch_Move_DestinationInstructionsAreRequiredBeforeMutation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        var (source, context) = await CreateLocalSourceAsync(scope, root);
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "one");
        context = context with { SessionId = Guid.NewGuid(), TranscriptEpoch = 1 };
        var existingScope = new AgentToolRequest("edit", "{\"path\":\"old.txt\",\"oldString\":\"one\",\"newString\":\"two\"}");
        Assert.NotNull(await source.PreflightExecutionAsync(context, existingScope));
        Assert.Null(await source.PreflightExecutionAsync(context, existingScope));
        await File.WriteAllTextAsync(Path.Combine(root, "nested/AGENTS.md"), "destination-specific instructions");

        var result = await source.ExecuteAsync(context, MoveRequest());

        Assert.True(result.IsError);
        Assert.Equal("files-prompt-context-refresh-required", result.ErrorCode);
        Assert.Contains("AGENTS.md", result.Content, StringComparison.Ordinal);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(root, "old.txt")));
        Assert.False(File.Exists(Path.Combine(root, "nested/new.txt")));
    }

    [Theory]
    [InlineData("@@\n missing")]
    [InlineData("@@\n-missing\n+two")]
    public async Task ApplyPatch_Move_InvalidContextOrEditFailsBeforeAnyMutation(string hunk)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" });
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest(hunk));

        Assert.True(result.IsError);
        Assert.Equal("patch-preflight-failed", result.ErrorCode);
        Assert.Equal(0, target.MutationCallCount);
        Assert.Equal("one", Assert.Single(target.Files).Value);
    }

    [Fact]
    public async Task ApplyPatch_Move_IncompleteSourceCannotBecomeACompleteDestination()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "partial" }) { ResultsTruncated = true };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest());

        Assert.True(result.IsError);
        Assert.Equal(0, target.MutationCallCount);
        Assert.Single(target.Files);
    }

    [Fact]
    public async Task ApplyPatch_Move_UnapprovedOutsideDestinationLeavesSourceUntouched()
    {
        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        var outside = Path.Combine(scope.RootPath, "outside");
        Directory.CreateDirectory(outside);
        var (source, context) = await CreateLocalSourceAsync(scope, root);
        await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "one");

        var result = await source.ExecuteAsync(context, MoveRequest(destination: Path.Combine(outside, "new.txt")));

        Assert.True(result.IsError);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(root, "old.txt")));
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
    }

    [FileSystemFact(TestFileSystemCapability.DirectorySymbolicLinks, "Move destination containment requires directory symbolic links.")]
    public async Task ApplyPatch_Move_LinkedDestinationCannotEscapeTheWorkspace()
    {
        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        var outside = Path.Combine(scope.RootPath, "outside");
        Directory.CreateDirectory(outside);
        var (source, context) = await CreateLocalSourceAsync(scope, root);
        await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "one");
        Directory.CreateSymbolicLink(Path.Combine(root, "nested"), outside);

        var result = await source.ExecuteAsync(context, MoveRequest());

        Assert.True(result.IsError);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(root, "old.txt")));
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
    }

    [Theory]
    [InlineData("old.txt", "old.txt", "")]
    [InlineData("old.txt", "OLD.txt", "")]
    [InlineData("old.txt", "nested/new.txt", "*** Delete File: old.txt\n")]
    [InlineData("old.txt", "nested/new.txt", "*** Add File: nested/new.txt\n+other\n")]
    public async Task ApplyPatch_Move_OverlappingPathsRejectWholePatch(string sourcePath, string destination, string extra)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" });
        var (source, _, context) = CreateSource(target);
        var request = new AgentToolRequest("apply_patch", PatchArguments(
            $"*** Begin Patch\n*** Update File: {sourcePath}\n*** Move to: {destination}\n{extra}*** End Patch"));

        var result = await source.ExecuteAsync(context, request);

        Assert.True(result.IsError);
        Assert.Equal(0, target.MutationCallCount);
        Assert.Single(target.Files);
    }

    [Fact]
    public async Task ApplyPatch_Move_PathLimitIncludesBothPathsBeforePermissionResolution()
    {
        var target = new TrackingPlanningExecutionTarget();
        var (source, context) = CreateSourceForTarget(target);
        var patch = "*** Begin Patch\n" + string.Concat(Enumerable.Range(0, 33)
            .Select(index => $"*** Update File: old-{index}.txt\n*** Move to: new-{index}.txt\n")) + "*** End Patch";

        var permission = await source.BuildPermissionRequestAsync(context, new("apply_patch", PatchArguments(patch)));

        Assert.NotNull(permission);
        Assert.Equal(AgentPermissionBoundaryIds.Unknown, permission.BoundaryId);
        Assert.Equal(0, target.ResolveCallCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ApplyPatch_Move_CancellationBeforeEachStepCompensatesAndPropagates(int step)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" }) { CancelMutationCall = step };
        var (source, _, context) = CreateSource(target);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await source.ExecuteAsync(context, MoveRequest()));

        Assert.Equal("one", target.Files["old.txt"]);
        Assert.False(target.Files.ContainsKey("nested/new.txt"));
        Assert.Equal(step == 1 ? 1 : 3, target.MutationCallCount);
    }

    [Fact]
    public async Task ApplyPatch_Move_UnconfirmedDestinationOwnershipRetainsRecoveryFile()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" }) { CancelAfterMutationCall = 1 };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest("@@\n-one\n+two"));

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Equal("one", target.Files["old.txt"]);
        Assert.Equal("two", target.Files["nested/new.txt"]);
        Assert.Contains("ownership could not be confirmed", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, target.MutationCallCount);
    }

    [Fact]
    public async Task ApplyPatch_Move_ConcurrentSourceChangeDoesNotDeleteItOrDiscardRecoveryDestination()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" })
        { BeforeMutationCall = 2, BeforeMutation = current => current.Files["old.txt"] = "concurrent source" };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest("@@\n-one\n+two"));

        Assert.True(result.IsError);
        Assert.Equal("concurrent source", target.Files["old.txt"]);
        Assert.Equal("two", target.Files["nested/new.txt"]);
        Assert.Contains("retained for recovery", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, target.MutationCallCount);
    }

    [Fact]
    public async Task ApplyPatch_Move_UnavailableSourceProbeAfterRemovalRetainsTheOnlyRecoveryFile()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" })
        { FailAfterMutationCall = 2, ReprobeUnavailablePath = "old.txt" };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest());

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.False(target.Files.ContainsKey("old.txt"));
        Assert.Equal("one", target.Files["nested/new.txt"]);
        Assert.Contains("retained for recovery", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, target.MutationCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPatch_Move_UnconfirmedSourceRemovalNeverRecreatesAnUnownedAbsence(bool thisCallRemovedIt)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" })
        {
            FailMutationCall = thisCallRemovedIt ? null : 2,
            FailAfterMutationCall = thisCallRemovedIt ? 2 : null,
            BeforeMutationCall = thisCallRemovedIt ? null : 2,
            BeforeMutation = current => current.Files.Remove("old.txt"),
        };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest());

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.False(target.Files.ContainsKey("old.txt"));
        Assert.Equal("one", target.Files["nested/new.txt"]);
        Assert.Contains("ownership could not be confirmed", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, target.MutationCallCount);
    }

    [Fact]
    public async Task ApplyPatch_Move_AcknowledgedRemovalIsRestoredBeforeDestinationCleanup()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" }) { FailMutationCall = 3 };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveThenAddRequest());

        Assert.True(result.IsError);
        Assert.Equal("one", target.Files["old.txt"]);
        Assert.False(target.Files.ContainsKey("nested/new.txt"));
        Assert.False(target.Files.ContainsKey("trigger.txt"));
        Assert.Equal(5, target.MutationCallCount);
        Assert.Contains("Applied patch file changes were restored", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("No applied patch changes remain", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyPatch_Move_UnconfirmedCancelledRemovalRetainsDestination()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" }) { CancelAfterMutationCall = 2 };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest());

        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.False(target.Files.ContainsKey("old.txt"));
        Assert.Equal("one", target.Files["nested/new.txt"]);
        Assert.Equal(2, target.MutationCallCount);
    }

    [Fact]
    public async Task ApplyPatch_Move_AmbiguousRemovalPreservesUnsafeDispatchWithoutRetry()
    {
        var target = new AmbiguousMoveRemovalTarget();
        var (source, _, context) = CreateSource(target);

        var error = await Assert.ThrowsAsync<SunderRpcException>(async () => await source.ExecuteAsync(context, MoveRequest()));

        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, error.Error.RetrySafety);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.False(target.Files.ContainsKey("old.txt"));
        Assert.Equal("one", target.Files["nested/new.txt"]);
        Assert.Equal(2, target.MutationCallCount);
    }

    [Theory]
    [InlineData("old.txt", "AGENTS.md")]
    [InlineData("AGENTS.md", "new.txt")]
    public async Task ApplyPatch_Move_InstructionDocumentsCannotBypassCompoundPatchPolicy(string from, string to)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { [from] = "one" });
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, MoveRequest(source: from, destination: to));

        Assert.True(result.IsError);
        Assert.Equal("files-agents-compound-patch-rejected", result.ErrorCode);
        Assert.Equal(0, target.MutationCallCount);
        Assert.Equal("one", target.Files[from]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPatch_RejectedMutationWithIdenticalConcurrentTextDoesNotRollBackAnotherWriter(bool update)
    {
        var target = new MemoryExecutionTarget(update ? new Dictionary<string, string> { ["file.txt"] = "old" } : [])
        { BeforeMutationCall = 1, BeforeMutation = current => current.Files["file.txt"] = "new" };
        var (source, _, context) = CreateSource(target);
        var patch = update ? "*** Update File: file.txt\n@@\n-old\n+new" : "*** Add File: file.txt\n+new";

        var result = await source.ExecuteAsync(context, new("apply_patch", PatchArguments($"*** Begin Patch\n{patch}\n*** End Patch")));

        Assert.True(result.IsError);
        Assert.Equal("new", target.Files["file.txt"]);
        Assert.Equal(1, target.MutationCallCount);
    }

    private static AgentToolRequest MoveRequest(string hunk = "", string source = "old.txt", string destination = "nested/new.txt")
        => new("apply_patch", PatchArguments($"*** Begin Patch\n*** Update File: {source}\n*** Move to: {destination}\n"
            + (hunk.Length == 0 ? string.Empty : hunk + "\n") + "*** End Patch"));

    private static AgentToolRequest MoveThenAddRequest()
        => new("apply_patch", PatchArguments("*** Begin Patch\n*** Update File: old.txt\n*** Move to: nested/new.txt\n@@\n-one\n+two\n*** Add File: trigger.txt\n+trigger\n*** End Patch"));

    private sealed class AmbiguousMoveRemovalTarget()
        : MemoryExecutionTarget(new Dictionary<string, string> { ["old.txt"] = "one" })
    {
        public override async ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
        {
            await base.DeleteFileAsync(context, request, cancellationToken);
            throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "test.move-removal-ambiguous", "Removal reply was lost.",
                SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        }
    }
}
