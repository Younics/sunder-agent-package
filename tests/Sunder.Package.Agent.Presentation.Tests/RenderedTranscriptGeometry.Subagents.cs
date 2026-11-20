using Avalonia.Controls;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static partial class RenderedTranscriptGeometry
{
    internal static bool GetAnchorHostSuspended(SubsessionsView view)
    {
        var host = Assert.IsAssignableFrom<Control>(view.FindControl<Control>("TranscriptAnchorHost"));
        return Assert.IsType<bool>(host.GetType()
            .GetProperty(
                "IsAnchoringSuspended",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host));
    }

    internal static Task GetPendingCoordinatorOperations(SubsessionsView view)
    {
        var coordinator = GetTranscriptScrollCoordinator(view);
        return Task.WhenAll((view.DataContext as SubsessionsViewModel)?.PendingTranscriptActivities ?? Task.CompletedTask,
            Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetProperty(
                "PendingPagingOperations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)));
    }

    internal static Task GetPendingRuntimeRefresh(SubsessionsViewModel viewModel)
    {
        var refresh = typeof(SubsessionsViewModel)
            .GetField(
                "_runtimeRefresh",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        return Assert.IsAssignableFrom<Task>(refresh.GetType()
            .GetProperty("WhenIdle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .GetValue(refresh));
    }

    internal static async Task WaitForPendingCoordinatorOperationsAsync(SubsessionsView view)
    {
        try
        {
            await GetPendingCoordinatorOperations(view).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException exception)
        {
            var coordinator = GetTranscriptScrollCoordinator(view);
            var snapshot = coordinator.GetType()
                .GetProperty(
                    "DiagnosticSnapshot",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(coordinator);
            throw new TimeoutException($"Subsession transcript coordinator did not settle: {snapshot}", exception);
        }
    }

    internal static object GetTranscriptScrollCoordinator(SubsessionsView view)
    {
        var behavior = GetTranscriptBehavior(view);
        return behavior.GetType()
            .GetField("_scrollCoordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(behavior)!;
    }

    internal static object GetTranscriptBehavior(SubsessionsView view)
        => typeof(SubsessionsView)
            .GetField("_transcriptBehavior", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view)!;

    internal static string DescribeSubsessionTranscriptState(
        SubsessionsView view,
        ScrollViewer transcript,
        object coordinator,
        string stage)
        => DescribeTranscriptState(
            stage,
            transcript,
            coordinator,
            GetTranscriptBehavior(view),
            GetPrivateField(view, "_navigationGeneration"));
}
