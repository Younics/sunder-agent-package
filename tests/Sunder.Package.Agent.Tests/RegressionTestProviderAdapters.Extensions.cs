using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using WorkerModels = Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestPromptContextProvider(IAgentPromptContextContributor contributor)
    : AgentServices.IPromptContextContributorService
{
    public ValueTask<AgentPromptContextContributorDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentPromptContextContributorDescriptor(contributor.ContributorId,
            contributor.DisplayName, (contributor as IAgentPromptContextAcknowledgmentSink)?.AcknowledgmentSinkId));
    }

    public ValueTask<AgentPromptContextContribution?> ContributeAsync(AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
        => contributor.ContributeContextAsync(request, cancellationToken);

    public ValueTask AcknowledgeAsync(AgentPromptContextReceipt request, CancellationToken cancellationToken = default)
        => contributor is IAgentPromptContextAcknowledgmentSink sink
            ? sink.AcknowledgePromptContextAsync(request, cancellationToken)
            : ValueTask.CompletedTask;
}

internal sealed class RegressionTestProfileCapabilityProvider(IAgentProfileCapabilityConsumer consumer)
    : AgentServices.IProfileCapabilityConsumerService
{
    public ValueTask<AgentProfileCapabilityConsumerSnapshot> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProfileCapabilityConsumerSnapshot(consumer.ConsumerId,
            consumer.DisplayName, consumer.ListConsumedCapabilities()));
    }
}

internal sealed class RegressionTestSelectableCapabilityProvider(IAgentProfileSelectableCapabilityProvider provider)
    : AgentServices.ISelectableCapabilityProviderService
{
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentServiceIdentity(provider.ProviderId, provider.DisplayName));
    }

    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListAsync(AgentProfileRecord? profile,
        CancellationToken cancellationToken = default)
        => provider.ListCapabilitiesAsync(new(profile), cancellationToken);

    public async IAsyncEnumerable<AgentCapabilityCatalogChange> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (provider is not IAgentProfileSelectableCapabilityChangeNotifier notifier)
        {
            yield return new(0);
            yield break;
        }

        var changes = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        long revision = 0;
        void OnChanged() => changes.Writer.TryWrite(Interlocked.Increment(ref revision));
        notifier.SelectableCapabilitiesChanged += OnChanged;
        try
        {
            // Subscribe before publishing the baseline so a concurrent invalidation is queued.
            yield return new(0);
            await foreach (var next in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return new(next);
        }
        finally
        {
            notifier.SelectableCapabilitiesChanged -= OnChanged;
            changes.Writer.TryComplete();
        }
    }
}

