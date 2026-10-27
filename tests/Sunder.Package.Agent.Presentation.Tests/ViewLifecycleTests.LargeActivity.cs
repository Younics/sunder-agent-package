extern alias AgentApp;
extern alias SubagentsApp;

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using AgentApp::Sunder.Package.Agent.PackageViews;
using SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Tests;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class ViewLifecycleTests
{
    // Also lets an isolated native diagnostic runner reuse this workload with
    // routed pointer input, without initializing a headless platform.
    internal static Action<Window, Point, Vector> LargeActivityWheel =
        (window, point, delta) => window.MouseWheel(point, delta, RawInputModifiers.None);

    [AvaloniaTheory]
    [InlineData(false, 1003)]
    [InlineData(true, 1003)]
    [InlineData(false, 10_000)]
    [InlineData(true, 10_000)]
    public async Task LargeActivity_ContinuousScrollAndRepeatedCollapseReleaseRetiredRows(bool subsessions, int count)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profiles = services.ProfileService;
        var profile = await profiles.CreateProfileAsync("Large transcript");
        var workspace = services.WorkspaceService.CreateWorkspace("Large transcript");
        var parent = services.SessionService.CreateSession("Parent", workspaceId: workspace.WorkspaceId);
        var session = subsessions
            ? services.SessionService.CreateSession("Child", parentSessionId: parent.SessionId,
                rootSessionId: parent.SessionId, profileId: profile.ProfileId, agentKind: "subagent")
            : parent;
        SeedLargeActivity(new AgentLocalStore(scope.Context).DatabasePath, session.SessionId, count);
        services.ExtensionCatalog.AddRuntimeCatalog(new AgentRuntimeCatalog(services.SessionService, profiles, services.WorkspaceService));
        using var adapter = subsessions ? new CatalogBackedSubsessionRuntimeAdapter(scope.Context, services.ExtensionCatalog) : null;
        using var lifetime = subsessions ? (IDisposable)new SubsessionsView(adapter!.CreateViewModel()) : CreateAgentChatView(scope, services, profiles);
        var view = (Control)lifetime;
        var window = new Window { Width = 850, Height = 700, Content = view };
        window.Show();
        try
        {
            if (view is SubsessionsView child)
                await child.OnNavigatedToAsync(new(SubagentConstants.SubsessionsViewId,
                    new Dictionary<string, string?> { ["sessionId"] = session.SessionId.ToString("D") }));
            else await ((AgentChatView)view).OnNavigatedToAsync(new("younics.ai.agent.chat", new Dictionary<string, string?>()));
            await PendingLargeActivity(view).WaitAsync(TimeSpan.FromSeconds(90));
            var group = view.DataContext is AgentChatViewModel agent
                ? (object)Assert.Single(agent.TranscriptItems.OfType<AgentActivityGroupRowViewModel>())
                : Assert.Single(((SubsessionsViewModel)view.DataContext!).TranscriptItems.OfType<SubsessionActivityGroupRowViewModel>());
            var expanded = group.GetType().GetProperty("IsExpanded")!;
            var viewer = view.FindControl<ScrollViewer>("TranscriptScrollViewer")!;
            var repeater = view.FindControl<ItemsRepeater>("TranscriptItemsControl")!;
            var retained = new List<WeakReference>();
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var timer = Stopwatch.StartNew();
                expanded.SetValue(group, true);
                try { await PendingLargeActivity(view).WaitAsync(TimeSpan.FromSeconds(90)); }
                catch (TimeoutException error)
                {
                    var coordinator = view is AgentChatView chat ? (object)GetTranscriptScrollCoordinator(chat) : GetTranscriptScrollCoordinator((SubsessionsView)view);
                    var diagnostic = coordinator.GetType().GetProperty("DiagnosticSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator);
                    var timeline = view.DataContext!.GetType().GetField("_timeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view.DataContext)!;
                    var members = (Task)timeline.GetType().GetProperty("PendingCompactOperations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timeline)!;
                    var items = (System.Collections.ICollection)view.DataContext.GetType().GetProperty("TranscriptItems")!.GetValue(view.DataContext)!;
                    throw new TimeoutException($"Expansion {subsessions}/{count}: items={items.Count}, members={members.Status}, loading={group.GetType().GetProperty("LoadingText")!.GetValue(group)}, scroll={diagnostic}", error);
                }
                TraceLargeActivity($"large-activity-expanded subsessions={subsessions} members={count} elapsed-ms={timer.ElapsedMilliseconds}");
                await RenderLargeActivityFrameAsync(window);
                var point = viewer.TranslatePoint(new Point(viewer.Bounds.Width / 2, viewer.Bounds.Height / 2), window)!.Value;
                LargeActivityWheel(window, point, new Vector(0, 3));
                await RenderLargeActivityFrameAsync(window);
                var middleAnchor = view.DataContext is AgentChatViewModel chatModel
                    ? chatModel.TranscriptItems[count / 2].AnchorKey
                    : ((SubsessionsViewModel)view.DataContext!).TranscriptItems[count / 2].AnchorKey;
                // Navigate through the repeater's normal realization protocol;
                // a raw estimated pixel jump can leave its made-anchor pending.
                Assert.NotNull(view.GetType().GetMethod("RealizeTranscriptAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, [middleAnchor]));
                await RenderLargeActivityFrameAsync(window);
                Assert.InRange(CaptureLargeActivityPosition(repeater, viewer), count / 2d - 30, count / 2d + 30);
                var inputSamples = new List<double>();
                foreach (var direction in new[] { 1, -1 })
                {
                    // No coordinator-settlement waits between input events: the
                    // reader must not need a direction reversal to regain motion.
                    for (var step = 0; step < 24; step++)
                    {
                        var before = viewer.Offset.Y;
                        var beforePosition = CaptureLargeActivityPosition(repeater, viewer);
                        var started = Stopwatch.GetTimestamp();
                        LargeActivityWheel(window, point, new Vector(0, direction * 2));
                        await RenderLargeActivityFrameAsync(window);
                        inputSamples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        var position = CaptureLargeActivityPosition(repeater, viewer);
                        TraceLargeActivity($"scroll subsessions={subsessions} count={count} cycle={cycle} direction={direction} step={step} logical={beforePosition:F3}->{position:F3} offset={before:F1}->{viewer.Offset.Y:F1} extent={viewer.Extent.Height:F1} children={repeater.GetVisualChildren().Count()}");
                        Assert.True(direction > 0 ? position < beforePosition : position > beforePosition,
                            $"{subsessions}/{count}: logical scroll stopped at cycle {cycle}, direction {direction}, step {step}; position {beforePosition} -> {position}, offset {before} -> {viewer.Offset.Y}, extent {viewer.Extent.Height}.");
                    }
                }
                Assert.InRange(repeater.GetVisualChildren().Count(), 1, 300);
                retained.AddRange(await CaptureLargeActivityDetailsAsync(view, viewer));
                expanded.SetValue(group, false);
                await PendingLargeActivity(view).WaitAsync(TimeSpan.FromSeconds(90));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                inputSamples.Sort();
                TraceLargeActivity($"large-activity subsessions={subsessions} members={count} cycle={cycle} elapsed-ms={timer.ElapsedMilliseconds} routed-input-layout-p95-ms={inputSamples[(int)(inputSamples.Count * 0.95)]:F3} max-ms={inputSamples[^1]:F3} managed={GC.GetTotalMemory(false)} working-set={Environment.WorkingSet}");
            }
            for (var attempt = 0; attempt < 10 && retained.Any(reference => reference.IsAlive); attempt++)
            {
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.All(retained, reference => Assert.False(reference.IsAlive, "A collapsed transcript retained a retired row/detail payload."));
            TraceLargeActivity($"large-activity-retained subsessions={subsessions} members={count} managed={GC.GetTotalMemory(true)}");
            GC.KeepAlive(view);
        }
        finally { window.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> CaptureLargeActivityDetailsAsync(Control view, ScrollViewer viewer)
    {
        var header = view.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("tool-step-header")
            && button.TranslatePoint(default, viewer) is { Y: >= 0 } position && position.Y < viewer.Viewport.Height - button.Bounds.Height);
        var row = header.DataContext!;
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => (bool)row.GetType().GetProperty("IsExpanded")!.GetValue(row)!);
        await PendingLargeActivity(view).WaitAsync(TimeSpan.FromSeconds(30));
        var details = row.GetType().GetProperty("ExpandedDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row);
        Assert.NotNull(details);
        return [new(row), new(details), .. view.GetVisualDescendants()
            .Where(visual => visual.GetType().Name == "StableMarkdownRenderer").Select(visual => new WeakReference(visual))];
    }

    private static void SeedLargeActivity(string databasePath, Guid session, int count)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var run = Guid.NewGuid();
        for (var index = 0; index < count; index++)
        {
            var turn = Guid.NewGuid();
            var execution = Guid.NewGuid();
            var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index);
            AgentLocalStore.InsertTurn(connection, transaction, new AgentTurnRecord(turn, session, AgentMessageRole.Tool, AgentTurnKind.ToolResult,
                [new(Guid.NewGuid(), turn, 0, AgentTurnItemKind.ToolResult, "Fixture detail output", execution.ToString(), "read",
                    "{\"path\":\"fixture.txt\"}", "Read fixture.txt", null, null, false, false, null, null)
                    { ToolExecutionId = execution }], timestamp, timestamp) { RunId = run, RunRevision = 1 });
        }
        transaction.Commit();
    }

    private static Task PendingLargeActivity(Control view)
        => view is AgentChatView agent ? GetPendingCoordinatorOperations(agent) : GetPendingCoordinatorOperations((SubsessionsView)view);

    private static async Task RenderLargeActivityFrameAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static void TraceLargeActivity(string message)
    {
        Console.WriteLine(message);
        if (Environment.GetEnvironmentVariable("SUNDER_TRANSCRIPT_LARGE_TRACE") is { Length: > 0 } path)
            File.AppendAllText(path, message + Environment.NewLine);
    }

    private static double CaptureLargeActivityPosition(ItemsRepeater repeater, ScrollViewer viewer)
    {
        var first = repeater.GetVisualChildren().OfType<Control>()
            .Select(visual => (Visual: visual, Index: repeater.GetElementIndex(visual), Top: visual.TranslatePoint(default, viewer)?.Y))
            .Where(item => item.Index >= 0 && item.Visual.IsEffectivelyVisible && item.Visual.Bounds.Height > 0
                && item.Top is { } top && top + item.Visual.Bounds.Height > 0 && top < viewer.Viewport.Height)
            .OrderBy(item => item.Top).First();
        return first.Index - first.Top!.Value / first.Visual.Bounds.Height;
    }
}
