extern alias BuilderWorker;

using BuilderWorker::Sunder.Package.Agent.Builder;
using BuilderModels = BuilderWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using PermissionSurface = BuilderWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Permission_Surface.IPermissionSurfaceService;
using Xunit;
using System.Text.Json.Nodes;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderToolSourceTests
{
    [Theory]
    [InlineData("{\"status\":\"unknown\"}", true, "tool-outcome-ambiguous")]
    [InlineData("{\"status\":\"failed\"}", true, "maker.operation-failed")]
    [InlineData("{\"status\":\"applied\"}", false, null)]
    public void OperationResultsPreserveAgentAmbiguitySemantics(string json, bool error, string? errorCode)
    {
        var rendered = BuilderToolSource.CompactResult("maker_apply", JsonNode.Parse(json)!, "Observed operation.",
            failed: error, unknown: errorCode == "tool-outcome-ambiguous");
        Assert.Equal(error, rendered.IsError);
        Assert.Equal(errorCode, rendered.ErrorCode);
    }

    [Theory]
    [InlineData("maker_package", "{\"package\":\"/project/sunder.package.json\",\"query\":null}", true)]
    [InlineData("maker_view", "{\"target\":\"v1\",\"role\":null,\"name\":null,\"text\":null,\"within\":null,\"value\":null,\"limit\":null,\"cursor\":null}", true)]
    [InlineData("maker_interact", "{\"target\":\"e1\",\"action\":\"invoke\",\"text\":null,\"verify\":null}", true)]
    [InlineData("maker_interact", "{\"target\":\"e1\",\"action\":\"invoke\",\"text\":\"unexpected\"}", false)]
    [InlineData("maker_logs", "{\"package\":\"p1\",\"limit\":\"10\"}", false)]
    [InlineData("maker_logs", "{\"package\":\"/project/sunder.package.json\",\"limit\":20,\"level\":\"error\",\"source\":\"runtime\",\"since\":\"2026-10-24T03:35:00+08:00\"}", true)]
    [InlineData("maker_apply", "{\"package\":\"/project/Package.csproj\"}", true)]
    [InlineData("maker_apply", "{\"package\":\"/project/package.json\"}", false)]
    [InlineData("maker_package", "{\"package\":\"/project/Package.csproj\",\"package\":\"/other/Package.csproj\"}", false)]
    [InlineData("maker_logs", "{\"package\":\"p1\",\"limit\":51}", false)]
    [InlineData("maker_logs", "{\"package\":\"p1\",\"since\":\"2026-10-24T03:35:00\"}", false)]
    [InlineData("maker_logs", "{\"package\":\"p1\",\"since\":\"2026-10-24T04:00:00Z\",\"until\":\"2026-10-24T03:00:00Z\"}", false)]
    [InlineData("maker_view", "{\"target\":\"v1\",\"selector\":\"body\"}", false)]
    [InlineData("maker_interact", "{\"target\":\"e1\",\"action\":\"evaluate-script\"}", false)]
    [InlineData("maker_apply", "{\"package\":\"relative.csproj\"}", false)]
    [InlineData("maker_apply", "{\"path\":\"/project/Package.csproj\"}", false)]
    [InlineData("maker_logs", "{\"package\":\"p1\",\"operationId\":\"untrusted\"}", false)]
    public async Task ArgumentValidationIsPureAndRejectsMalformedIntentBeforeAnyAuthority(string tool, string json, bool valid)
    {
        await using var runtime = new BuilderRuntimeService(null!);
        var source = new BuilderToolSource(runtime);
        var result = await source.ValidateArgumentsAsync(new BuilderModels.AgentToolRequest { ToolId = tool, ArgumentsJson = json });
        Assert.Equal(valid, result is null);
        if (result is not null) Assert.True(result.IsError);
        Assert.False(runtime.HasDevelopmentApp);
    }

    [Theory]
    [InlineData("maker_get_package_info")]
    [InlineData("maker_get_package_logs")]
    [InlineData("maker_hotreload_package")]
    [InlineData("maker_inspect_package_view")]
    [InlineData("maker_interact_package_view")]
    [InlineData("maker_build_package")]
    public async Task RetiredToolIdsCannotAcquireReadinessPermissionOrExecutionAuthority(string tool)
    {
        await using var runtime = new BuilderRuntimeService(null!);
        var source = new BuilderToolSource(runtime);
        var request = new BuilderModels.AgentToolRequest { ToolId = tool, ArgumentsJson = "{\"entryPoint\":\"/project/Package.csproj\"}" };
        Assert.NotNull(await source.ValidateArgumentsAsync(request));
        Assert.Null(await source.GetReadinessAsync(new() { ToolId = tool, Context = null! }));
        Assert.Null(await source.PermissionAsync(new() { Request = request, Context = null! }));
        Assert.NotNull(await source.PreflightAsync(new() { Request = request, Context = null! }));
        Assert.True((await source.ExecuteAsync(new() { Request = request, Context = null! })).IsError);
        Assert.False(runtime.HasDevelopmentApp);
    }

    [Fact]
    public async Task InvalidEntryPointExplainsTheProjectFileRatherThanOnlyRequestingAnAbsolutePath()
    {
        await using var runtime = new BuilderRuntimeService(null!);
        var source = new BuilderToolSource(runtime);
        var result = await source.ValidateArgumentsAsync(new BuilderModels.AgentToolRequest
            { ToolId = "maker_apply", ArgumentsJson = "{\"package\":\"app/index.html\"}" });
        Assert.NotNull(result);
        Assert.Contains(".csproj", result.Content);
        Assert.Contains("sunder.package.json", result.Content);
    }

    [Fact]
    public async Task ExplicitToolsAreDiscoverableWithoutCreatingJobsOrAnAppView()
    {
        await using var runtime = new BuilderRuntimeService(null!);
        var source = new BuilderToolSource(runtime);
        var tools = await source.ListToolsAsync(null!);
        Assert.Equal(new[] { "maker_package", "maker_apply", "maker_logs", "maker_view", "maker_screenshot", "maker_interact" }, tools.Select(tool => tool.ToolId));
        Assert.Single(tools, tool => !string.IsNullOrWhiteSpace(tool.RuntimeInstructions));
        Assert.DoesNotContain(tools, tool => tool.ToolId == "maker_build_package");
        Assert.All(tools, tool => Assert.DoesNotContain("watch", tool.ToolId, StringComparison.OrdinalIgnoreCase));
        Assert.All(tools, tool => Assert.Empty(tool.Aliases ?? []));
        var permissions = await ((PermissionSurface)source).DescribeAsync(default);
        Assert.DoesNotContain(permissions.Actions, action => action.ActionId == "maker.build");
        Assert.Equal(BuilderModels.AgentPermissionDecision.Ask,
            Assert.Single(Assert.Single(permissions.Actions, action => action.ActionId == "maker.logs").Boundaries).DefaultDecision);
        Assert.Equal(BuilderModels.AgentPermissionDecision.Allow,
            Assert.Single(Assert.Single(permissions.Actions, action => action.ActionId == "maker.inspect").Boundaries).DefaultDecision);
        Assert.False(runtime.HasDevelopmentApp);
    }
}
