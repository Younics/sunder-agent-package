extern alias BuilderWorker;

using BuilderWorker::Sunder.Package.Agent.Builder;
using BuilderModels = BuilderWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using PermissionSurface = BuilderWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Permission_Surface.IPermissionSurfaceService;
using Xunit;
using System.Text.Json;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderToolSourceTests
{
    [Theory]
    [InlineData("{\"state\":\"failed\",\"outcomeUnknown\":true}", true, "tool-outcome-ambiguous")]
    [InlineData("{\"state\":\"failed\",\"outcomeUnknown\":false}", true, "maker.operation-failed")]
    [InlineData("{\"state\":\"succeeded\"}", false, null)]
    public void OperationResultsPreserveAgentAmbiguitySemantics(string json, bool error, string? errorCode)
    {
        using var result = JsonDocument.Parse(json);
        var rendered = BuilderToolSource.Result("maker_hotreload_package", result.RootElement, "Observed operation.");
        Assert.Equal(error, rendered.IsError);
        Assert.Equal(errorCode, rendered.ErrorCode);
    }

    [Theory]
    [InlineData("maker_build_package", "{\"entryPoint\":\"relative.csproj\"}", false)]
    [InlineData("maker_hotreload_package", "{\"path\":\"/project/Package.csproj\"}", false)]
    [InlineData("maker_get_package_info", "{\"entryPoint\":\"/project/Package.csproj\",\"entryPoint\":\"/other/Package.csproj\"}", false)]
    [InlineData("maker_get_package_logs", "{\"entryPoint\":\"/project/Package.csproj\",\"operationId\":\"untrusted\"}", false)]
    [InlineData("maker_build_package", "{\"entryPoint\":\"/project/Package.csproj\"}", false)]
    [InlineData("maker_hotreload_package", "{\"entryPoint\":\"/project/Package.csproj\"}", true)]
    [InlineData("maker_build_package", "{\"entryPoint\":\"/project/app/index.html\"}", false)]
    [InlineData("maker_hotreload_package", "{\"entryPoint\":\"/project/package.json\"}", false)]
    [InlineData("maker_hotreload_package", "{\"entryPoint\":\"/project/sunder.package.json\"}", true)]
    [InlineData("maker_get_package_info", "{\"entryPoint\":\"/project/sunder.package.json\",\"operationId\":null}", true)]
    [InlineData("maker_package", "{\"package\":\"/project/sunder.package.json\",\"query\":null}", true)]
    [InlineData("maker_view", "{\"target\":\"v1\",\"role\":null,\"name\":null,\"text\":null,\"within\":null,\"value\":null,\"limit\":null,\"cursor\":null}", true)]
    [InlineData("maker_interact", "{\"target\":\"e1\",\"action\":\"invoke\",\"text\":null,\"verify\":null}", true)]
    [InlineData("maker_interact", "{\"target\":\"e1\",\"action\":\"invoke\",\"text\":\"unexpected\"}", false)]
    [InlineData("maker_logs", "{\"package\":\"p1\",\"limit\":\"10\"}", false)]
    [InlineData("maker_get_package_logs", "{\"entryPoint\":\"/project/sunder.package.json\",\"limit\":20,\"minLevel\":\"error\",\"source\":\"runtime\",\"since\":\"2026-10-24T03:35:00+08:00\"}", true)]
    [InlineData("maker_get_package_logs", "{\"entryPoint\":\"/project/sunder.package.json\",\"limit\":101}", false)]
    [InlineData("maker_get_package_logs", "{\"entryPoint\":\"/project/sunder.package.json\",\"limit\":\"20\"}", false)]
    [InlineData("maker_get_package_logs", "{\"entryPoint\":\"/project/sunder.package.json\",\"since\":\"2026-10-24T03:35:00\"}", false)]
    [InlineData("maker_get_package_logs", "{\"entryPoint\":\"/project/sunder.package.json\",\"since\":\"2026-10-24T04:00:00Z\",\"until\":\"2026-10-24T03:00:00Z\"}", false)]
    [InlineData("maker_inspect_package_view", "{\"entryPoint\":\"/project/app/index.html\",\"viewId\":\"local.main\"}", false)]
    [InlineData("maker_inspect_package_view", "{\"entryPoint\":\"/project/Package.csproj\",\"viewId\":\"local.main\"}", true)]
    [InlineData("maker_inspect_package_view", "{\"entryPoint\":\"/project/Package.csproj\",\"viewId\":\"local.main\",\"selector\":\"body\"}", false)]
    [InlineData("maker_interact_package_view", "{\"entryPoint\":\"/project/Package.csproj\",\"viewId\":\"local.main\",\"snapshotId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"elementReference\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"action\":\"invoke\"}", true)]
    [InlineData("maker_interact_package_view", "{\"entryPoint\":\"/project/Package.csproj\",\"viewId\":\"local.main\",\"snapshotId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"elementReference\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"action\":\"evaluate-script\"}", false)]
    [InlineData("maker_interact_package_view", "{\"entryPoint\":\"/project/Package.csproj\",\"viewId\":\"local.main\",\"snapshotId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"elementReference\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"action\":\"invoke\",\"text\":\"unadvertised input\"}", false)]
    public async Task ArgumentValidationIsPureAndRejectsMalformedIntentBeforeAnyAuthority(string tool, string json, bool valid)
    {
        await using var runtime = new BuilderRuntimeService(null!);
        var source = new BuilderToolSource(runtime);
        var result = await source.ValidateArgumentsAsync(new BuilderModels.AgentToolRequest { ToolId = tool, ArgumentsJson = json });
        Assert.Equal(valid, result is null);
        if (result is not null) Assert.True(result.IsError);
        Assert.False(runtime.HasDevelopmentApp);
    }

    [Fact]
    public async Task InvalidEntryPointExplainsTheProjectFileRatherThanOnlyRequestingAnAbsolutePath()
    {
        await using var runtime = new BuilderRuntimeService(null!);
        var source = new BuilderToolSource(runtime);
        var result = await source.ValidateArgumentsAsync(new BuilderModels.AgentToolRequest
            { ToolId = "maker_build_package", ArgumentsJson = "{\"entryPoint\":\"app/index.html\"}" });
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
        Assert.Equal(new[] { "maker_package", "maker_apply", "maker_operation", "maker_logs", "maker_view", "maker_screenshot", "maker_interact" }, tools.Select(tool => tool.ToolId));
        Assert.Single(tools, tool => !string.IsNullOrWhiteSpace(tool.RuntimeInstructions));
        Assert.DoesNotContain(tools, tool => tool.ToolId == "maker_build_package");
        Assert.All(tools, tool => Assert.DoesNotContain("watch", tool.ToolId, StringComparison.OrdinalIgnoreCase));
        var permissions = await ((PermissionSurface)source).DescribeAsync(default);
        Assert.DoesNotContain(permissions.Actions, action => action.ActionId == "maker.build");
        Assert.Equal(BuilderModels.AgentPermissionDecision.Ask,
            Assert.Single(Assert.Single(permissions.Actions, action => action.ActionId == "maker.logs").Boundaries).DefaultDecision);
        Assert.Equal(BuilderModels.AgentPermissionDecision.Allow,
            Assert.Single(Assert.Single(permissions.Actions, action => action.ActionId == "maker.inspect").Boundaries).DefaultDecision);
        Assert.False(runtime.HasDevelopmentApp);
    }
}
