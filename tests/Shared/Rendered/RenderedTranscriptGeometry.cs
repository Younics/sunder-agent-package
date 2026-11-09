extern alias AgentApp;

using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static partial class RenderedTranscriptGeometry
{
    internal static void AssertTranscriptTailGeometry(
        AgentChatView view,
        AgentChatViewModel viewModel)
    {
        var repeater = Assert.IsType<ItemsRepeater>(
            view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
        var transcript = GetTranscriptScrollViewer(view);
        var content = Assert.IsType<Border>(view.FindControl<Border>("TranscriptScrollContent"));
        var activityIndex = viewModel.TranscriptItems.Count - 2;
        var tailIndex = viewModel.TranscriptItems.Count - 1;
        Assert.True(repeater.TryGetElement(activityIndex) is Control,
            $"Activity row not realized at the tail: offset={transcript.Offset.Y},extent={transcript.Extent.Height},viewport={transcript.Viewport.Height},items={viewModel.TranscriptItems.Count}; {GetCoordinatorDiagnosticSnapshot(GetTranscriptScrollCoordinator(view))}");
        var activity = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(activityIndex));
        var tail = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(tailIndex));
        Assert.Same(viewModel.RunActivityRow, activity.DataContext);
        Assert.Same(viewModel.TranscriptItems[^1], tail.DataContext);
        Assert.False(tail.IsHitTestVisible);
        Assert.Equal(1, tail.Bounds.Height, precision: 3);
        Assert.Same(tail, transcript.CurrentAnchor);

        var signedDistance = transcript.Extent.Height - transcript.Viewport.Height - transcript.Offset.Y;
        Assert.InRange(signedDistance, -1, 1);
        var tailTop = Assert.IsType<Point>(tail.TranslatePoint(default, transcript)).Y;
        Assert.InRange(
            Math.Abs(tailTop + tail.Bounds.Height + content.Padding.Bottom - transcript.Viewport.Height),
            0,
            1);
        var activityTop = Assert.IsType<Point>(activity.TranslatePoint(default, repeater)).Y;
        var tailRepeaterTop = Assert.IsType<Point>(tail.TranslatePoint(default, repeater)).Y;
        Assert.InRange(Math.Abs(tailRepeaterTop + tail.Bounds.Height - repeater.Bounds.Height), 0, 1);

        if (viewModel.Messages.Count > 0)
        {
            var finalTransient = Assert.IsAssignableFrom<Control>(
                repeater.TryGetElement(viewModel.Messages.Count - 1));
            var finalTransientTop = Assert.IsType<Point>(
                finalTransient.TranslatePoint(default, repeater)).Y;
            var finalTransientBottom = finalTransientTop + finalTransient.Bounds.Height;
            if (activity.IsVisible)
            {
                Assert.InRange(Math.Abs(finalTransientBottom - activityTop), 0, 1);
                Assert.InRange(Math.Abs(activityTop + activity.Bounds.Height - tailRepeaterTop), 0, 1);
            }
            else
            {
                Assert.InRange(Math.Abs(finalTransientBottom - tailRepeaterTop), 0, 1);
            }
        }
    }

    internal static void AssertFollowingRowsMoveMonotonically(
        IReadOnlyList<IReadOnlyDictionary<object, double>> frames,
        bool expanding,
        IReadOnlyList<string>? details)
    {
        var comparedFollowingRows = 0;
        foreach (var (previous, current) in frames.Zip(frames.Skip(1)))
        {
            foreach (var (key, previousY) in previous)
            {
                if (!current.TryGetValue(key, out var currentY))
                {
                    continue;
                }

                comparedFollowingRows++;
                var delta = currentY - previousY;
                Assert.True(
                    expanding ? delta >= -0.1 : delta <= 0.1,
                    $"A realized row below the tool moved in the wrong direction ({key}: {previousY:F3} -> {currentY:F3}).{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
            }
        }
        Assert.True(
            comparedFollowingRows > 0,
            $"No continuously realized row below the tool was observed.{Environment.NewLine}{string.Join(Environment.NewLine, details ?? [])}");
    }

    internal static IReadOnlyDictionary<object, double> CaptureFollowingRowPositions<TRow>(
        IReadOnlyList<TRow> transcriptItems,
        ItemsRepeater repeater,
        ScrollViewer transcript,
        int anchorIndex,
        Func<TRow, object> getAnchorKey,
        Func<TRow, bool>? includeRow = null)
    {
        var positions = new Dictionary<object, double>();
        for (var index = anchorIndex + 1; index < transcriptItems.Count; index++)
        {
            if ((includeRow is not null && !includeRow(transcriptItems[index]))
                || repeater.TryGetElement(index) is not Control row
                || row.TranslatePoint(default, transcript) is not { } point)
            {
                continue;
            }

            positions[getAnchorKey(transcriptItems[index])] = point.Y;
        }

        return positions;
    }

    internal static bool GetAnchorHostSuspended(AgentChatView view)
    {
        var host = Assert.IsAssignableFrom<Control>(view.FindControl<Control>("TranscriptAnchorHost"));
        return Assert.IsType<bool>(host.GetType()
            .GetProperty(
                "IsAnchoringSuspended",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host));
    }

    internal static Task GetSettledScrollOperation(AgentChatView view)
    {
        var coordinator = GetTranscriptScrollCoordinator(view);
        return Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetField("_settledScrollOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));
    }

    internal static Task GetPendingCoordinatorOperations(AgentChatView view)
    {
        var coordinator = GetTranscriptScrollCoordinator(view);
        return Task.WhenAll((view.DataContext as AgentChatViewModel)?.PendingTranscriptActivities ?? Task.CompletedTask,
            Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetProperty(
                "PendingPagingOperations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)));
    }

    internal static Task GetViewportMutationCompletionOperation(object coordinator)
        => Assert.IsAssignableFrom<Task>(coordinator.GetType()
            .GetField(
                "_viewportMutationCompletionOperation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator));

    internal static bool InvokeQueueLoadOlderRows(object coordinator)
        => Assert.IsType<bool>(coordinator.GetType()
            .GetMethod(
                "QueueLoadOlderRows",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, null));

    internal static async Task WaitForPendingCoordinatorOperationsAsync(
        AgentChatView view,
        TimeSpan? timeout = null)
    {
        try
        {
            await GetPendingCoordinatorOperations(view).WaitAsync(
                timeout ?? TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException exception)
        {
            var coordinator = GetTranscriptScrollCoordinator(view);
            var snapshot = coordinator.GetType()
                .GetProperty(
                    "DiagnosticSnapshot",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(coordinator);
            throw new TimeoutException($"Transcript coordinator did not settle: {snapshot}", exception);
        }
    }

    internal static object GetTranscriptScrollCoordinator(AgentChatView view)
    {
        var behavior = GetTranscriptBehavior(view);
        return behavior.GetType()
            .GetField("_scrollCoordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(behavior)!;
    }

    internal static object GetTranscriptBehavior(AgentChatView view)
        => typeof(AgentChatView)
            .GetField("_transcriptBehavior", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view)!;

    internal static bool GetPrivateBoolean(object instance, string fieldName)
        => Assert.IsType<bool>(instance.GetType()
            .GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance));

    internal static Task GetPrivateTask(object instance, string fieldName)
        => Assert.IsAssignableFrom<Task>(instance.GetType()
            .GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance));

    internal static object? GetPrivateField(object instance, string fieldName)
        => instance.GetType()
            .GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance);

    internal static async Task WaitForTranscriptGeometrySettledAsync(
        Window window,
        ScrollViewer transcript,
        object coordinator)
    {
        const int maximumPasses = 100;
        const int requiredStablePasses = 2;
        (double Offset, double Extent, double Viewport)? previous = null;
        var stablePasses = 0;
        for (var pass = 0; pass < maximumPasses; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var current = (
                Offset: transcript.Offset.Y,
                Extent: transcript.Extent.Height,
                Viewport: transcript.Viewport.Height);
            var renderedContentPending = GetCoordinatorRenderedContentPending(coordinator);
            if (!renderedContentPending
                && previous is { } prior
                && Math.Abs(current.Offset - prior.Offset) <= 0.01
                && Math.Abs(current.Extent - prior.Extent) <= 0.01
                && Math.Abs(current.Viewport - prior.Viewport) <= 0.01)
            {
                stablePasses++;
                if (stablePasses >= requiredStablePasses)
                {
                    return;
                }
            }
            else
            {
                stablePasses = 0;
            }

            previous = current;
            await Task.Delay(1);
        }

        throw new TimeoutException(
            $"Transcript geometry did not settle: {GetCoordinatorDiagnosticSnapshot(coordinator)}");
    }

    internal static bool GetCoordinatorRenderedContentPending(object coordinator)
    {
        var snapshot = GetCoordinatorDiagnosticSnapshot(coordinator);
        return Assert.IsType<bool>(snapshot.GetType()
            .GetProperty("RenderedContentPending")!
            .GetValue(snapshot));
    }

    internal static object GetCoordinatorDiagnosticSnapshot(object coordinator)
        => coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;

    internal static void RecordDiagnostic(List<string> trace, string entry)
    {
        const int maximumEntries = 128;
        if (trace.Count == maximumEntries)
        {
            trace.RemoveAt(0);
        }
        trace.Add(entry);
    }

    internal static string DescribeTranscriptState(
        string stage,
        ScrollViewer transcript,
        object coordinator,
        object behavior,
        object? navigationGeneration)
    {
        var coordinatorSnapshot = GetCoordinatorDiagnosticSnapshot(coordinator);
        var behaviorSnapshot = behavior.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(behavior);
        return FormattableString.Invariant(
            $"{stage}: offset={transcript.Offset.Y:F3}, extent={transcript.Extent.Height:F3}, viewport={transcript.Viewport.Height:F3}, currentAnchor={transcript.CurrentAnchor?.DataContext?.GetType().Name ?? "null"}, navigationGeneration={navigationGeneration}, programmatic={GetPrivateBoolean(coordinator, "_isProgrammaticScroll")}, coordinator={coordinatorSnapshot}, placement={behaviorSnapshot}");
    }

    internal static object? CaptureCurrentScrollAnchorKey(object coordinator)
        => coordinator.GetType()
            .GetMethod(
                "CaptureCurrentScrollAnchorKey",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, null);

    internal static long GetCoordinatorProgrammaticOffsetWrites(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        return Assert.IsType<long>(snapshot.GetType()
            .GetProperty("ProgrammaticOffsetWrites")!
            .GetValue(snapshot));
    }

    internal static long GetCoordinatorAuthorityRevision(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        return Assert.IsType<long>(snapshot.GetType()
            .GetProperty("AuthorityRevision")!
            .GetValue(snapshot));
    }

    internal static long GetCoordinatorPresentationRevision(object coordinator)
    {
        var snapshot = GetCoordinatorDiagnosticSnapshot(coordinator);
        return Assert.IsType<long>(snapshot.GetType().GetProperty("PresentationRevision")!.GetValue(snapshot));
    }

    internal static string? GetCoordinatorMutationStatus(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        var mutation = snapshot.GetType().GetProperty("Mutation")!.GetValue(snapshot);
        return mutation?.GetType().GetProperty("Status")?.GetValue(mutation)?.ToString();
    }

    internal static string? GetCoordinatorMutationMode(object coordinator)
    {
        var snapshot = GetCoordinatorDiagnosticSnapshot(coordinator);
        var mutation = snapshot.GetType().GetProperty("Mutation")!.GetValue(snapshot);
        return mutation?.GetType().GetProperty("Mode")?.GetValue(mutation)?.ToString();
    }

    internal static object GetCoordinatorMutationDiagnostic(object coordinator)
    {
        var snapshot = coordinator.GetType()
            .GetProperty(
                "DiagnosticSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        var mutation = snapshot.GetType().GetProperty("Mutation")!.GetValue(snapshot);
        Assert.NotNull(mutation);
        return mutation;
    }

    internal static int GetCoordinatorMutationCollapseClamps(object coordinator)
    {
        var mutation = GetCoordinatorMutationDiagnostic(coordinator);
        return Assert.IsType<int>(mutation.GetType().GetProperty("CollapseClamps")!.GetValue(mutation));
    }

    internal static bool GetTranscriptFollowingLatest(object viewModel)
        => Assert.IsType<bool>(viewModel.GetType()
            .GetProperty(
                "IsTranscriptFollowingLatest",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewModel));

    internal static ScrollViewer GetTranscriptScrollViewer(AgentChatView view)
        => Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("TranscriptScrollViewer"));
}
