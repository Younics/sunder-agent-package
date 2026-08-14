extern alias SkillsWorker;
extern alias SubagentsWorker;

using Sunder.Package.Agent.Builder;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;
using SkillOwnerJson = SkillsWorker::Sunder.Package.Agent.Skills.Runtime.OwnerJson;
using SkillOwnerOperationException = SkillsWorker::Sunder.Package.Agent.Skills.Runtime.SkillOwnerOperationException;
using SubagentOwnerJson = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime.SubagentOwnerJson;

namespace Sunder.Package.Agent.Tests;

public sealed class OwnerRpcFaultMappingTests
{
    private const string UnexpectedMessage = "unexpected-provider-secret-canary";

    [Fact]
    public void OwnerAdapters_MapOnlyExplicitDomainExceptions()
    {
        var unexpected = new InvalidOperationException(UnexpectedMessage);

        Assert.False(McpManagementRpcProvider.ShouldMap(unexpected));
        Assert.False(BuilderRpcProvider.ShouldMap(unexpected));
        Assert.False(SkillOwnerJson.ShouldMap(unexpected));
        Assert.False(SubagentOwnerJson.ShouldMap(unexpected));
        Assert.False(AgentOwnerRuntimeRpc.ShouldMap(unexpected));
        Assert.False(MemoryInspectorRpc.ShouldMap(unexpected));

        Assert.True(McpManagementRpcProvider.ShouldMap(new McpManagementOperationException("expected")));
        Assert.True(BuilderRpcProvider.ShouldMap(new BuilderRpcDomainException("agent.builder.invalid-request", "expected")));
        Assert.True(SkillOwnerJson.ShouldMap(new SkillOwnerOperationException("expected")));
        Assert.True(SubagentOwnerJson.ShouldMap(new SubagentOwnerOperationException("expected")));
        Assert.True(AgentOwnerRuntimeRpc.ShouldMap(new AgentOwnerOperationException("expected")));
        Assert.True(MemoryInspectorRpc.ShouldMap(new MemoryInspectorOperationException("expected")));
    }

    [Fact]
    public void ExtensionOwnerMappings_PreserveOnlyAuthoredDomainMessages()
    {
        var skills = SkillOwnerJson.Map(new SkillOwnerOperationException("expected skills failure"));
        var subagents = SubagentOwnerJson.Map(
            new SubagentOwnerOperationException("expected subagents failure"));

        Assert.Equal(SunderRpcErrorKind.Domain, skills.Error.Kind);
        Assert.Equal("skills.owner.operation-failed", skills.Error.Code);
        Assert.Equal("expected skills failure", skills.Error.Message);
        Assert.DoesNotContain(UnexpectedMessage, skills.Error.Message, StringComparison.Ordinal);
        Assert.Equal(SunderRpcErrorKind.Domain, subagents.Error.Kind);
        Assert.Equal("subagents.owner.operation-failed", subagents.Error.Code);
        Assert.Equal("expected subagents failure", subagents.Error.Message);
        Assert.DoesNotContain(UnexpectedMessage, subagents.Error.Message, StringComparison.Ordinal);
    }
}
