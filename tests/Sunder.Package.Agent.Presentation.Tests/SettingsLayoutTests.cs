extern alias BuilderApp;
extern alias McpApp;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuilderApp::Sunder.Package.Agent.Builder;
using McpApp::Sunder.Package.Agent.Mcp;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class SettingsLayoutTests
{
    [AvaloniaTheory]
    [InlineData(360)]
    [InlineData(620)]
    [InlineData(1000)]
    public async Task McpImportsBelongToOverviewAndTheirFeedbackRemainsVisible(double width)
    {
        var gateway = new SettingsLayoutFixtures.McpGateway();
        var model = new AgentMcpSettingsViewModel(gateway);
        await model.InitializeAsync();
        using var view = new AgentMcpSettingsView(model);
        var window = new Window { Width = width, Height = 720, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            foreach (var name in new[] { "ImportCommonConfigsButton", "ImportConfigFileButton" })
            {
                var button = view.FindControl<Button>(name)!;
                Assert.True(button.IsEffectivelyVisible);
                Assert.Contains(button.GetVisualAncestors(), control => control.Name == "McpListPane");
                Assert.DoesNotContain(button.GetVisualAncestors(), control => control.Name == "McpEditorPane");
            }
            await model.ImportCommonConfigurationsCommand.ExecuteAsync(null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var status = view.FindControl<TextBlock>("McpOperationStatus")!;
            Assert.True(status.IsEffectivelyVisible);
            Assert.Contains("Imported 2", status.Text, StringComparison.Ordinal);
            await model.ImportConfigurationFileAsync("/synthetic/config.json");
            Assert.Contains("Imported 1", model.StatusText, StringComparison.Ordinal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Contains("Imported 1", status.Text, StringComparison.Ordinal);
            Assert.Equal(1, gateway.CommonImports);
            Assert.Equal(1, gateway.FileImports);
            Assert.False(model.IsEditorActive);
            model.CreateServerCommand.Execute(null);
            window.UpdateLayout();
            var editor = view.FindControl<Border>("McpEditorPane")!;
            Assert.True(editor.IsEffectivelyVisible);
            Assert.DoesNotContain(editor.GetVisualDescendants().OfType<Button>(), button => button.Content is string text && text.StartsWith("Import", StringComparison.Ordinal));
            Assert.Contains(editor.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Local Template"));
            UiCapture.Save(window, $"mcp-editor-{width:0}");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(360)]
    [InlineData(620)]
    [InlineData(1000)]
    public async Task SkillsOverviewOwnsCommonImportWhileDetailShowsExactlyOneSourceWithoutLosingUrl(double width)
    {
        var model = new SkillSettingsViewModel(new SettingsLayoutFixtures.SkillGateway());
        await model.InitializeAsync();
        using var view = new SkillSettingsView(model);
        var window = new Window { Width = width, Height = 720, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            var common = view.FindControl<Button>("ImportCommonFoldersButton")!;
            Assert.True(common.IsEffectivelyVisible);
            Assert.Contains(common.GetVisualAncestors(), control => control.Name == "SkillListPane");
            await model.ImportCommonSkillFoldersCommand.ExecuteAsync(null);
            Assert.True(model.IsListActive);
            Assert.True(view.FindControl<TextBlock>("SkillOperationStatus")!.IsEffectivelyVisible);
            model.NewSkillCommand.Execute(null);
            window.UpdateLayout();
            var detail = view.FindControl<Border>("SkillDetailPane")!;
            Assert.DoesNotContain(detail.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Import Common Folders"));
            var github = view.FindControl<StackPanel>("GitHubImportForm")!;
            var local = view.FindControl<StackPanel>("LocalFolderImportForm")!;
            Assert.True(github.IsEffectivelyVisible);
            Assert.False(local.IsEffectivelyVisible);
            model.GithubUrl = "https://github.com/example/skills/tree/main/docs";
            view.FindControl<RadioButton>("LocalFolderSourceButton")!.IsChecked = true;
            Assert.False(model.IsGitHubSource);
            Assert.True(model.IsLocalSource);
            Assert.False(github.IsEffectivelyVisible);
            Assert.True(local.IsEffectivelyVisible);
            view.FindControl<RadioButton>("GitHubSourceButton")!.IsChecked = true;
            Assert.True(github.IsEffectivelyVisible);
            Assert.False(local.IsEffectivelyVisible);
            Assert.Equal("https://github.com/example/skills/tree/main/docs", model.GithubUrl);
            UiCapture.Save(window, $"skills-import-{width:0}");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(360)]
    [InlineData(620)]
    [InlineData(1000)]
    public async Task MakerUsesCompactCardsWithCollapsedInstallationDetailsAndVisibleDiscoveryAndErrors(double width)
    {
        var backend = new SettingsLayoutFixtures.SdkBackend();
        await using var application = new BuilderProjectApplicationService(backend, null!, null!, null!);
        await using var manager = new BuilderSdkManager(application, new AvaloniaBuilderUiDispatcher());
        var model = new BuilderSettingsViewModel(manager);
        using var view = new BuilderSettingsView(model);
        var window = new Window { Width = width, Height = 720, Content = view };
        window.Show();
        try
        {
            var preparation = model.PrepareNavigationAsync(new("settings:maker", new Dictionary<string, string?>()));
            Assert.True(preparation.IsCompletedSuccessfully);
            Assert.True(await preparation);
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.UpdateLayout();
            var details = view.GetVisualDescendants().OfType<Expander>().Where(expander => Equals(expander.Header, "Details")).ToArray();
            var sdkActions = view.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("primary-action")).ToArray();
            Assert.Equal(2, details.Length);
            Assert.Equal(2, sdkActions.Length);
            Assert.All(sdkActions, button => Assert.False(button.IsEffectivelyVisible));
            Assert.All(details, expander => Assert.False(expander.IsExpanded));
            Assert.All(model.Cards, card => Assert.Equal("Checking SDKs…", card.Status));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == ".NET");
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "JS");
            backend.Release.TrySetResult();
            await model.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3));
            window.UpdateLayout();
            Assert.All(sdkActions, button => Assert.False(button.IsEffectivelyVisible));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.IsEffectivelyVisible && text.Text!.StartsWith("/synthetic", StringComparison.Ordinal));
            details[0].IsExpanded = true;
            window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.IsEffectivelyVisible && text.Text == "/synthetic/sdk/dotnet");
            Assert.All(view.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "Repair") || Equals(button.Content, "Remove")), button => Assert.False(button.IsEffectivelyEnabled));
            details[0].IsExpanded = false;
            foreach (var button in sdkActions)
            {
                var card = Assert.IsType<BuilderSdkCardViewModel>(button.DataContext);
                var current = new BuilderToolchainStatus(card.Kind, card.Installations.Select(row => row.Installation).ToArray(),
                    card.RecommendedVersion, DateTimeOffset.UtcNow, false, null);
                card.Apply(current with { Installed = [], UpdateAvailable = true });
                Assert.True(button.IsEffectivelyVisible);
                Assert.Equal("Install SDK", button.Content);
                card.IsRefreshing = true;
                Assert.False(button.IsEffectivelyVisible);
                card.IsRefreshing = false;
                Assert.True(button.IsEffectivelyVisible);
                card.Apply(current with { UpdateAvailable = true });
                Assert.True(button.IsEffectivelyVisible);
                Assert.Equal("Update SDK", button.Content);
                card.Apply(current);
                Assert.False(button.IsEffectivelyVisible);
                card.Apply(current with { UpdateAvailable = null, CatalogError = "Offline" });
                Assert.False(button.IsEffectivelyVisible);
            }
            model.Cards[0].Apply(new(BuilderProjectKind.Dotnet, [], null, null, null, null));
            Assert.True(sdkActions[0].IsEffectivelyVisible);
            model.Cards[0].SetRefreshError("SDK observation unavailable");
            Assert.False(sdkActions[0].IsEffectivelyVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "SDK observation unavailable");
            Assert.Equal(2, backend.Checks);
            UiCapture.Save(window, $"sdk-settings-{width:0}");
        }
        finally { backend.Release.TrySetResult(); window.Close(); }
    }
}
