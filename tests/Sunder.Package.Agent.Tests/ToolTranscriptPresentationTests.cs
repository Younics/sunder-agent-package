extern alias AgentApp;
extern alias FilesWorker;
extern alias ShellWorker;
extern alias WebWorker;

using System.Text.Json;
using AgentApp::Sunder.Package.Agent.PackageViews;
using FilesWorker::Sunder.Package.Agent.Tools.Files;
using ShellWorker::Sunder.Package.Agent.Tools.Shell;
using Sunder.Package.Agent.Contracts.Models;
using WebWorker::Sunder.Package.Agent.Tools.Web.Backends;
using WebWorker::Sunder.Package.Agent.Tools.Web.Services;
using WebWorker::Sunder.Package.Agent.Tools.Web;
using Xunit;
using AgentToolPresentationService = AgentApp::Sunder.Package.Agent.Services.AgentToolPresentationService;
using AppModels = AgentApp::Sunder.Package.Agent.Contracts.Models;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class ToolTranscriptPresentationTests
{
    [Fact]
    public async Task FilesToolSource_Edit_EmitsPresentationPayloadWithActualLineNumbers()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("one\ntwo\nold\nsame\nfive");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);
        var argumentsJson = JsonSerializer.Serialize(new
        {
            path = "notes.txt",
            oldString = "old\nsame",
            newString = "new\nsame",
        });

        catalog.AddProvider(RegressionTestBindings.ToolSources, source, "younics.ai.agent.tools.files");
        var sourceProvider = await catalog.GetRequiredProviderAsync(catalog.ToolSources);
        var result = await sourceProvider.Service.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("edit", argumentsJson));

        Assert.False(result.IsError, result.Content);
        Assert.NotNull(result.PresentationPayloadJson);

        Assert.NotNull(AgentApp::Sunder.Package.Agent.Shared.PackageViews.ToolDiffViewModel.TryCreate(
            "edit", "{}", null, null, false, result.PresentationPayloadJson));
        Assert.Null(AgentApp::Sunder.Package.Agent.Shared.PackageViews.ToolDiffViewModel.TryCreate(
            "edit", "{}", null, null, false,
            result.PresentationPayloadJson.Replace("younics.ai.agent.tools.files.diff.v1", "sunder.file-diff.v1", StringComparison.Ordinal)));

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("edit", argumentsJson, textContent: result.Content, resultSummary: result.Summary, presentationPayloadJson: result.PresentationPayloadJson),
            new AgentToolPresentationService());
        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        var file = Assert.Single(details.ToolDiffFiles);

        Assert.Equal("Edited notes.txt: 1 replacement(s), +1/-1 lines.", row.HeaderDetailText);
        Assert.Equal("one\ntwo\nnew\nsame\nfive", target.Content);
        Assert.Contains(file.Lines, line => line.IsDeleted && line.LineNumberText == "3" && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.LineNumberText == "3" && line.Text == "new");
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "4" && line.Text == "same");
        Assert.DoesNotContain(file.Lines, line => line.MarkerText is "+" or "-" or "@@");
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatch_EmitsPresentationPayloadWithActualLineNumbers()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("one\ntwo\nold\nsame\nfive");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);
        var patchText = """
            *** Begin Patch
            *** Update File: notes.txt
            @@
             two
            -old
            +new
             same
            *** End Patch
            """;
        var argumentsJson = ApplyPatchArgs(patchText);

        catalog.AddProvider(RegressionTestBindings.ToolSources, source, "younics.ai.agent.tools.files");
        var sourceProvider = await catalog.GetRequiredProviderAsync(catalog.ToolSources);
        var result = await sourceProvider.Service.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("apply_patch", argumentsJson));

        Assert.False(result.IsError, result.Content);
        Assert.NotNull(result.PresentationPayloadJson);
        Assert.Equal("one\ntwo\nnew\nsame\nfive", target.Content);

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("apply_patch", argumentsJson, textContent: result.Content, resultSummary: result.Summary, presentationPayloadJson: result.PresentationPayloadJson),
            new AgentToolPresentationService());
        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        var file = Assert.Single(details.ToolDiffFiles);

        Assert.Equal("Applied 1 patch operation to 1 file", row.HeaderDetailText);
        Assert.Equal("Patch", details.ToolDiffSectionTitle);
        Assert.False(details.ShowMarkdownDetails);
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "2" && line.Text == "two");
        Assert.Contains(file.Lines, line => line.IsDeleted && line.LineNumberText == "3" && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.LineNumberText == "3" && line.Text == "new");
        Assert.Contains(file.Lines, line => line.IsContext && line.LineNumberText == "4" && line.Text == "same");
        Assert.DoesNotContain(file.Lines, line => line.MarkerText is "+" or "-" or "@@");
    }

    [Fact]
    public async Task FilesToolSource_ApplyPatchPresentation_CompactsHeaderAndKeepsPatchInDetails()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var source = new FilesToolSource(scope.Context);
        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var patchText = """
            *** Begin Patch
            *** Add File: first.txt
            +first
            *** Update File: second.txt
            @@
            -old
            +new
            *** End Patch
            """;

        var presentation = await sourceProbe.PresentationAsync(new AgentToolPresentationRequest(
            "apply_patch",
            ApplyPatchArgs(patchText),
            ResultSummary: null,
            TextContent: "Applied patch.",
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.Equal("Applied 2 patch operations to 2 files", presentation!.HeaderText);
        Assert.Contains("*** Add File: first.txt", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Equal("Applied patch.", presentation.OutputText);
    }

    [Fact]
    public async Task FilesToolSource_GlobPresentation_UsesSemanticDetailsAndCleanSummary()
    {
        using var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(RegressionTestBindings.ToolSources, source);
        var service = catalog.CreateToolPresentationService(scope.Context);
        var argumentsJson = JsonSerializer.Serialize(new { pattern = "*.html", path = "." });

        var presentation = await service.ResolveAsync(CreateToolItem("glob", argumentsJson, textContent: "index.html", resultSummary: "Found 1 match."));

        Assert.Equal("Found 1 match", presentation.HeaderText);
        Assert.Contains("**Request**", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Contains("Pattern: *.html", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", presentation.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_LazyResultDetailsUseAuthoritativeCallArguments()
    {
        using var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(RegressionTestBindings.ToolSources, source);
        var service = catalog.CreateToolPresentationService(scope.Context);
        var patchText = """
            *** Begin Patch
            *** Update File: index.html
            @@
            -old
            +new
            *** End Patch
            """;
        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                "apply_patch",
                ApplyPatchArgs(patchText),
                textContent: "Updated index.html",
                resultSummary: "Applied 1 patch operation to 1 file."),
            service);

        Assert.False(row.HasMaterializedDetails);
        var details = await TranscriptToolTestHarness.ExpandAsync(row);

        Assert.Equal("Applied 1 patch operation to 1 file.", row.HeaderDetailText);
        Assert.Contains("*** Update File: index.html", details.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", details.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
        Assert.True(details.HasToolDiff);
        Assert.False(details.ShowMarkdownDetails);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_ApplyPatchBuildsVisualDiffAfterExpansion()
    {
        using var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(RegressionTestBindings.ToolSources, source);
        var service = catalog.CreateToolPresentationService(scope.Context);
        var patchText = """
            *** Begin Patch
            *** Update File: /workspace/src/Foo.cs
            @@
            -old
            +new
            *** End Patch
            """;

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("apply_patch", ApplyPatchArgs(patchText), textContent: "Updated /workspace/src/Foo.cs", resultSummary: "Applied 1 patch operation to 1 file."),
            service);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        var file = Assert.Single(details.ToolDiffFiles);
        Assert.Equal("Applied 1 patch operation to 1 file.", row.HeaderDetailText);
        Assert.Equal("Patch", details.ToolDiffSectionTitle);
        Assert.True(details.HasToolDiff);
        Assert.False(details.ShowMarkdownDetails);
        Assert.Equal("/workspace/src/Foo.cs", file.Path);
        Assert.Equal(1, file.AddedLineCount);
        Assert.Equal(1, file.DeletedLineCount);
        Assert.DoesNotContain(file.Lines, line => line.IsHunk || line.Text.Contains("@@", StringComparison.Ordinal));
        Assert.Contains(file.Lines, line => line.IsDeleted && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.Text == "new");
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_ApplyPatchFailureKeepsLazyVisualDiff()
    {
        using var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(RegressionTestBindings.ToolSources, source);
        var service = catalog.CreateToolPresentationService(scope.Context);
        var patchText = """
            *** Begin Patch
            *** Update File: /workspace/src/Foo.cs
            @@
            -old
            +new
            *** End Patch
            """;

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                "apply_patch",
                ApplyPatchArgs(patchText),
                textContent: "### File tool failed\n\nPatch hunk did not match the current file content.",
                resultSummary: "Patch hunk did not match the current file content.",
                isError: true),
            service);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        Assert.Equal("Patch hunk did not match the current file content.", row.HeaderDetailText);
        Assert.True(details.HasToolDiff);
        Assert.False(details.ShowMarkdownDetails);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_EditBuildsFocusedDiffAfterExpansion()
    {
        var service = new AgentToolPresentationService();
        var argumentsJson = JsonSerializer.Serialize(new
        {
            path = "/workspace/src/Foo.cs",
            oldString = "old\nline",
            newString = "new\nline",
            replaceAll = false,
        });

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("edit", argumentsJson, textContent: "Wrote 42 character(s).", resultSummary: "Wrote 42 character(s)."),
            service);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        var file = Assert.Single(details.ToolDiffFiles);
        Assert.Equal("Wrote 42 character(s).", row.HeaderDetailText);
        Assert.Equal("Diff", details.ToolDiffSectionTitle);
        Assert.True(details.HasToolDiff);
        Assert.True(details.ShowMarkdownDetails);
        Assert.DoesNotContain(file.Lines, line => line.IsHunk || line.Text.Contains("@@", StringComparison.Ordinal));
        Assert.Contains(file.Lines, line => line.IsDeleted && line.Text == "old");
        Assert.Contains(file.Lines, line => line.IsAdded && line.Text == "new");
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_MalformedApplyPatchFallsBackToLazyMarkdownDetails()
    {
        using var catalog = new TestExtensionCatalog();
        using var scope = TestScope.Create();
        var source = new FilesToolSource(scope.Context);
        catalog.AddProvider(RegressionTestBindings.ToolSources, source);
        var service = catalog.CreateToolPresentationService(scope.Context);

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("apply_patch", ApplyPatchArgs("not a patch"), textContent: "failed", resultSummary: null),
            service);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        Assert.False(details.HasToolDiff);
        Assert.True(details.ShowMarkdownDetails);
        Assert.Contains("not a patch", details.DetailMarkdownBuilder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebTools_PresentationResolvers_AreResolvedThroughInstalledPackageSource()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var settings = new WebToolsSettingsService(scope.Context);
        using var exa = new ExaWebSearchBackend(settings);
        using var parallel = new ParallelWebSearchBackend(settings);
        await using var source = new WebToolSource(new WebFetchTool(new WebFetchService()),
            new WebSearchTool(new WebSearchService(exa, parallel, settings), settings));
        catalog.AddProvider(RegressionTestBindings.ToolSources, source);
        var service = catalog.CreateToolPresentationService(scope.Context);

        var search = await service.ResolveAsync(CreateToolItem("web_search", JsonSerializer.Serialize(new { query = "avalonia docs", maxResults = 3 })));
        var fetch = await service.ResolveAsync(CreateToolItem("web_fetch", JsonSerializer.Serialize(new { url = "https://example.com", format = "markdown" })));

        Assert.Equal("avalonia docs", search.HeaderText);
        Assert.Contains("Query: avalonia docs", search.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", search.DetailMarkdown, StringComparison.Ordinal);
        Assert.Equal("https://example.com", fetch.HeaderText);
        Assert.Contains("URL: https://example.com", fetch.DetailMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", fetch.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellToolSource_Presentation_CompactsLongCommandAndKeepsCommandInDetails()
    {
        using var catalog = new TestExtensionCatalog();
        var source = new ShellToolSource();
        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.shell");
        var command = string.Join('\n', Enumerable.Range(1, 8).Select(index => $"echo line-{index}"));
        var argumentsJson = JsonSerializer.Serialize(new { command });

        var presentation = await sourceProbe.PresentationAsync(new AgentToolPresentationRequest(
            "shell",
            argumentsJson,
            ResultSummary: null,
            TextContent: "done",
            StructuredPayloadJson: null,
            SourcesJson: null,
            IsError: false,
            ErrorCode: null,
            BackendId: null));

        Assert.NotNull(presentation);
        Assert.Equal("command: 8 lines", presentation!.HeaderText);
        Assert.Contains("echo line-8", presentation.DetailMarkdown, StringComparison.Ordinal);
        Assert.Equal("done", presentation.OutputText);
    }

    [Fact]
    public async Task AgentToolPresentationService_Fallback_CompactsUnknownMultilineArguments()
    {
        var service = new AgentToolPresentationService();
        var argumentsJson = JsonSerializer.Serialize(new { script = string.Join('\n', Enumerable.Range(1, 6).Select(index => $"line-{index}")) });

        var presentation = await service.ResolveAsync(CreateToolItem("unknown_tool", argumentsJson));

        Assert.Equal("script: 6 lines", presentation.HeaderText);
        Assert.Contains("line-6", presentation.DetailMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_ShowsDetailsAndOutputTogetherAfterExpansion()
    {
        using var row = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("unknown_tool", JsonSerializer.Serialize(new { query = "weather tomorrow" }), textContent: "tool output", resultSummary: "Tool completed."),
            new AgentToolPresentationService());

        Assert.True(row.HasHeaderDetail);
        Assert.False(row.HasMaterializedDetails);
        var details = await TranscriptToolTestHarness.ExpandAsync(row);
        Assert.True(details.HasMarkdownDetails);
        Assert.True(details.HasOutput);
        Assert.Contains("tool output", details.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentToolInvocationRowViewModel_LazyDetailPredicatesIncludeEveryVisualKind()
    {
        using var outputOnly = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("output_only", "{}", textContent: "output"),
            new AgentToolPresentationService());
        using var metadataOnly = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                "metadata_only",
                "{}",
                errorCode: "tool-failed",
                backendId: "local"),
            new AgentToolPresentationService());
        using var diffOnly = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem(
                "edit",
                JsonSerializer.Serialize(new
                {
                    path = "src/Foo.cs",
                    oldString = "old",
                    newString = "new",
                })),
            new AgentToolPresentationService());
        using var markdownOnly = TranscriptToolTestHarness.CreateAgentRow(
            CreateToolTurn(),
            CreateToolItem("markdown_only", "{\"query\":\"details\"}"),
            new AgentToolPresentationService());

        var outputDetails = await TranscriptToolTestHarness.ExpandAsync(outputOnly);
        var metadataDetails = await TranscriptToolTestHarness.ExpandAsync(metadataOnly);
        var diffDetails = await TranscriptToolTestHarness.ExpandAsync(diffOnly);
        diffDetails.DetailMarkdownBuilder.Clear();
        var markdownDetails = await TranscriptToolTestHarness.ExpandAsync(markdownOnly);

        Assert.True(outputDetails.HasOutput);
        Assert.False(outputDetails.HasMarkdownDetails);
        Assert.True(metadataDetails.HasMetadata);
        Assert.False(metadataDetails.HasMarkdownDetails);
        Assert.True(diffDetails.HasToolDiff);
        Assert.False(diffDetails.HasMarkdownDetails);
        Assert.True(markdownDetails.HasMarkdownDetails);
        Assert.All(new[] { outputOnly, metadataOnly, diffOnly, markdownOnly }, row =>
        {
            Assert.True(row.HasDetails);
            Assert.True(row.IsExpanded);
            Assert.NotNull(row.ExpandedDetails);
        });
    }

    [Fact]
    public void AgentTextTranscriptRowViewModel_UnchangedAttachmentsDoNotResetCollection()
    {
        var metadata = new AppModels.AgentAttachmentMetadata(
            Guid.NewGuid(),
            "diagram.png",
            "image/png",
            AppModels.AgentAttachmentKind.Image,
            128,
            "hash",
            "attachments/diagram.png",
            IsText: false,
            WasTruncated: false);
        var attachment = new AgentTranscriptAttachmentViewModel(metadata);
        var row = new AgentTextTranscriptRowViewModel(
            new AppModels.AgentTurnRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                AppModels.AgentMessageRole.User,
                AppModels.AgentTurnKind.Message,
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            string.Empty,
            [attachment]);
        var collectionChanges = 0;
        var propertyChanges = 0;
        row.Attachments.CollectionChanged += (_, _) => collectionChanges++;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AgentTextTranscriptRowViewModel.HasAttachments))
            {
                propertyChanges++;
            }
        };

        row.ReplaceAttachments([new AgentTranscriptAttachmentViewModel(metadata)]);

        Assert.Same(attachment, Assert.Single(row.Attachments));
        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, propertyChanges);
    }

    private static AppModels.AgentTurnRecord CreateToolTurn(AgentTurnKind kind = AgentTurnKind.ToolResult)
    {
        var now = DateTimeOffset.UtcNow;
        return new AppModels.AgentTurnRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AppModels.AgentMessageRole.Assistant,
            kind switch
            {
                AgentTurnKind.Message => AppModels.AgentTurnKind.Message,
                AgentTurnKind.ToolCall => AppModels.AgentTurnKind.ToolCall,
                AgentTurnKind.ToolResult => AppModels.AgentTurnKind.ToolResult,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            [],
            now,
            now);
    }

    private static AppModels.AgentTurnItemRecord CreateToolItem(
        string toolId,
        string? argumentsJson,
        string? textContent = null,
        string? resultSummary = null,
        AgentTurnItemKind kind = AgentTurnItemKind.ToolResult,
        bool isError = false,
        string? presentationPayloadJson = null,
        string? errorCode = null,
        string? backendId = null)
    {
        var turnId = Guid.NewGuid();
        return new AppModels.AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            kind switch
            {
                AgentTurnItemKind.Text => AppModels.AgentTurnItemKind.Text,
                AgentTurnItemKind.ToolCall => AppModels.AgentTurnItemKind.ToolCall,
                AgentTurnItemKind.ToolResult => AppModels.AgentTurnItemKind.ToolResult,
                AgentTurnItemKind.Attachment => AppModels.AgentTurnItemKind.Attachment,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            textContent,
            "call-1",
            toolId,
            argumentsJson,
            resultSummary,
            StructuredPayloadJson: null,
            SourcesJson: null,
            WasTruncated: false,
            IsError: isError,
            ErrorCode: errorCode,
            BackendId: backendId,
            PresentationPayloadJson: presentationPayloadJson);
    }
}
