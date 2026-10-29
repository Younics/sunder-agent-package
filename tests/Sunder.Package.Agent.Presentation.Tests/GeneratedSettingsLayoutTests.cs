extern alias MemoryWorker;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.App.ViewModels;
using Sunder.Package.Agent.Tests;
using Sunder.Package.Agent.Tools.Web;
using Sunder.Runtime.Contracts;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class GeneratedSettingsLayoutTests
{
    [AvaloniaTheory]
    [InlineData("web")]
    [InlineData("memory")]
    public async Task RealPackageSchemasUseSpacedSectionsInlineHelpAndEdgeToEdgeScrollingWithoutReplacingDrafts(string package)
    {
        using var scope = RegressionTestPackageScope.Create();
        var authored = package == "web" ? WebToolsConfiguration.Schema : MemorySemanticPackageConfiguration.Schema;
        var schema = new PackageSettingsSchemaDescriptor(HostedSettingsWindow.PackageId, package, authored.Summary,
            authored.Sections.Select(section => new PackageSettingsSectionDescriptor(section.SectionId, section.Title, section.Description,
                section.Fields.Select(field => new PackageSettingsFieldDescriptor(field.Key, field.Label,
                    (PackageSettingsFieldKind)(int)field.Kind, field.Description, field.IsRequired, field.Placeholder, field.DefaultValue,
                    field.Options.Select(option => new PackageSettingsOptionDescriptor(option.Value, option.Label)).ToArray())).ToArray())).ToArray());
        await using var hosted = new HostedSettingsWindow(null, scope.RootPath, [schema]);
        await hosted.ShowAsync();
        var window = hosted.Window;
        var scroll = window.FindControl<ScrollViewer>("SettingsSelectionScroll")!;
        var pane = window.FindControl<Border>("SettingsSelectionPane")!;
        var draft = scroll.GetVisualDescendants().OfType<TextBox>().First(control => control.IsEffectivelyVisible);
        draft.Text = "Uncommitted setting";
        Assert.True(draft.Focus()); draft.SelectionStart = 1; draft.SelectionEnd = 5;
        foreach (var width in new[] { 1000d, 620d, 420d, 1000d })
        {
            window.Width = width + 294;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Same(draft, scroll.GetVisualDescendants().OfType<TextBox>().First(control => control.IsEffectivelyVisible));
            Assert.Equal(("Uncommitted setting", 1, 5), (draft.Text, draft.SelectionStart, draft.SelectionEnd));
            Assert.True(draft.IsKeyboardFocusWithin);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + .5);
            Assert.InRange(Math.Abs(scroll.TranslatePoint(default, pane)!.Value.X + scroll.Bounds.Width - pane.Bounds.Width), 0, 1);
            var cards = scroll.GetVisualDescendants().OfType<Border>().Where(control => control.Classes.Contains("card-surface")
                && control.DataContext is SettingsFieldSectionViewModel).ToArray();
            Assert.Equal(schema.Sections.Count, cards.Length);
            for (var index = 1; index < cards.Length; index++)
                Assert.True(cards[index].TranslatePoint(default, scroll)!.Value.Y - cards[index - 1].TranslatePoint(default, scroll)!.Value.Y
                    - cards[index - 1].Bounds.Height >= 16);
            var content = Assert.IsType<StackPanel>(scroll.Content);
            Assert.All(cards, card => Assert.InRange(Math.Abs(card.Bounds.Width - (scroll.Viewport.Width - content.Margin.Left - content.Margin.Right)), 0, 1));
            var helps = scroll.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("settings-info") && button.IsEffectivelyVisible).ToArray();
            Assert.True(helps.Length >= 4);
            UiCapture.Save(window, $"generated-{package}-{width:0}");
        }
        var help = scroll.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("settings-info") && button.IsEffectivelyVisible);
        var flyout = Assert.IsType<Flyout>(help.Flyout);
        flyout.ShowAt(help);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        var text = Assert.IsType<SelectableTextBlock>(flyout.Content);
        UiCapture.Save(TopLevel.GetTopLevel(text)!, $"generated-{package}-help-popover");
        flyout.Hide();
    }
}
