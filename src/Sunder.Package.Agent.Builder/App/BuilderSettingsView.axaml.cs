using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(BuilderSettingsViewModel))]
public partial class BuilderSettingsView : UserControl, IDisposable
{
    private readonly PresentationOperationObserver _operations = PresentationOperationObserver.CreateUnreported("Package Maker SDK settings");
    private int _disposed;
    public BuilderSettingsView() => InitializeComponent();
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public BuilderSettingsView(BuilderSettingsViewModel viewModel) : this() => DataContext = viewModel;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _operations.Dispose();
        DataContext = null;
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => Submit(model => model.RefreshAsync());
    private void OnInstallClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BuilderSdkCardViewModel card }) Submit(model => model.InstallAsync(card.Kind));
    }
    private void OnRepairClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BuilderSdkInstallationViewModel row }) Submit(model => model.RepairAsync(row.Installation));
    }
    private void OnRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BuilderSdkInstallationViewModel row }) Submit(model => model.RemoveAsync(row.Installation));
    }
    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BuilderSdkCardViewModel card } && DataContext is BuilderSettingsViewModel model) model.Cancel(card.Kind);
    }
    private void Submit(Func<BuilderSettingsViewModel, Task> action)
    {
        if (DataContext is BuilderSettingsViewModel model)
            _operations.Submit(new("builder.sdk-settings", "The SDK operation could not be completed."), _ => action(model));
    }
}
