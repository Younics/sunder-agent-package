extern alias AgentApp;

using System.Reflection;
using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Interactivity;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class InlineEditorXamlHandlerTests
{
    [Fact]
    public void NamedInlineEditorHandlers_UseInstanceRoutedEventSignatures()
    {
        // Compiled XAML accepts static methods, but design-time named handlers
        // must resolve on the view instance as well.
        (Type View, string Handler)[] handlers =
        [
            (typeof(AgentChatView), "OnSessionRenameInputInteraction"),
            (typeof(AgentWorkspacesView), "OnWorkspaceInlineEditInputInteraction"),
            (typeof(AgentWorkspacesView), "OnSetSelectedEditorPathItemAsDefaultClick"),
            (typeof(AgentWorkspacesView), "OnDeleteSelectedEditorPathItemClick"),
        ];
        foreach (var (view, handler) in handlers)
        {
            var method = view.GetMethod(handler, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            Assert.False(method.IsStatic);
            Assert.Equal(typeof(void), method.ReturnType);
            Assert.Equal(new[] { typeof(object), typeof(RoutedEventArgs) }, method.GetParameters().Select(parameter => parameter.ParameterType));
        }
    }
}
