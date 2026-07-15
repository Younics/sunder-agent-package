using Avalonia.Controls;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.TestSupport;

public sealed class ProviderCompositionTestRegistry : ISunderRuntimeContributionRegistry, IAvaloniaPackageContributionRegistry
{
    public List<string> ExtensionIds { get; } = [];
    public List<Type> ExtensionTypes { get; } = [];
    public List<PackageSettingsSchema> SettingsSchemas { get; } = [];
    public List<string> RuntimeOperationIds { get; } = [];
    public List<Type> RuntimeOperationHandlerTypes { get; } = [];
    public List<Type> SettingsViewTypes { get; } = [];

    public void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ExtensionIds.Add(extensionPoint.Id);
        ExtensionTypes.Add(contribution.GetType());
    }

    public void RegisterSettingsSchema(PackageSettingsSchema schema)
        => SettingsSchemas.Add(schema);

    public void RegisterRuntimeOperation<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        IPackageRuntimeOperationHandler<TRequest, TResponse> handler)
        where TRequest : class
        where TResponse : class
    {
        RuntimeOperationIds.Add(operation.OperationId);
        RuntimeOperationHandlerTypes.Add(handler.GetType());
    }

    public void RegisterRuntimeStream<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        IPackageRuntimeStreamHandler<TRequest, TEvent> handler)
        where TRequest : class
        where TEvent : class
        => throw new NotSupportedException();

    public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService
        => throw new NotSupportedException();

    public void RegisterSettingsView<TView>() where TView : Control
        => SettingsViewTypes.Add(typeof(TView));

    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
        => throw new NotSupportedException();

}