internal sealed class RegressionTestWorkspaceEditorProvider(AgentServices.IWorkspaceEditorService contributor)
    : AgentServices.IWorkspaceEditorService
{
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
        => contributor.DescribeAsync(cancellationToken);

    public ValueTask<bool> CanEditAsync(WorkerModels.AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
        => contributor.CanEditAsync(context, cancellationToken);

    public ValueTask<IReadOnlyList<WorkerModels.AgentEditorSection>> GetSectionsAsync(WorkerModels.AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
        => contributor.GetSectionsAsync(context, cancellationToken);

    public ValueTask<WorkerModels.AgentEditorSaveResult> SaveSectionAsync(WorkerModels.AgentWorkspaceEditorContext context,
        WorkerModels.AgentEditorSaveRequest request, CancellationToken cancellationToken = default)
        => contributor.SaveSectionAsync(context, request, cancellationToken);
}

internal interface IRegressionTestBehaviorRunControlCallback
{
    ValueTask<IAgentBehaviorLoopRuntime> BindAsync(ServiceReference<AgentServices.IRunControlService> runControl,
        Guid invocationId, CancellationToken cancellationToken);
}

// The received reference remains the single authority for every run operation.
// This maps the business runtime to its generated service client without rediscovery.
internal sealed class RegressionTestBehaviorRunControlCallback : IRegressionTestBehaviorRunControlCallback
{
    public async ValueTask<IAgentBehaviorLoopRuntime> BindAsync(ServiceReference<AgentServices.IRunControlService> runControl,
        Guid invocationId, CancellationToken cancellationToken)
    {
        await runControl.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return new RegressionTestBehaviorRunControlRuntime(runControl, invocationId);
    }
}

internal sealed class RegressionTestBehaviorRunControlRuntime(
    ServiceReference<AgentServices.IRunControlService> runControl,
    Guid invocationId) : IAgentBehaviorLoopRuntime, IAgentInnerBehaviorLoopRuntime,
    IAgentRunBudgetRuntime, IAgentRunActivitySink, IAgentSessionContextSelectionRuntime,
    IAgentPromptContextAcknowledgmentRuntime
{
    public bool IsCurrentRun()
        => CallAsync((service, token) => service.IsCurrentAsync(invocationId, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public IReadOnlyList<AgentTurnRecord> ListTurns()
        => CallAsync((service, token) => service.ListTurnsAsync(invocationId, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit)
        => CallAsync((service, token) => service.ListRecentTurnsAsync(invocationId, limit, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default)
        => CallAsync((service, token) => service.BuildInstructionContextAsync(invocationId, token), cancellationToken);

    public async ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default)
        => (await CallAsync((service, token) => service.ListReadyToolsAsync(invocationId, token), cancellationToken)
            .ConfigureAwait(false)).Select(static descriptor => new AgentRuntimeTool(descriptor,
                AIFunctionFactory.CreateDeclaration(descriptor.ToolId, descriptor.Description, ParseSchema(descriptor.ArgumentsJsonSchema))))
            .ToArray();

    public async ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        var type = typeof(AgentChatClientContext).Assembly.GetType("Sunder.Package.Agent.Services.AgentModelChatClient", true)!;
        Func<AgentChatStreamRequest, CancellationToken, IAsyncEnumerable<AgentChatStreamEvent>> stream =
            (request, token) => StreamChatAsync(request, context.CorrelationAttributes, token);
        return (IChatClient)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [context, stream], culture: null)!;
    }

    public AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary)
        => CallAsync((service, token) => service.SaveCheckpointAsync(invocationId, status, summary, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public void LogEvent(PackageLogLevel level, string eventName, string message, long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null)
        => CallAsync((service, token) => service.LogEventAsync(invocationId, level.ToString(), eventName, message,
                elapsedMilliseconds, ToJsonAttributes(attributes), token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content)
        => CallAsync((service, token) => service.UpsertAssistantTurnAsync(invocationId, assistantTurn, content, token),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public AgentTurnRecord CompleteAssistantTurn(AgentTurnRecord assistantTurn)
        => CallAsync((service, token) => service.CompleteAssistantTurnAsync(invocationId, assistantTurn, token),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public ValueTask PublishLifecycleEventAsync(AgentLifecycleEventKind kind, AgentRunStatus status,
        AgentTurnRecord? triggerTurn = null, AgentRunCheckpointRecord? checkpoint = null, bool isInterrupted = false,
        CancellationToken cancellationToken = default)
        => CallAsync((service, token) => service.PublishLifecycleAsync(invocationId, kind, status, triggerTurn, checkpoint,
            isInterrupted, token), cancellationToken);

    public ValueTask<AgentToolCallOutcome> InvokeToolAsync(AgentToolCallRequest toolCall, AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default)
        => CallAsync((service, token) => service.InvokeToolAsync(invocationId, toolCall, assistantTurn, token), cancellationToken);

    public ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(IReadOnlyList<AgentToolCallRequest> toolCalls,
        AgentTurnRecord? assistantTurn, CancellationToken cancellationToken = default)
        => CallAsync((service, token) => service.InvokeToolsAsync(invocationId, toolCalls, assistantTurn, token), cancellationToken);

    public ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(AgentBehaviorLoopContext context,
        CancellationToken cancellationToken = default)
        => CallAsync((service, token) => service.RunDefaultAsync(invocationId, context, token), cancellationToken);

    public void SelectSessionContextProjection(AgentSessionPromptProjection projection)
        => CallAsync((service, token) => service.SelectSessionContextAsync(invocationId, projection, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public void ReportRunActivity(AgentRunActivityKind kind, string text)
        => CallAsync((service, token) => service.ReportActivityAsync(invocationId, kind, text, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public AgentRunBudgetState GetRunBudgetState()
        => CallAsync((service, token) => service.GetRunBudgetAsync(invocationId, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public AgentRunBudgetState ChargeRunBudget(AgentRunBudgetCharge charge)
        => CallAsync((service, token) => service.ChargeRunBudgetAsync(invocationId, charge, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public ValueTask AcknowledgePromptContextAsync(IReadOnlyList<AgentPromptContextReceiptBlock> blocks,
        CancellationToken cancellationToken)
        => CallAsync((service, token) => service.AcknowledgePromptContextAsync(invocationId, blocks, token), cancellationToken);

    public void DiscardPromptContextAcknowledgment()
        => CallAsync((service, token) => service.DiscardPromptContextAsync(invocationId, token), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    private async ValueTask<T> CallAsync<T>(Func<AgentServices.IRunControlService, CancellationToken, ValueTask<T>> call,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runControl.RetirementToken);
        await runControl.EnsureCurrentAsync(operation.Token).ConfigureAwait(false);
        var result = await call(runControl.Service, operation.Token).ConfigureAwait(false);
        await runControl.EnsureCurrentAsync(operation.Token).ConfigureAwait(false);
        return result;
    }

    private async ValueTask CallAsync(Func<AgentServices.IRunControlService, CancellationToken, ValueTask> call,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runControl.RetirementToken);
        await runControl.EnsureCurrentAsync(operation.Token).ConfigureAwait(false);
        await call(runControl.Service, operation.Token).ConfigureAwait(false);
        await runControl.EnsureCurrentAsync(operation.Token).ConfigureAwait(false);
    }

    private async ValueTask EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runControl.RetirementToken);
        await runControl.EnsureCurrentAsync(operation.Token).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(AgentChatStreamRequest request,
        IReadOnlyDictionary<string, object?>? correlationAttributes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runControl.RetirementToken);
        await runControl.EnsureCurrentAsync(operation.Token).ConfigureAwait(false);
        await foreach (var item in runControl.Service.StreamChatAsync(invocationId, request,
            ToJsonAttributes(correlationAttributes), operation.Token).WithCancellation(operation.Token).ConfigureAwait(false))
        {
            yield return item;
        }
        await runControl.EnsureCurrentAsync(operation.Token).ConfigureAwait(false);
    }

    private static JsonElement ParseSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false });

        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, JsonElement>? ToJsonAttributes(IReadOnlyDictionary<string, object?>? attributes)
        => attributes?.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal);
}

internal sealed class RegressionTestBehaviorLoopProvider(IAgentBehaviorLoop loop,
    IRegressionTestBehaviorRunControlCallback runControlCallback) : AgentServices.IBehaviorLoopService
{
    public ValueTask<AgentBehaviorLoopDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(loop.Descriptor);
    }

    public async ValueTask<AgentBehaviorLoopResult> RunAsync(AgentBehaviorLoopContext context, Guid invocationId,
        ServiceReference<AgentServices.IRunControlService> runControl, CancellationToken cancellationToken = default)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            runControl.RetirementToken);
        await runControl.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
        var runtime = await runControlCallback.BindAsync(runControl, invocationId, invocation.Token).ConfigureAwait(false);
        await runControl.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
        return await loop.RunAsync(context, runtime, invocation.Token).ConfigureAwait(false);
    }
}

internal sealed class RegressionTestChildRunProvider(IAgentChildRunExecutor executor)
    : AgentServices.IChildRunExecutorService
{
    public ValueTask<AgentChildRunResult> RunChildAsync(AgentChildRunRequest request,
        CancellationToken cancellationToken = default)
        => executor.RunChildAsync(request, cancellationToken);
}

internal sealed class RegressionTestObservedToolProvider(IAgentToolSource source)
    : AgentServices.IToolExecutionObservedService
{
    public ValueTask<string> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(source.SourceId);
    }

    public async IAsyncEnumerable<AgentObservedToolExecutionEvent> ExecuteAsync(AgentToolExecutionContext context,
        AgentToolRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = System.Threading.Channels.Channel.CreateUnbounded<AgentObservedToolExecutionEvent>();
        var execution = ProduceAsync();
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await execution.ConfigureAwait(false);
        }

        async Task ProduceAsync()
        {
            try
            {
                var observedContext = context with { };
                var dispatch = typeof(AgentToolExecutionContext).GetProperty("DispatchStarted", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var original = (Action?)dispatch.GetValue(context);
                dispatch.SetValue(observedContext, (Action)(() =>
                {
                    original?.Invoke();
                    events.Writer.TryWrite(new AgentToolDispatchedEvent());
                }));
                var observed = source.GetType().GetInterfaces().FirstOrDefault(type =>
                    type.FullName == "Sunder.Package.Agent.Contracts.Contracts.IAgentObservedToolExecution");
                ValueTask<AgentToolResult> pending;
                try
                {
                    pending = observed is null ? source.ExecuteAsync(observedContext, request, stopping.Token)
                        : (ValueTask<AgentToolResult>)observed.GetMethod("ExecuteAsync")!.Invoke(source, [observedContext, request, stopping.Token])!;
                }
                catch (TargetInvocationException error) when (error.InnerException is not null)
                { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
                var result = await pending.ConfigureAwait(false);
                events.Writer.TryWrite(new AgentToolExecutionCompletedEvent(result));
                events.Writer.TryComplete();
            }
            catch (Exception exception) { events.Writer.TryComplete(exception); }
        }
    }
}
