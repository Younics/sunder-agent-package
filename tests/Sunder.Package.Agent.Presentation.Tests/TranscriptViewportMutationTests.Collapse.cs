extern alias AgentApp;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class TranscriptViewportMutationTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledCollapseReleasesCompensationWithoutWritingAnOffset(bool deactivate)
    {
        var source = new ControlledGeometrySource();
        var detail = new TranscriptToolDetailHost();
        detail.AttachPrepared(new CollapseOwner(), new Border { Height = 800 });
        var first = CreateRow("first", 200);
        var tool = CreateAutoRow("tool", new StackPanel { Children = { source, new Border { Height = 60 }, detail } });
        var last = CreateRow("last", 600);
        var rows = new StackPanel { Children = { first, tool, last } };
        var host = CreateAnchorHost(rows, first, tool, last);
        var viewer = new ScrollViewer { Content = host };
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        try
        {
            await LayoutAsync(window);
            viewer.Offset = new Vector(0, 150);
            await LayoutAsync(window);
            using var coordinator = CreateCoordinator(viewer, rows, host, isFollowingLatest: false);
            source.GeometryChanged += (_, _) => coordinator.OnRenderedContentChanged(source);
            coordinator.BeginViewportMutation(TranscriptViewportMutationKind.ToolExpansion, tool.AnchorKey, tool, isExpanding: false);
            source.Request();
            detail.BeginCollapseCompensation();
            detail.Clear(notifyOwner: false, preserveCollapseCompensator: true);
            coordinator.OnViewportContentChanged();
            await LayoutAsync(window);
            Assert.True(host.HasExactAnchorLease);
            Assert.True(detail.Bounds.Height > 0);
            var writes = coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites;

            if (deactivate) coordinator.SetPresentationActive(false);
            else window.MouseWheel(new Point(150, 120), new Vector(0, 1), RawInputModifiers.None);
            source.Settle();
            await coordinator.PendingPagingOperations.WaitAsync(TimeSpan.FromSeconds(3));
            await LayoutAsync(window);

            Assert.Equal(writes, coordinator.DiagnosticSnapshot.ProgrammaticOffsetWrites);
            Assert.Equal(deactivate ? TranscriptViewportMutationStatus.PresentationInactive : TranscriptViewportMutationStatus.CanceledByAuthority,
                coordinator.DiagnosticSnapshot.Mutation?.Status);
            Assert.False(host.HasExactAnchorLease);
            Assert.False(host.IsTrailingCompensatorActive);
            Assert.Equal(0, host.AnchoringSuspensionDepth);
            Assert.Equal(0, detail.Bounds.Height);
        }
        finally { window.Close(); }
    }

    private sealed class CollapseOwner : ITranscriptToolExpansionOwner
    {
        public event Action? DetailVisualInvalidated;
        public void OnDetailVisualInvalidated() => DetailVisualInvalidated?.Invoke();
    }
}
