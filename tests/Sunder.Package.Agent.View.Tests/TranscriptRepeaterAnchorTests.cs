extern alias AgentApp;

using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Transcript = AgentApp::Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class TranscriptRepeaterAnchorTests
{
    [AvaloniaFact]
    public async Task AnchorCaptureInspectsOnlyRetainedControlsAndExcludesRecycledAndParkedElements()
    {
        var rows = Enumerable.Range(0, 10_000).Select(_ => new Row()).ToArray();
        var counted = new CountingRows(rows);
        var repeater = new ItemsRepeater
        {
            ItemsSource = rows,
            Layout = new StackLayout(),
            ItemTemplate = new FuncDataTemplate<Row>((_, _) => new Transcript.TranscriptRowPresenter
            {
                Height = 40, Width = 500, IsRepeaterHosted = true,
            }),
        };
        var viewer = new ScrollViewer { Content = repeater };
        var window = new Window { Width = 600, Height = 500, Content = viewer };
        window.Show();
        try
        {
            foreach (var offset in new[] { 0, 20_000, 100_000, 50_000, 0 })
            {
                viewer.Offset = new Vector(0, offset);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                counted.Reads = 0;
                var anchors = Transcript.TranscriptRepeaterAnchors.Enumerate(repeater, counted).ToArray();
                Assert.NotEmpty(anchors);
                var controls = repeater.GetVisualChildren().OfType<Control>().ToArray();
                Assert.InRange(counted.Reads, anchors.Length, controls.Length);
                Assert.InRange(controls.Length, 1, 200);
                Assert.All(anchors, anchor =>
                {
                    var index = repeater.GetElementIndex(anchor.Visual);
                    Assert.InRange(index, 0, rows.Length - 1);
                    Assert.Same(rows[index], anchor.Visual.DataContext);
                    Assert.Equal(rows[index].AnchorKey, anchor.Item);
                    Assert.True(Transcript.TranscriptRowPresenter.HasLayoutGeometry(anchor.Visual));
                });
            }
        }
        finally { window.Close(); }
    }

    private sealed class Row : Transcript.ITranscriptAnchorItem
    {
        public object AnchorKey { get; } = new();
        public Transcript.TranscriptAnchorItemRole AnchorRole => Transcript.TranscriptAnchorItemRole.Transient;
    }

    private sealed class CountingRows(Row[] rows) : IReadOnlyList<Row>
    {
        public int Reads;
        public int Count => rows.Length;
        public Row this[int index] { get { Reads++; return rows[index]; } }
        public IEnumerator<Row> GetEnumerator() => ((IEnumerable<Row>)rows).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
