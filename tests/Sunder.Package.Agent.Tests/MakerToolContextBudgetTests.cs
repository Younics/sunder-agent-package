extern alias BuilderWorker;
extern alias OpenAiWorker;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;
using Maker = BuilderWorker::Sunder.Package.Agent.Builder;
using Codex = OpenAiWorker::Sunder.Package.Agent.Provider.OpenAI.Transport.CodexResponsesRequestBuilder;
using ProviderContext = OpenAiWorker::Sunder.Package.Agent.Contracts.Models.AgentChatClientContext;

namespace Sunder.Package.Agent.Tests;

public sealed class MakerToolContextBudgetTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AssembledRequestKeepsOptionalArgumentsOptionalAndBoundsRepeatedWorkflowContext()
    {
        await using var runtime = new Maker.BuilderRuntimeService(null!);
        var descriptors = await new Maker.BuilderToolSource(runtime).ListToolsAsync(null!);
        var declarations = descriptors.Select(descriptor =>
        {
            using var schema = JsonDocument.Parse(descriptor.ArgumentsJsonSchema!);
            return (AITool)AIFunctionFactory.CreateDeclaration(descriptor.ToolId, descriptor.Description, schema.RootElement.Clone());
        }).ToList();
        var request = Codex.Build(new ProviderContext("openai", "openai/gpt-5.5"), [new ChatMessage(ChatRole.User, "Apply the built package and inspect its Send button.")],
            new ChatOptions { Tools = declarations, Instructions = string.Join("\n", descriptors.Select(descriptor => descriptor.RuntimeInstructions).OfType<string>()) }, toolAware: true);
        using var document = JsonDocument.Parse(request.Body);
        var tools = document.RootElement.GetProperty("tools");
        Assert.Equal(6, tools.GetArrayLength());
        var package = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "maker_package");
        Assert.Equal(new[] { "package" }, package.GetProperty("parameters").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("string", package.GetProperty("parameters").GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        var bytes = Encoding.UTF8.GetByteCount(request.Body);
        Assert.InRange(bytes, 1, 16 * 1024);
        var oldInput = JsonSerializer.Serialize(new { entryPoint = "/workspace/example-package/sunder.package.json", viewId = "example-package.main", snapshotId = new string('a', 32), elementReference = new string('b', 32), action = "invoke" });
        var newInput = JsonSerializer.Serialize(new { target = "e" + new string('x', 22) + "_1", action = "invoke" });
        Assert.True(Encoding.UTF8.GetByteCount(newInput) < Encoding.UTF8.GetByteCount(oldInput) / 2);
        output.WriteLine(JsonSerializer.Serialize(new { assembledCodexRequestUtf8Bytes = bytes, instructionCopies = descriptors.Count(descriptor => descriptor.RuntimeInstructions is not null),
            legacyInteractionArgumentBytes = Encoding.UTF8.GetByteCount(oldInput), compactInteractionArgumentBytes = Encoding.UTF8.GetByteCount(newInput), tokenMeasurement = "not measured; UTF-8 bytes only, no provider call" }));
        output.WriteLine(request.Body);
    }
}
