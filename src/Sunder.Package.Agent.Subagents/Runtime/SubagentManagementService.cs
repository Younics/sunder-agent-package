using System.Runtime.CompilerServices;
using IChatProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using IRuntimeCatalogService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Runtime;

[SunderService(Key = "management", ContractId = "younics.ai.agent.subagents.owner", ServiceId = "subagents-management", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "subagents.management")]
[SunderServiceError("subagents.owner.operation-failed", Description = "The requested Subagents operation could not be completed.")]
internal sealed partial class SubagentManagementService(SubagentService service, SubagentEditorCapabilityCatalog capabilities,
    IServiceProviders<IChatProviderService> chats, IServiceProviders<IRuntimeCatalogService> runtimes, SubagentRuntimeChangeStream changes)
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> ManagementAsync(CancellationToken token = default)
        => ProjectAsync(async () =>
        {
            var providers = (await SnapshotChatsAsync(token).ConfigureAwait(false)).OrderBy(item => item.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(item => new SubagentProviderProjection(item.Metadata.ProviderId, item.Metadata.DisplayName, item.PackageId)).ToArray();
            var tools = capabilities.ListLocalToolsAsync(token);
            var selectable = capabilities.ListPackageCapabilitiesAsync(token);
            await Task.WhenAll(tools, selectable).ConfigureAwait(false);
            return new(Subagents: service.ListSubagents(), Providers: providers, Tools: await tools, Capabilities: await selectable);
        });

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> ProviderModelsAsync(string providerId, CancellationToken token = default)
        => ProjectAsync(async () =>
        {
            var contribution = (await SnapshotChatsAsync(token).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.Metadata.ProviderId, Require(providerId), StringComparison.OrdinalIgnoreCase));
            if (contribution is null) return new(Providers: []);
            var models = await SubagentCatalogSelection.InvokeOptionalAsync(contribution, token, static (provider, cancellation) => provider.ListModelsAsync(cancellation)).ConfigureAwait(false);
            var readiness = await SubagentCatalogSelection.InvokeOptionalAsync(contribution, token, static (provider, cancellation) => provider.GetReadinessAsync(cancellation)).ConfigureAwait(false);
            return new(Providers: [new(contribution.Metadata.ProviderId, contribution.Metadata.DisplayName, contribution.PackageId,
                Models: models.IsAvailable ? models.Value : [],
                Readiness: models.IsAvailable && readiness.IsAvailable ? readiness.Value : new AgentProviderReadiness
                { ProviderId = contribution.Metadata.ProviderId, Status = AgentProviderReadinessStatus.Failed, Message = "The chat provider package is no longer available." })]);
        });

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> RuntimeCatalogAsync(CancellationToken token = default)
        => ProjectAsync(() => WithRuntimeAsync(async (runtime, cancellation) =>
        {
            var sessions = await runtime.ListSessionsAsync(cancellation).ConfigureAwait(false);
            var checkpoints = new List<AgentRunCheckpointRecord>();
            foreach (var session in sessions)
                if (await runtime.GetLatestCheckpointAsync(session.SessionId, cancellation).ConfigureAwait(false) is { } checkpoint) checkpoints.Add(checkpoint);
            return new SubagentProjection(Sessions: sessions, Profiles: await runtime.ListProfilesAsync(cancellation).ConfigureAwait(false), Checkpoints: checkpoints);
        }, token));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> RecentTurnsAsync(Guid sessionId, [SunderValueConstraints(Minimum = "1", Maximum = "500")] int limit, CancellationToken token = default)
        => ProjectAsync(() => WithRuntimeAsync(async (runtime, cancellation) => new SubagentProjection(
            TranscriptPage: await SubsessionTranscriptReader.RecentAsync(runtime, sessionId, limit, cancellation).ConfigureAwait(false)), token));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> TurnsBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId,
        [SunderValueConstraints(Minimum = "1", Maximum = "500")] int limit, CancellationToken token = default)
        => ProjectAsync(() => WithRuntimeAsync(async (runtime, cancellation) => new SubagentProjection(
            TranscriptPage: await SubsessionTranscriptReader.BeforeAsync(runtime, sessionId, SubsessionTranscriptReader.Timestamp(createdAtUtc), turnId, limit, cancellation).ConfigureAwait(false)), token));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> TurnsAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId,
        [SunderValueConstraints(Minimum = "1", Maximum = "500")] int limit, CancellationToken token = default)
        => ProjectAsync(() => WithRuntimeAsync(async (runtime, cancellation) => new SubagentProjection(
            TranscriptPage: await SubsessionTranscriptReader.AfterAsync(runtime, sessionId, SubsessionTranscriptReader.Timestamp(createdAtUtc), turnId, limit, cancellation).ConfigureAwait(false)), token));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> AroundTurnAsync(Guid sessionId, Guid turnId, DateTimeOffset createdAtUtc, Guid itemId,
        [SunderValueConstraints(Minimum = "0", Maximum = "30")] int beforeLimit,
        [SunderValueConstraints(Minimum = "0", Maximum = "30")] int afterLimit, CancellationToken token = default)
        => ProjectAsync(() => WithRuntimeAsync(async (runtime, cancellation) => new SubagentProjection(
            AroundTurn: await SubsessionTranscriptReader.AroundAsync(runtime, sessionId, turnId, createdAtUtc, itemId, beforeLimit, afterLimit, cancellation).ConfigureAwait(false)), token));

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> ToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken token = default)
        => ProjectAsync(() => WithRuntimeAsync(async (runtime, cancellation) => new SubagentProjection(
            ToolDetail: await runtime.GetTranscriptToolDetailAsync(request, cancellation).ConfigureAwait(false) is { } detail
                ? SubsessionAroundTurnPayload.FitToolDetail(detail) : null), token));

    [SunderMethod(Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> CreateAsync(string displayName, CancellationToken token = default)
        => Mutate(() => service.CreateSubagent(displayName), token);

    [SunderMethod(Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> SaveAsync(SubagentSaveRequest request, CancellationToken token = default)
        => Mutate(() => service.SaveSubagent(request.SubagentId, request.DisplayName, request.Description, request.Instructions,
            request.ChatProviderId, request.ChatModelId, request.Assignments, request.ChatModelSettingsJson), token);

    [SunderMethod(Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<SunderContent> DeleteAsync(string subagentId, CancellationToken token = default)
        => Mutate(() => { service.DeleteSubagent(Require(subagentId)); return null; }, token);

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"], CompleteOnResponseContentRetirement = true)]
    public async IAsyncEnumerable<SunderContent> ChangesAsync([SunderValueConstraints(Minimum = "0")] long afterRevision,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var change in changes.SubscribeAsync(afterRevision, token).ConfigureAwait(false))
        {
            await using var content = SubagentProjectionContent.Create(change, "subagents-change.json");
            yield return content;
        }
    }

    private ValueTask<IReadOnlyList<SubagentCatalogEntry<IChatProviderService, AgentProviderDescriptor>>> SnapshotChatsAsync(CancellationToken token)
        => SubagentCatalogSelection.SnapshotAsync(chats, token, static (provider, cancellation) => provider.DescribeAsync(cancellation));

    private async ValueTask<T> WithRuntimeAsync<T>(Func<IRuntimeCatalogService, CancellationToken, ValueTask<T>> invoke, CancellationToken token)
    {
        var provider = (await runtimes.ListAsync(token).ConfigureAwait(false)).Providers.FirstOrDefault(value => value.State == ServiceProviderState.Active)
            ?? throw new SubagentOwnerOperationException("The Agent runtime catalog is not available.");
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, provider.RetirementToken);
        try
        {
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            var result = await invoke(provider.Service, invocation.Token).ConfigureAwait(false);
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (SubagentCatalogSelection.IsUnavailable(error, provider.RetirementToken, token))
        { throw new SubagentOwnerOperationException("The Agent runtime catalog became unavailable.", error); }
    }
    private ValueTask<SunderContent> Mutate(Func<Models.SubagentRecord?> mutate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var result = mutate();
            return ValueTask.FromResult(SubagentProjectionContent.Create(new SubagentProjection(Subagents: service.ListSubagents(), Subagent: result)));
        }
        catch (SubagentOwnerOperationException error) { throw SubagentOwnerJson.Map(error); }
    }
    private static async ValueTask<SunderContent> ProjectAsync(Func<ValueTask<SubagentProjection>> operation)
    {
        try { return SubagentProjectionContent.Create(await operation().ConfigureAwait(false)); }
        catch (SubagentOwnerOperationException error) { throw SubagentOwnerJson.Map(error); }
    }
    private static string Require(string? value)
        => !string.IsNullOrWhiteSpace(value) ? value : throw new SubagentOwnerOperationException("A value is required.");
}
