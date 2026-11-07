extern alias AgentTestProviders;

using System.Reflection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Rpc.Public;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

// Uses the ordinary project dependency. The fixture owns generated
// registration/dispatch; this test-side bridge only invokes current typed fakes.
internal static class RegressionTestGeneratedProviders
{
    internal static Assembly FixtureAssembly => typeof(AgentTestProviders::RegressionTestGeneratedProviders.Fixture).Assembly;
    internal static bool TryRegister(GeneratedServiceTestScope receiver, string contractId, object implementation,
        out SunderWorkerProviderRegistration registration)
    {
        object? provider = contractId switch
        {
            RegressionTestBindings.ChatProviders => implementation.GetType().GetInterfaces().Any(type =>
                    type.FullName == typeof(AgentServices.IChatProviderService).FullName
                    || type.FullName == "Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService") ? implementation
                : new RegressionTestChatProvider(AgentRoleBoundary.As<IAgentChatProvider>(implementation), receiver,
                    RegressionTestGeneratedProviderProjection.FromBuild(contractId)),
            RegressionTestBindings.ToolSources => implementation is RegressionTestToolProvider tool ? tool
                : new RegressionTestToolProvider(AgentRoleBoundary.As<IAgentToolSource>(implementation)),
            RegressionTestBindings.ExecutionTargets => implementation is RegressionTestExecutionProvider target ? target
                : new RegressionTestExecutionProvider(AgentRoleBoundary.As<IAgentExecutionTarget>(implementation)),
            RegressionTestBindings.RuntimeCatalog => implementation is IAgentRuntimeCatalog domainCatalog
                ? new RegressionTestRuntimeCatalogProvider(domainCatalog)
                : implementation is AgentServices.IRuntimeCatalogService ? implementation
                : implementation is AgentRuntimeCatalog catalog ? new RegressionTestRuntimeCatalogProvider(AgentRoleBoundary.As<AgentServices.IRuntimeCatalogService>(catalog))
                : new RegressionTestRuntimeCatalogProvider(AgentRoleBoundary.As<IAgentRuntimeCatalog>(implementation)),
            RegressionTestBindings.EmbeddingProviders => implementation is RegressionTestEmbeddingProvider embedding ? embedding
                : new RegressionTestEmbeddingProvider(AgentRoleBoundary.As<IAgentEmbeddingProvider>(implementation)),
            RegressionTestBindings.SessionCleaners => implementation is RegressionTestSessionCleanerProvider cleaner ? cleaner
                : new RegressionTestSessionCleanerProvider(AgentRoleBoundary.As<IAgentSessionDataCleaner>(implementation)),
            RegressionTestBindings.DurableLifecycleObservers => implementation is RegressionTestDurableLifecycleProvider lifecycle ? lifecycle
                : new RegressionTestDurableLifecycleProvider(AgentRoleBoundary.As<IAgentDurableLifecycleObserver>(implementation)),
            RegressionTestBindings.PromptContextContributors => implementation is RegressionTestPromptContextProvider prompt ? prompt
                : new RegressionTestPromptContextProvider(AgentRoleBoundary.As<IAgentPromptContextContributor>(implementation)),
            RegressionTestBindings.SystemPromptContributors => implementation is RegressionTestSystemPromptProvider systemPrompt ? systemPrompt
                : new RegressionTestSystemPromptProvider(AgentRoleBoundary.As<IAgentSystemPromptContributor>(implementation)),
            RegressionTestBindings.PermissionSurfaces => implementation is RegressionTestPermissionProvider permission ? permission
                : new RegressionTestPermissionProvider(AgentRoleBoundary.As<IAgentPermissionSurface>(implementation)),
            RegressionTestBindings.ProfileCapabilityConsumers => implementation is RegressionTestProfileCapabilityProvider profileCapability ? profileCapability
                : new RegressionTestProfileCapabilityProvider(AgentRoleBoundary.As<IAgentProfileCapabilityConsumer>(implementation)),
            RegressionTestBindings.SelectableCapabilityProviders => implementation is RegressionTestSelectableCapabilityProvider selectable ? selectable
                : new RegressionTestSelectableCapabilityProvider(AgentRoleBoundary.As<IAgentProfileSelectableCapabilityProvider>(implementation)),
            RegressionTestBindings.WorkspaceEditors => implementation is RegressionTestWorkspaceEditorProvider editor ? editor
                : new RegressionTestWorkspaceEditorProvider(AgentRoleBoundary.As<AgentServices.IWorkspaceEditorService>(implementation)),
            RegressionTestBindings.BehaviorLoops => implementation is RegressionTestBehaviorLoopProvider loop ? loop
                : new RegressionTestBehaviorLoopProvider(AgentRoleBoundary.As<IAgentBehaviorLoop>(implementation),
                    new RegressionTestBehaviorRunControlCallback()),
            RegressionTestBindings.ChildRunExecutor => implementation is RegressionTestChildRunProvider child ? child
                : new RegressionTestChildRunProvider(AgentRoleBoundary.As<IAgentChildRunExecutor>(implementation)),
            RegressionTestBindings.ObservedToolExecutions => implementation is RegressionTestObservedToolProvider observed ? observed
                : new RegressionTestObservedToolProvider(AgentRoleBoundary.As<IAgentToolSource>(implementation)),
            RegressionTestBindings.WorkspaceExecutionResolver => implementation is RegressionTestWorkspaceResolverProvider resolver ? resolver
                : new RegressionTestWorkspaceResolverProvider(AgentRoleBoundary.As<IAgentWorkspaceExecutionResolver>(implementation)),
            _ => null,
        };
        if (provider is null)
        {
            registration = default!;
            return false;
        }

        var assembly = FixtureAssembly;
        var typeName = contractId switch
        {
            RegressionTestBindings.ChatProviders => "RegressionTestGeneratedProviders.ChatProvider",
            RegressionTestBindings.ToolSources => "RegressionTestGeneratedProviders.ToolSourceProvider",
            RegressionTestBindings.ExecutionTargets => "RegressionTestGeneratedProviders.ExecutionTargetProvider",
            RegressionTestBindings.RuntimeCatalog => "RegressionTestGeneratedProviders.RuntimeCatalogProvider",
            RegressionTestBindings.EmbeddingProviders => "RegressionTestGeneratedProviders.EmbeddingProvider",
            RegressionTestBindings.SessionCleaners => "RegressionTestGeneratedProviders.SessionCleanerProvider",
            RegressionTestBindings.DurableLifecycleObservers => "RegressionTestGeneratedProviders.DurableLifecycleProvider",
            RegressionTestBindings.PromptContextContributors => "RegressionTestGeneratedProviders.PromptContextProvider",
            RegressionTestBindings.SystemPromptContributors => "RegressionTestGeneratedProviders.SystemPromptProvider",
            RegressionTestBindings.PermissionSurfaces => "RegressionTestGeneratedProviders.PermissionProvider",
            RegressionTestBindings.ProfileCapabilityConsumers => "RegressionTestGeneratedProviders.ProfileCapabilityProvider",
            RegressionTestBindings.SelectableCapabilityProviders => "RegressionTestGeneratedProviders.SelectableCapabilityProvider",
            RegressionTestBindings.WorkspaceEditors => "RegressionTestGeneratedProviders.WorkspaceEditorProvider",
            RegressionTestBindings.BehaviorLoops => "RegressionTestGeneratedProviders.BehaviorLoopProvider",
            RegressionTestBindings.ChildRunExecutor => "RegressionTestGeneratedProviders.ChildRunProvider",
            RegressionTestBindings.ObservedToolExecutions => "RegressionTestGeneratedProviders.ObservedToolProvider",
            RegressionTestBindings.WorkspaceExecutionResolver => "RegressionTestGeneratedProviders.WorkspaceResolverProvider",
            _ => throw new InvalidOperationException($"No generated fixture type is registered for '{contractId}'."),
        };
        var fixture = Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!,
            new Func<string, Type, object?[], CancellationToken, object>(new FixtureBridge(provider, receiver,
                RegressionTestGeneratedProviderProjection.FromBuild(contractId)).Invoke))!;
        registration = receiver.Register(fixture, contractId);
        return true;
    }

    internal static object CreateProvider(string family,
        Func<string, Type, object?[], CancellationToken, object> invoke)
        => Activator.CreateInstance(FixtureAssembly.GetType("RegressionTestGeneratedProviders." + family + "Provider", throwOnError: true)!, invoke)!;

    private sealed class FixtureBridge(object implementation, GeneratedServiceTestScope receiver,
        RegressionTestGeneratedProviderProjection projection)
    {
        internal object Invoke(string method, Type resultType, object?[] arguments, CancellationToken cancellationToken)
        {
            var source = SourceMethod(method);
            if (source.ReturnType.IsGenericType && source.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                return typeof(FixtureBridge).GetMethod(nameof(InvokeStream), BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(resultType).Invoke(this, [method, arguments, cancellationToken])!;
            return resultType == typeof(void)
                ? InvokeVoid(method, arguments, cancellationToken)
                : typeof(FixtureBridge).GetMethod(nameof(InvokeResult), BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(resultType).Invoke(this, [method, arguments, cancellationToken])!;
        }

        private async ValueTask<T> InvokeResult<T>(string method, object?[] arguments, CancellationToken cancellationToken)
        {
            var result = await InvokeSourceAsync(method, arguments, cancellationToken).ConfigureAwait(false);
            return (T)(await projection.ProjectAsync(result, typeof(T), receiver, cancellationToken).ConfigureAwait(false))!;
        }

        private async ValueTask InvokeVoid(string method, object?[] arguments, CancellationToken cancellationToken)
            => await InvokeSourceAsync(method, arguments, cancellationToken).ConfigureAwait(false);

        private async IAsyncEnumerable<T> InvokeStream<T>(string method, object?[] arguments,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var source = await InvokeSource(method, arguments, cancellationToken).ConfigureAwait(false);
            var itemType = SourceMethod(method).ReturnType.GenericTypeArguments[0];
            var enumerator = (IAsyncEnumerable<object?>)typeof(FixtureBridge).GetMethod(nameof(CastStream), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(itemType).Invoke(null, [source, cancellationToken])!;
            await foreach (var item in enumerator.WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return (T)(await projection.ProjectAsync(item, typeof(T), receiver, cancellationToken).ConfigureAwait(false))!;
        }

        private async ValueTask<object?> InvokeSourceAsync(string name, object?[] arguments, CancellationToken cancellationToken)
        {
            var pending = await InvokeSource(name, arguments, cancellationToken).ConfigureAwait(false);
            return await AwaitAsync(pending).ConfigureAwait(false);
        }

        private async ValueTask<object> InvokeSource(string name, object?[] arguments, CancellationToken cancellationToken)
        {
            var method = SourceMethod(name);
            var parameters = method.GetParameters();
            var values = Flatten(arguments, parameters[..^1]);
            var converted = new object?[parameters.Length];
            for (var index = 0; index < values.Length; index++)
                converted[index] = await ConvertArgumentAsync(values[index], parameters[index].ParameterType, cancellationToken).ConfigureAwait(false);
            converted[^1] = cancellationToken;
            try { return method.Invoke(implementation, converted)!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private async ValueTask<object?> ConvertArgumentAsync(object? value, Type target, CancellationToken cancellationToken)
        {
            if (target == typeof(AgentToolExecutionContext) && value is not null)
                return await ProjectToolContextAsync(value, cancellationToken).ConfigureAwait(false);
            return await projection.ProjectAsync(value, target, receiver, cancellationToken).ConfigureAwait(false);
        }

        private MethodInfo SourceMethod(string name)
            => implementation.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(candidate => candidate.Name == name)
               ?? (implementation is AgentServices.IRuntimeCatalogService
                   ? typeof(AgentServices.IRuntimeCatalogService).GetMethod(name) : null)
               ?? throw new MissingMethodException(implementation.GetType().FullName, name);

        private async ValueTask<AgentToolExecutionContext> ProjectToolContextAsync(object value, CancellationToken cancellationToken)
        {
            object? Value(string name) => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(value);
            async ValueTask<T?> Project<T>(string name) => (T?)await projection.ProjectAsync(Value(name), typeof(T), receiver, cancellationToken).ConfigureAwait(false);
            var reference = Value(nameof(AgentToolExecutionContext.ExecutionTarget));
            return new AgentToolExecutionContext(
                (Guid?)Value(nameof(AgentToolExecutionContext.SessionId)),
                (string?)Value(nameof(AgentToolExecutionContext.ProfileId)),
                await Project<AgentWorkspaceRecord?>(nameof(AgentToolExecutionContext.Workspace)).ConfigureAwait(false),
                await Project<AgentWorkspaceBindingRecord?>(nameof(AgentToolExecutionContext.ExecutionBinding)).ConfigureAwait(false),
                (bool)Value(nameof(AgentToolExecutionContext.AllowOutsideConfiguredScope))!,
                (Guid?)Value(nameof(AgentToolExecutionContext.RunId)),
                (long?)Value(nameof(AgentToolExecutionContext.RunRevision)),
                (Guid?)Value(nameof(AgentToolExecutionContext.UserTurnId)),
                (string?)Value(nameof(AgentToolExecutionContext.ToolCallId)))
            {
                TranscriptEpoch = (long)Value(nameof(AgentToolExecutionContext.TranscriptEpoch))!,
                ApprovedResourceReferences = (await Project<IReadOnlyList<string>>(nameof(AgentToolExecutionContext.ApprovedResourceReferences)).ConfigureAwait(false))!,
                ApprovedResourceClaims = (await Project<IReadOnlyList<AgentResourceClaim>>(nameof(AgentToolExecutionContext.ApprovedResourceClaims)).ConfigureAwait(false))!,
                ApprovedResourceCapabilities = (await Project<IReadOnlyList<string>>(nameof(AgentToolExecutionContext.ApprovedResourceCapabilities)).ConfigureAwait(false))!,
                ResourceOperation = await Project<AgentResourceOperationContext?>(nameof(AgentToolExecutionContext.ResourceOperation)).ConfigureAwait(false),
                ExecutionTarget = reference is null ? null : (ServiceReference<AgentServices.IExecutionTargetService>)await receiver
                    .RebindReferenceAsync(reference, typeof(AgentServices.IExecutionTargetService), cancellationToken).ConfigureAwait(false),
            };
        }

        private static object?[] Flatten(object?[] values, ParameterInfo[] parameters)
        {
            if (values.Length == parameters.Length) return values;
            var result = new List<object?>();
            foreach (var value in values)
            {
                if (value is null) { result.Add(null); continue; }
                var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
                if (properties.Length == 0) result.Add(value);
                else result.AddRange(parameters.Skip(result.Count).Select(parameter => properties.Single(property =>
                    string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)).GetValue(value)));
            }
            if (result.Count != parameters.Length)
                throw new InvalidOperationException($"Generated fixture call expected {parameters.Length} arguments but received {result.Count}.");
            return result.ToArray();
        }

        private static async ValueTask<object?> AwaitAsync(object pending)
        {
            if (pending is ValueTask valueTask) { await valueTask.ConfigureAwait(false); return null; }
            var type = pending.GetType();
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ValueTask<>)) return pending;
            return await (Task<object?>)typeof(FixtureBridge).GetMethod(nameof(AwaitResult), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type.GenericTypeArguments[0]).Invoke(null, [pending])!;
        }

        private static async Task<object?> AwaitResult<T>(ValueTask<T> pending) => await pending.ConfigureAwait(false);

        private static async IAsyncEnumerable<object?> CastStream<T>(IAsyncEnumerable<T> source,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false)) yield return item;
        }
    }
}
