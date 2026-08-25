using Avalonia.Controls;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.TestSupport.Avalonia;

public sealed record RecordedPackageView(
    Type ViewType,
    PackageViewRegistration Registration);

public sealed class RecordingPackageContributionRegistry : IAvaloniaPackageContributionRegistry
{
    public List<Type> SettingsViewTypes { get; } = [];

    public List<RecordedPackageView> PackageViews { get; } = [];

    public void RegisterSettingsView<TView>() where TView : Control
        => SettingsViewTypes.Add(typeof(TView));

    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
    {
        var viewType = typeof(TView);
        if (viewType.Assembly.GetName().Name?.StartsWith(
                "Sunder.Package.Agent.Provider.",
                StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(
                $"Provider App package '{viewType.Assembly.GetName().Name}' must register settings views only; "
                + $"package view '{viewType.FullName}' is forbidden.");
        }
        PackageViews.Add(new RecordedPackageView(viewType, registration));
    }
}
