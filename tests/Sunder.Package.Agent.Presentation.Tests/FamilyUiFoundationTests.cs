extern alias AgentApp;
extern alias MemoryApp;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using AgentApp::Sunder.Package.Agent.Shared.Presentation;
using MemoryApp::Sunder.Package.Agent.Memory.Semantic;
using MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class FamilyUiFoundationTests
{
    [AvaloniaFact]
    public async Task NonChatSelectionAndExpanders_UseSemanticThemeInsideTheirTemplates()
    {
        var list = new ListBox { ItemsSource = new[] { "Selected" }, SelectedIndex = 0 };
        var expander = new Expander { Header = "Details", Content = new TextBlock { Text = "Content" } };
        var page = new DerivedThemePage { Content = new StackPanel { Children = { list, expander } } };
        page.Classes.Add("agent-page");
        page.Resources["Sunder.Brush.Surface.Selected"] = Brushes.DimGray;
        page.Resources["Sunder.Brush.Surface.Raised"] = Brushes.DarkSlateGray;
        page.Styles.Add(new StyleInclude(new Uri("avares://Sunder.Package.Agent.App/"))
        { Source = new Uri("avares://Sunder.Package.Agent.App/Presentation/PresentationStyles.axaml") });
        var window = new Window { Width = 500, Height = 320, Content = page };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Same(Brushes.DarkSlateGray, page.Background);
            var item = Assert.Single(list.GetVisualDescendants().OfType<ListBoxItem>());
            var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First(control => control.Name == "PART_ContentPresenter");
            Assert.Same(Brushes.DimGray, presenter.Background);
            var header = expander.GetVisualDescendants().OfType<ToggleButton>().Single(control => control.Name == "ExpanderHeader");
            Assert.Same(Brushes.DarkSlateGray, header.Background);
            page.Resources["Sunder.Brush.Surface.Selected"] = Brushes.Gray;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Same(Brushes.Gray, presenter.Background);
        }
        finally { window.Close(); }
    }

    private sealed class DerivedThemePage : UserControl { }

    [AvaloniaFact]
    public async Task InfoPopover_OpensFromKeyboardAndUsesCurrentThemeResources()
    {
        var help = new SectionHelpButton { HelpText = "This setting changes the default timeout." };
        var owner = new UserControl { Content = help };
        owner.Styles.Add(new StyleInclude(new Uri("avares://Sunder.Package.Agent.App/"))
        { Source = new Uri("avares://Sunder.Package.Agent.App/Presentation/PresentationStyles.axaml") });
        var window = new Window { Width = 480, Height = 320, Content = owner };
        window.Show();
        try
        {
            window.UpdateLayout();
            var icon = Assert.IsType<Avalonia.Controls.Shapes.Path>(help.Content);
            var iconOrigin = icon.TranslatePoint(default, help)!.Value;
            Assert.InRange(Math.Abs(iconOrigin.X + icon.Bounds.Width / 2 - help.Bounds.Width / 2), 0, 0.5);
            Assert.InRange(Math.Abs(iconOrigin.Y + icon.Bounds.Height / 2 - help.Bounds.Height / 2), 0, 0.5);
            Assert.True(help.Focus());
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var flyout = Assert.IsType<Flyout>(help.Flyout);
            Assert.True(flyout.IsOpen);
            var text = Assert.IsType<SelectableTextBlock>(flyout.Content);
            var presenter = Assert.Single(text.GetVisualAncestors().OfType<FlyoutPresenter>());
            Assert.Same(Application.Current!.Resources["Sunder.Brush.Surface.Hover"], presenter.Background);
            Assert.Same(Application.Current.Resources["Sunder.Brush.Border.Strong"], presenter.BorderBrush);
            Assert.NotEqual(Application.Current.Resources["Sunder.Brush.Surface.Base"], presenter.Background);
            Assert.Equal(help.HelpText, text.Text);
            Assert.Equal(14, text.FontSize);
            owner.Resources["Sunder.FontSize.Body"] = 16d;
            owner.Resources["Sunder.Brush.Foreground.Primary"] = Brushes.White;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(16, text.FontSize);
            Assert.Same(Brushes.White, text.Foreground);
            UiCapture.Save(TopLevel.GetTopLevel(presenter)!, "agent-help-popover");
            flyout.Hide();
            Assert.False(flyout.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EditorFooter_KeepsActionsTogetherAcrossMeasuredBreakpointsAndVisibilityChanges()
    {
        var remove = new Button { Content = "Delete", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var notice = new TextBlock { Text = "Unsaved changes in this draft", TextWrapping = TextWrapping.Wrap };
        var back = new Button { Content = "Back" };
        var save = new Button { Content = "Save" };
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { back, save } };
        Grid.SetColumn(notice, 1);
        Grid.SetColumn(actions, 2);
        var footer = new EditorFooter { Children = { remove, notice, actions } };
        var draft = new TextBox { Text = "Unchanged draft", SelectionStart = 2, SelectionEnd = 7 };
        var root = new Grid { RowDefinitions = new("*,Auto"), Children = { draft, footer } };
        Grid.SetRow(footer, 1);
        var window = new Window { Width = 800, Height = 320, Content = root };
        window.Show();
        try
        {
            Assert.True(draft.Focus());
            foreach (var width in new[] { 800d, 360d, 240d, 180d, 800d })
            {
                window.Width = width;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                var backOrigin = back.TranslatePoint(default, footer)!.Value;
                var saveOrigin = save.TranslatePoint(default, footer)!.Value;
                Assert.InRange(Math.Abs(backOrigin.Y - saveOrigin.Y), 0, 0.5);
                Assert.True(saveOrigin.X >= backOrigin.X + back.Bounds.Width);
                Assert.True(saveOrigin.X + save.Bounds.Width <= footer.Bounds.Width + 0.5,
                    $"At {width}px: footer {footer.Bounds}, left {remove.Bounds}, actions {actions.Bounds}, Save {saveOrigin} / {save.Bounds}, row {Grid.GetRow(actions)}.");
                Assert.Same(actions, footer.Children[2]);
                Assert.True(draft.IsFocused);
                Assert.Equal(("Unchanged draft", 2, 7), (draft.Text, draft.SelectionStart, draft.SelectionEnd));
                if (width == 360) Assert.Equal(Grid.GetRow(remove), Grid.GetRow(actions));
                if (width == 180) Assert.True(Grid.GetRow(actions) > Grid.GetRow(remove));
            }
            window.Width = 360;
            notice.IsVisible = false;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(0, Grid.GetRow(actions));
            notice.IsVisible = true;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(1, Grid.GetRow(actions));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ResponsiveGrid_ReflowsAndRestoresOriginalCellsWithoutReplacingInputs()
    {
        var input = new TextBox { Text = "Draft", SelectionStart = 1, SelectionEnd = 3 };
        var action = new Button { Content = "Save" };
        Grid.SetColumn(action, 1);
        var grid = new ResponsiveGrid { ColumnDefinitions = new("*,Auto"), Children = { input, action } };
        var window = new Window { Width = 700, Height = 320, Content = grid };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(1, Grid.GetColumn(action));
            window.Width = 360;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(0, Grid.GetColumn(action));
            Assert.Equal(1, Grid.GetRow(action));
            window.Width = 700;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(1, Grid.GetColumn(action));
            Assert.Equal(0, Grid.GetRow(action));
            Assert.Same(input, grid.Children[0]);
            Assert.Equal("Draft", input.Text);
            Assert.Equal(1, input.SelectionStart);
            Assert.Equal(3, input.SelectionEnd);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MemoryGraphRefresh_PreservesPositionsAndViewportUntilExplicitReset()
    {
        var now = DateTimeOffset.UtcNow;
        var memory = new StoredMemoryRecord(Guid.NewGuid(), Guid.NewGuid(), "preference", "Original", null, null,
            1, 1, false, "Active", null, now, now, null, 0);
        var graph = new MemoryGraphState();
        graph.Apply([memory]);
        var control = new MemoryGraphControl { Width = 800, Height = 500, Graph = graph };
        var window = new Window { Width = 800, Height = 500, Content = control };
        window.Show();
        try
        {
            window.UpdateLayout();
            control.Fit();
            control.ZoomBy(1.2);
            var offset = control.ViewOffset;
            var zoom = control.Zoom;
            var position = control.Positions.Single();
            var updated = new MemoryGraphState();
            updated.Apply([memory with { Content = "Updated" }, memory with { MemoryId = Guid.NewGuid() }]);
            control.Graph = updated;
            Assert.Equal(position.Value, control.Positions[position.Key]);
            Assert.Equal(offset, control.ViewOffset);
            Assert.Equal(zoom, control.Zoom);
            Assert.Equal(2, control.Positions.Count);
            var center = new Point((position.Value.X + control.NodeWidth / 2) * control.Zoom + control.ViewOffset.X,
                (position.Value.Y + control.NodeHeight / 2) * control.Zoom + control.ViewOffset.Y);
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Vector(30, 20));
            window.MouseUp(center + new Vector(30, 20), MouseButton.Left);
            Assert.Equal(memory.MemoryId, control.SelectedMemoryId);
            Assert.NotEqual(position.Value, control.Positions[position.Key]);
            window.MouseWheel(center, new Vector(0, 1));
            Assert.True(control.Zoom > zoom);
            var dragged = control.Positions[position.Key];
            var hidden = new MemoryGraphState();
            hidden.Apply([], sessionId: memory.SessionId);
            control.Graph = hidden;
            control.Graph = updated;
            Assert.Equal(dragged, control.Positions[position.Key]);
            var nextSession = new MemoryGraphState();
            nextSession.Apply([], sessionId: Guid.NewGuid());
            control.Graph = nextSession;
            Assert.Empty(control.Positions);
            Assert.Equal(1, control.Zoom);
            Assert.Equal(default, control.ViewOffset);
        }
        finally { window.Close(); }
    }
}
