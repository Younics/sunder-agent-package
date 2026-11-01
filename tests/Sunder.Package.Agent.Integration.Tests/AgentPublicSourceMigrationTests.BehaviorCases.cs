using System.Reflection;
using System.Runtime.Loader;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    // These probes used to run sequentially behind one Fact. Each now has its own
    // result and disposable scope; an early failure cannot hide the other boundaries.
    [AgentPublicSourceMigrationFact]
    public Task PromptContext_PreservesOrderingBoundsAndAuthority()
        => WithPublicRole(probe => VerifyPromptContextAsync(probe.Assembly, probe.Descriptors));

    [AgentPublicSourceMigrationFact]
    public Task ChildRuns_PreserveCorrelationCancellationAndPersistence()
        => WithPublicRole(probe => VerifyChildRunsAsync(probe.Assembly, probe.Descriptors[RegressionTestBindings.ChildRunExecutor]));

    [AgentPublicSourceMigrationFact]
    public Task BehaviorLoop_PreservesGeneratedRunAndCallbackSemantics()
        => WithPublicRole(probe => VerifyBehaviorLoopAsync(probe.Assembly, probe.Descriptors));

    [AgentPublicSourceMigrationFact]
    public Task RunControl_PreservesStreamsCancellationAndRecovery()
        => WithPublicRole(probe => VerifyRunControlAsync(probe.Assembly, probe.Descriptors["younics.ai.agent.run.control"]));

    [AgentPublicSourceMigrationFact]
    public Task RuntimeCatalog_PreservesCurrentSessionAndTranscriptProjections()
        => WithPublicRole(probe => VerifyRuntimeCatalogAsync(probe.Assembly, probe.Descriptors["younics.ai.agent.runtime.catalog"]));

    [AgentPublicSourceMigrationFact]
    public Task ExecutionTarget_PreservesFileAndProcessBoundaries()
        => WithPublicRole(probe => VerifyExecutionTargetAsync(probe.Root, probe.Context, probe.Assembly,
            probe.Descriptors[RegressionTestBindings.ExecutionTargets]));

    [AgentPublicSourceMigrationFact]
    public Task Extensions_PreserveGeneratedProviderSemantics()
        => WithPublicRole(probe => VerifyExtensionsAsync(probe.Root, probe.Context, probe.Assembly, probe.Descriptors));

    [AgentPublicSourceMigrationFact]
    public Task Tools_PreserveInvocationRetirementAndDisposal()
        => WithPublicRole(probe => VerifyToolLifecycleAsync(probe.Root, probe.Context, probe.Assembly, probe.Descriptors));

    [AgentPublicSourceMigrationFact]
    public Task Chat_PreservesGeneratedStreamingAndCancellation()
        => WithPublicRole(probe => VerifyChatAsync(probe.Root, probe.Context, probe.Assembly,
            probe.Descriptors["younics.ai.agent.chat.provider"]));

    [AgentPublicSourceMigrationFact]
    public Task SessionCleaners_PreserveErasureAndRetrySemantics()
        => WithPublicRole(probe => VerifySessionCleanersAsync(probe.Assembly, probe.Descriptors["younics.ai.agent.session.cleaner"]));

    private static async Task WithPublicRole(Func<PublicRoleProbe, Task> exercise)
    {
        var baselinePath = Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot, "src/Sunder.Package.Agent/sunder-contracts.owned.json");
        var baseline = File.ReadAllBytes(baselinePath);
        var root = Directory.CreateTempSubdirectory("sunder-public-role-").FullName;
        var context = new AssemblyLoadContext("public-role-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var descriptors = CurrentPublicRpcDescriptors.ContractIds.ToDictionary(id => id, CurrentPublicRpcDescriptors.Get, StringComparer.Ordinal);
            await exercise(new(root, context, typeof(Services.AgentRunControlService).Assembly, descriptors));
            Assert.Equal(baseline, File.ReadAllBytes(baselinePath));
        }
        finally
        {
            context.Unload();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record PublicRoleProbe(string Root, AssemblyLoadContext Context, Assembly Assembly,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> Descriptors);
}
