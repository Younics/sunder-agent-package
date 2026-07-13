using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Package.Agent.Tools.Shell;
using Sunder.Package.Agent.Tools.Web;
using Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class FilesToolSourceRefactorTests
{
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
    public async Task ApplyPatch_CancellationAfterMutation_CompensatesAndPropagates()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "old second",
        }) { CancelMutationCall = 2 };
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
    public async Task Glob_BoundsResultsAndReportsTruncation()
    {
        var target = new MemoryExecutionTarget
        {
            ProcessOutput = string.Join('\n', Enumerable.Range(1, 1005).Select(index => $"file-{index:D4}.txt")),
        };
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, new AgentToolRequest("glob", "{\"pattern\":\"*.txt\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.True(result.WasTruncated);
        Assert.Contains("truncated", result.Summary, StringComparison.OrdinalIgnoreCase);
        using var payload = JsonDocument.Parse(result.StructuredPayloadJson!);
        Assert.Equal(1000, payload.RootElement.GetArrayLength());
        Assert.DoesNotContain("file-1001.txt", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesShellAndWeb_InvalidRequestPresentation_UsesSharedMarkdownShape()
    {
        const string malformedJson = "{";
        var (files, _, _) = CreateSource(new MemoryExecutionTarget());
        var shell = new ShellToolSource(new TestExtensionCatalog(new MemoryExecutionTarget()));
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
            "shell" => await new ShellToolSource(new TestExtensionCatalog(target)).ExecuteAsync(
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

    private static (FilesToolSource Source, MemoryExecutionTarget Target, AgentToolExecutionContext Context) CreateSource(MemoryExecutionTarget target)
    {
        var catalog = new TestExtensionCatalog(target);
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var binding = new AgentWorkspaceBindingRecord(
            "binding",
            workspace.WorkspaceId,
            PackageExtensionPoints.ExecutionTargets.Id,
            target.Descriptor.TargetId,
            "primary-execution-target",
            true,
            0,
            now,
            now);
        return (new FilesToolSource(catalog), target, new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding));
    }

    private static string PatchArguments(string patchText)
        => JsonSerializer.Serialize(new { patchText });

    private static AgentToolPresentationRequest PresentationRequest(string toolId, string argumentsJson)
        => new(toolId, argumentsJson, null, null, null, null, false, null, null);

    private static string RawRequestBlock(string markdown)
    {
        var fence = markdown.IndexOf("```json", StringComparison.Ordinal);
        Assert.True(fence >= 0);
        return markdown[fence..];
    }

    private sealed class TestExtensionCatalog(IAgentExecutionTarget target) : IPackageExtensionCatalog
    {
        public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => string.Equals(extensionPoint.Id, PackageExtensionPoints.ExecutionTargets.Id, StringComparison.Ordinal)
                ? [((TContract)(object)target)]
                : [];

        public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => GetExtensions(extensionPoint)
                .Select(extension => new PackageExtensionContribution<TContract>("test.package", extension))
                .ToArray();
    }

    private class MemoryExecutionTarget : IAgentProcessExecutionTarget
    {
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
            SupportsFiles: true,
            SupportsSearch: true);

        public Dictionary<string, string> Files { get; }

        public int MutationCallCount { get; private set; }

        public int? FailMutationCall { get; init; }

        public int? CancelMutationCall { get; init; }

        public int? FailAfterMutationCall { get; init; }

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
            return ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, Files.ContainsKey(path)));
        }

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(
                ShellTimedOut ? 124 : 0,
                ProcessOutput,
                ShellTimedOut,
                WasTruncated: ResultsTruncated));

        public ValueTask<AgentShellCommandResult> ExecuteProcessAsync(AgentExecutionTargetContext context, AgentProcessCommandRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(0, ProcessOutput));

        public virtual ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastReadRequest = request;
            return ValueTask.FromResult(new AgentFileReadResult(request.Path, Files[request.Path], WasTruncated: ResultsTruncated));
        }

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
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
            if (MutationCallCount == FailAfterMutationCall)
            {
                return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Scripted post-mutation failure.", IsError: true, ErrorCode: "scripted-failure"));
            }

            return ValueTask.FromResult(new AgentFileMutationResult(request.Path, "Written."));
        }

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
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
}
