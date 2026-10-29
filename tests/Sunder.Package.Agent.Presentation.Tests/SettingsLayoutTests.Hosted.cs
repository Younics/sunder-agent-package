extern alias LocalApp;
extern alias McpApp;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Reflection;
using Sunder.Package.Agent.Tests;
using LocalApp::Sunder.Package.Agent.Execution.Local;
using McpApp::Sunder.Package.Agent.Mcp;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class SettingsLayoutTests
{
    [AvaloniaTheory]
    [InlineData("local")]
    [InlineData("mcp")]
    [InlineData("skills")]
    public async Task HostedSettingsKeepGuttersInsideScrollingContentAtEveryPaneWidth(string package)
    {
        using var scope = RegressionTestPackageScope.Create();
        Control view;
        if (package == "local")
        {
            var constructor = typeof(LocalExecutionSettingsViewModel).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var model = (LocalExecutionSettingsViewModel)constructor.Invoke([
                DispatchProxy.Create(constructor.GetParameters()[0].ParameterType, typeof(LocalSettingsObserver))]);
            view = new LocalExecutionSettingsView(model);
        }
        else if (package == "mcp")
            view = new AgentMcpSettingsView(new AgentMcpSettingsViewModel(new SettingsLayoutFixtures.McpGateway()));
        else view = new SkillSettingsView(new SkillSettingsViewModel(new SettingsLayoutFixtures.SkillGateway()));
        await using var hosted = new HostedSettingsWindow(view, scope.RootPath);
        await hosted.ShowAsync();
        if (view.DataContext is AgentMcpSettingsViewModel mcp) mcp.CreateServerCommand.Execute(null);
        if (view.DataContext is SkillSettingsViewModel skills) skills.NewSkillCommand.Execute(null);
        var window = hosted.Window;
        TextBox? draft = null;
        foreach (var width in new[] { 420d, 620d, 819d, 820d, 1000d, 420d })
        {
            window.Width = width + 294;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var pane = window.FindControl<Border>("SettingsSelectionPane")!;
            ScrollViewer scroll;
            if (package == "local")
            {
                scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
                var content = Assert.IsType<StackPanel>(scroll.Content);
                Assert.True(content.Margin.Left >= 16 && content.Margin.Right >= 16);
                var input = view.GetVisualDescendants().OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "Shell name");
                if (draft is null) { draft = input; input.Text = "Draft shell"; input.Focus(); input.SelectionStart = 1; input.SelectionEnd = 4; }
                Assert.Same(draft, input);
                Assert.Equal(("Draft shell", 1, 4), (input.Text, input.SelectionStart, input.SelectionEnd));
                Assert.True(input.IsKeyboardFocusWithin);
                Assert.All(view.GetVisualDescendants().OfType<Button>().Where(button => button.Content is string),
                    button => Assert.True(button.Classes.Contains("primary-action") || button.Classes.Contains("secondary-action")));
            }
            else
            {
                var body = view.GetVisualDescendants().OfType<Border>().Single(control =>
                    control.Classes.Contains(package == "mcp" ? "mcp-editor-body" : "skill-detail-body"));
                scroll = body.GetVisualAncestors().OfType<ScrollViewer>().First();
                Assert.True(body.Padding.Left >= 16 && body.Padding.Right >= 16);
            }
            Assert.InRange(Math.Abs(scroll.TranslatePoint(default, pane)!.Value.X + scroll.Bounds.Width - pane.Bounds.Width), 0, 1);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + .5,
                $"{package} overflowed at pane width {width}: {scroll.Extent.Width} > {scroll.Viewport.Width}.");
            UiCapture.Save(window, $"hosted-{package}-{width:0}");
        }
    }

    public class LocalSettingsObserver : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "GetSettingsAsync"
                ? ValueTask.FromResult(new LocalExecutionOperationResponse("300",
                    [new("detected", "POSIX sh", "/synthetic/bin/sh", "posix-sh", true)],
                    [new("custom", "Custom shell", "/synthetic/bin/custom", "custom", false)], 1))
                : throw new NotSupportedException("The layout fixture cannot mutate or execute anything.");
    }
}
