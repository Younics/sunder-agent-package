using Avalonia.Controls;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Provider.TestSupport;

public sealed class ProviderCompositionTestRegistry : IAvaloniaPackageContributionRegistry
{
    public List<Type> SettingsViewTypes { get; } = [];

    public void RegisterSettingsView<TView>() where TView : Control
        => SettingsViewTypes.Add(typeof(TView));

    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
        => throw new NotSupportedException();

}
