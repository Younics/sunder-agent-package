using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;

namespace Sunder.Package.Agent.Presentation.Tests;

// Exercise the real host boundary as well as the package's own scrolling content.
internal sealed class HostedSettingsWindow : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly PackageViewHostService _host;
    internal SettingsWindowViewModel Model { get; }
    internal SettingsWindow Window { get; }
    internal const string PackageId = "local.hosted.settings";

    internal HostedSettingsWindow(Control? view, string root, IReadOnlyList<PackageSettingsSchemaDescriptor>? schemas = null)
    {
        var services = new ServiceCollection();
        if (view is not null) services.AddSingleton(view);
        _services = services.BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        if (view is not null) registry.RegisterSettingsView<Control>(PackageId, _services);
        _host = new(registry, [], [_services], [], sessionFolder: null);
        Model = new(new EmptySettingsClient(schemas ?? []), _host, new CliInstallationService(),
            runtimeResourcePolicySettingsService: new AppRuntimeResourcePolicySettingsService(Path.Combine(root, "ui-resource-policy.json")));
        Window = new() { DataContext = Model, MinWidth = 0, Width = 1294, Height = 800 };
        Window.Styles.Add(new StyleInclude(new Uri("avares://Sunder.App/"))
            { Source = new Uri("avares://Sunder.App/Styles/SunderShellStyles.axaml") });
    }

    internal async Task ShowAsync()
    {
        await Model.RefreshPackageSectionsAsync();
        Xunit.Assert.True(await Model.SelectPackageSettingsAsync(PackageId));
        Window.Show();
        Window.UpdateLayout();
    }

    public async ValueTask DisposeAsync()
    {
        Window.CloseForShutdown();
        Model.Dispose();
        await _host.DisposeAsync();
        await _services.DisposeAsync();
    }

    private sealed class EmptySettingsClient(IReadOnlyList<PackageSettingsSchemaDescriptor> schemas) : IRuntimePackageSettingsClient
    {
        public Task<IReadOnlyList<PackageSettingsSchemaDescriptor>> GetPackageSettingsSchemasAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(schemas);
        public Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<PackageSettingsValuesResponse?>(null);
        public Task SavePackageSettingsValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Layout fixtures cannot save settings.");
        public void Dispose() { }
    }
}
