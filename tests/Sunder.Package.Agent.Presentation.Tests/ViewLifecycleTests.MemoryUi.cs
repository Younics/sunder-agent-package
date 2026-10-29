extern alias AgentApp;
extern alias MemoryApp;
extern alias MemoryWorker;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using MemoryAppGateway = MemoryApp::Sunder.Package.Agent.Memory.Semantic.Runtime.IMemoryInspectorGateway;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class ViewLifecycleTests
{
    [AvaloniaFact]
    public async Task MemoryGraphData_KeepSelectedEditorDraftExpansionAndViewportThroughRefreshAndResize()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var workspace = services.WorkspaceService.CreateWorkspace("Product workspace");
        var session = services.SessionService.CreateSession("Release planning", workspaceId: workspace.WorkspaceId);
        services.ExtensionCatalog.AddRuntimeCatalog(new AgentRuntimeCatalog(services.SessionService, profileService, services.WorkspaceService));
        using var store = new MemoryLocalStore(scope.Context);
        var turn = Guid.NewGuid();
        for (var index = 0; index < 8; index++)
            store.UpsertMemory(new(session.SessionId, index % 2 == 0 ? "preference" : "project-fact",
                index == 0 ? "Use concise summaries and keep decisions traceable to their evidence." : $"Milestone {index} is reviewed before release.",
                "memory-ui-" + index, "Recorded during planning.", turn, index == 0, .8f, .9f));
        var inspector = CreateMemoryInspector(scope.Context, services.ExtensionCatalog);
        using var model = new MemoryInspectorViewModel(AgentRoleBoundary.As<MemoryAppGateway>(inspector));
        using var view = new MemoryInspectorView(model);
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();
        try
        {
            await model.InitializeAsync();
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(0, model.SelectedTab);
            var graph = view.FindControl<MemoryGraphControl>("MemoryGraph")!;
            Assert.True(graph.Bounds.Width > 200 && graph.Bounds.Height > 100);
            UiCapture.Save(window, "memory-graph-wide");
            graph.ZoomBy(1.2);
            var viewport = (graph.Zoom, graph.ViewOffset);
            model.SelectedTab = 1;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var editor = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), input => input.AcceptsReturn);
            model.EditContent = "Unsaved memory draft";
            Assert.True(editor.Focus());
            editor.SelectionStart = 2;
            editor.SelectionEnd = 8;
            var evidence = Assert.Single(view.GetVisualDescendants().OfType<Expander>(), expander => Equals(expander.Header, "Evidence"));
            evidence.IsExpanded = true;
            window.Height = 620;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var scroll = view.FindControl<Border>("MemoryDetailPane")!.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, 48);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var scrollOffset = scroll.Offset;
            var selected = model.SelectedMemory!.MemoryId;
            await model.RefreshCommand.ExecuteAsync(null);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Same(editor, Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), input => input.AcceptsReturn));
            Assert.Equal("Unsaved memory draft", editor.Text);
            Assert.True(editor.IsKeyboardFocusWithin);
            Assert.Equal(2, editor.SelectionStart);
            Assert.Equal(8, editor.SelectionEnd);
            Assert.True(evidence.IsExpanded);
            Assert.Equal(scrollOffset, scroll.Offset);
            Assert.Equal(selected, model.SelectedMemory?.MemoryId);
            UiCapture.Save(window, "memory-data-wide");

            window.Width = 430;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(view.FindControl<Border>("MemoryDetailPane")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Border>("MemoryListPane")!.IsEffectivelyVisible);
            Assert.Equal("Unsaved memory draft", editor.Text);
            var back = view.FindControl<Button>("MemoryBackButton")!;
            var pane = view.FindControl<Border>("MemoryDetailPane")!;
            var backPosition = back.TranslatePoint(default, pane)!.Value;
            Assert.True(backPosition.Y >= 0 && backPosition.Y + back.Bounds.Height <= pane.Bounds.Height);
            UiCapture.Save(window, "memory-data-compact");
            back.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            store.UpdateMemory(selected, "preference", "Changed outside this editor.", null);
            await model.RefreshCommand.ExecuteAsync(null);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(view.FindControl<Border>("MemoryListPane")!.IsEffectivelyVisible);
            model.SelectedTab = 0;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(viewport, (graph.Zoom, graph.ViewOffset));
            Assert.Equal(selected, graph.SelectedMemoryId);
            UiCapture.Save(window, "memory-graph-compact");
        }
        finally { window.Close(); }
    }
}
