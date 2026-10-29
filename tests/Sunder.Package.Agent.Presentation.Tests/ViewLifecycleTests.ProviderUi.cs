extern alias AnthropicApp;
extern alias GeminiApp;
extern alias LMStudioApp;
extern alias OpenAiApp;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Reflection;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;
using AnthropicUi = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic;
using Gemini = GeminiApp::Sunder.Package.Agent.Provider.Gemini;
using LMStudio = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio;
using OpenAi = OpenAiApp::Sunder.Package.Agent.Provider.OpenAI;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class ViewLifecycleTests
{
    [AvaloniaTheory]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("lmstudio")]
    [InlineData("openai")]
    public async Task ProviderSettings_ResizeWithoutOverflowOrReplacingCredentialDraft(string package)
    {
        using var scope = RegressionTestPackageScope.Create();
        Control view = package switch
        {
            "anthropic" => new AnthropicUi.AnthropicSettingsView(CreateUiCredentialModel<AnthropicUi.AnthropicSettingsViewModel>(scope.Context)),
            "gemini" => new Gemini.GeminiSettingsView(CreateUiCredentialModel<Gemini.GeminiSettingsViewModel>(scope.Context)),
            "lmstudio" => new LMStudio.LMStudioSettingsView(CreateUiCredentialModel<LMStudio.LMStudioSettingsViewModel>(scope.Context)),
            "openai" => new OpenAi.OpenAiSettingsView(new OpenAi.OpenAiSettingsViewModel(scope.Context, new ConnectedOpenAiManagementClient())),
            _ => throw new ArgumentOutOfRangeException(nameof(package)),
        };
        using var lifetime = Assert.IsAssignableFrom<IDisposable>(view);
        var preparation = Assert.IsAssignableFrom<IPackageViewNavigationPreparationTarget>(view.DataContext);
        Assert.True(await preparation.PrepareNavigationAsync(new("settings:test", new Dictionary<string, string?>())));
        await using var hosted = new HostedSettingsWindow(view, scope.RootPath);
        await hosted.ShowAsync();
        var window = hosted.Window;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var field = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), input => input.Classes.Contains("secret-field"));
            field.Text = "disposable-draft";
            Assert.True(field.Focus());
            field.SelectionStart = 1;
            field.SelectionEnd = 5;
            foreach (var width in new[] { 1000d, 620d, 480d, 420d, 1000d })
            {
                window.Width = width + 294;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Assert.Same(field, Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), input => input.Classes.Contains("secret-field")));
                Assert.Equal("disposable-draft", field.Text);
                Assert.Equal('*', field.PasswordChar);
                Assert.True(field.IsKeyboardFocusWithin);
                Assert.Equal(1, field.SelectionStart);
                Assert.Equal(5, field.SelectionEnd);
                var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
                var pane = window.FindControl<Border>("SettingsSelectionPane")!;
                Assert.InRange(Math.Abs(scroll.TranslatePoint(default, pane)!.Value.X + scroll.Bounds.Width - pane.Bounds.Width), 0, 1);
                var content = Assert.IsType<StackPanel>(scroll.Content);
                Assert.True(content.Margin.Left >= 16 && content.Margin.Right >= 16);
                var card = field.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("card-surface"));
                Assert.InRange(Math.Abs(card.Bounds.Width - (scroll.Viewport.Width - content.Margin.Left - content.Margin.Right)), 0, 1);
                var bar = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(control => control.Orientation == Orientation.Vertical
                    && ReferenceEquals(control.GetVisualAncestors().OfType<ScrollViewer>().First(), scroll));
                if (bar.IsEffectivelyVisible)
                    Assert.InRange(Math.Abs(bar.TranslatePoint(default, pane)!.Value.X + bar.Bounds.Width - pane.Bounds.Width), 0, 1);
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + .5,
                    $"{package} overflowed at {width}: {scroll.Extent.Width} > {scroll.Viewport.Width}.");
                UiCapture.Save(window, $"provider-{package}-{width:0}");
            }
        }
        finally { window.CloseForShutdown(); }
    }

    private static TModel CreateUiCredentialModel<TModel>(IPackageContext context)
    {
        var constructor = typeof(TModel).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var credentials = DispatchProxy.Create(constructor.GetParameters()[1].ParameterType, typeof(UiCredentialObserver));
        return (TModel)constructor.Invoke([context, credentials]);
    }

    public class UiCredentialObserver : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "GetStatusAsync" ? ValueTask.FromResult(false)
                : throw new NotSupportedException("Observation-only fixture.");
    }
}
