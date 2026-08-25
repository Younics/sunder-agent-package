using System.ComponentModel;

namespace Sunder.Package.Agent.TestSupport.Avalonia;

public enum AdaptiveEditorRuntimeState
{
    Unknown,
    Available,
    Unavailable,
    Recovering,
}

public sealed record AdaptiveEditorSnapshot(
    bool IsHydrating,
    bool CanMutate,
    bool IsBusy,
    bool IsDirty,
    string? SelectionKey,
    string? DraftValue,
    int PublicationCount,
    AdaptiveEditorRuntimeState RuntimeState = AdaptiveEditorRuntimeState.Unknown);

public interface IAdaptiveEditorLifecycleAdapter
{
    string Name { get; }

    AdaptiveEditorSnapshot Capture();

    IDisposable Subscribe(Action<AdaptiveEditorSnapshot> observer);
}

public sealed class DelegateAdaptiveEditorLifecycleAdapter(
    string name,
    Func<AdaptiveEditorSnapshot> capture,
    INotifyPropertyChanged notifications) : IAdaptiveEditorLifecycleAdapter
{
    public string Name { get; } = name;

    public AdaptiveEditorSnapshot Capture() => capture();

    public IDisposable Subscribe(Action<AdaptiveEditorSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        PropertyChangedEventHandler handler = (_, _) => observer(Capture());
        notifications.PropertyChanged += handler;
        observer(Capture());
        return new Subscription(() => notifications.PropertyChanged -= handler);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class AdaptiveEditorContractException(string message) : Exception(message);

public static class AdaptiveEditorLifecycleContract
{
    public static void HydrationBlocksMutation(IAdaptiveEditorLifecycleAdapter adapter)
    {
        var snapshot = Capture(adapter);
        Require(snapshot.IsHydrating, adapter, "hydration was not observable");
        Require(!snapshot.CanMutate, adapter, "mutation remained enabled before hydration completed");
    }

    public static void SharedInitialization(
        IAdaptiveEditorLifecycleAdapter adapter,
        Task first,
        Task second)
    {
        _ = Capture(adapter);
        Require(ReferenceEquals(first, second), adapter, "concurrent initialization did not share one task");
    }

    public static void SharedInitialization(
        IAdaptiveEditorLifecycleAdapter adapter,
        int observedInitializationStarts)
    {
        _ = Capture(adapter);
        Require(
            observedInitializationStarts == 1,
            adapter,
            $"concurrent initialization started {observedInitializationStarts} underlying loads");
    }

    public static void CurrentSelectionWins(
        IAdaptiveEditorLifecycleAdapter adapter,
        string expectedSelectionKey,
        string? rejectedDraftValue = null)
    {
        var snapshot = Capture(adapter);
        Require(
            string.Equals(snapshot.SelectionKey, expectedSelectionKey, StringComparison.Ordinal),
            adapter,
            $"stale completion replaced selection '{expectedSelectionKey}' with '{snapshot.SelectionKey ?? "<null>"}'");
        if (rejectedDraftValue is not null)
        {
            Require(
                !string.Equals(snapshot.DraftValue, rejectedDraftValue, StringComparison.Ordinal),
                adapter,
                "stale completion published its draft value");
        }
    }

    public static void EditDuringSaveRemainsDirty(
        IAdaptiveEditorLifecycleAdapter adapter,
        string expectedDraftValue)
        => NewerEditRemainsDirty(adapter, expectedDraftValue, "save completion");

    public static void NewerEditRemainsDirty(
        IAdaptiveEditorLifecycleAdapter adapter,
        string expectedDraftValue,
        string completionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completionName);
        var snapshot = Capture(adapter);
        Require(
            string.Equals(snapshot.DraftValue, expectedDraftValue, StringComparison.Ordinal),
            adapter,
            $"{completionName} overwrote a newer edit");
        Require(snapshot.IsDirty, adapter, $"{completionName} incorrectly marked the newer edit clean");
    }

    public static async Task RetiredWorkCannotPublishAsync(
        IAdaptiveEditorLifecycleAdapter adapter,
        Action retire,
        Action releaseBlockedWork,
        Task drainage)
    {
        ArgumentNullException.ThrowIfNull(retire);
        ArgumentNullException.ThrowIfNull(releaseBlockedWork);
        ArgumentNullException.ThrowIfNull(drainage);
        var publications = 0;
        using var subscription = Subscribe(adapter, _ => Interlocked.Increment(ref publications));

        retire();
        var publicationsAtRetirement = Volatile.Read(ref publications);
        releaseBlockedWork();
        await drainage.ConfigureAwait(false);
        await Task.Yield();

        var snapshot = Capture(adapter);
        Require(!snapshot.IsBusy && !snapshot.IsHydrating, adapter, "retired work left the editor busy");
        Require(
            Volatile.Read(ref publications) == publicationsAtRetirement,
            adapter,
            $"late work published {Volatile.Read(ref publications) - publicationsAtRetirement} state changes");
    }

    public static async Task RuntimeCycleIsExplicitAsync(
        IAdaptiveEditorLifecycleAdapter adapter,
        Func<Task> exercise)
    {
        ArgumentNullException.ThrowIfNull(exercise);
        var transitions = new List<AdaptiveEditorRuntimeState>();
        using var subscription = Subscribe(adapter, snapshot =>
        {
            lock (transitions)
            {
                if (transitions.Count == 0 || transitions[^1] != snapshot.RuntimeState)
                {
                    transitions.Add(snapshot.RuntimeState);
                }
            }
        });

        await exercise().ConfigureAwait(false);
        _ = Capture(adapter);

        AdaptiveEditorRuntimeState[] observed;
        lock (transitions)
        {
            observed = [.. transitions];
        }
        var unavailable = observed.IndexOf(AdaptiveEditorRuntimeState.Unavailable);
        var recovered = observed.LastIndexOf(AdaptiveEditorRuntimeState.Available);
        if (unavailable < 0 || recovered <= unavailable)
        {
            throw new AdaptiveEditorContractException(
                $"Adaptive editor '{adapter.Name}' did not expose an unavailable-to-available runtime transition: "
                + string.Join(", ", observed));
        }
    }

    private static AdaptiveEditorSnapshot Capture(IAdaptiveEditorLifecycleAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.Name);
        return adapter.Capture();
    }

    private static IDisposable Subscribe(
        IAdaptiveEditorLifecycleAdapter adapter,
        Action<AdaptiveEditorSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.Name);
        return adapter.Subscribe(observer);
    }

    private static void Require(
        bool condition,
        IAdaptiveEditorLifecycleAdapter adapter,
        string violation)
    {
        if (!condition)
        {
            throw new AdaptiveEditorContractException(
                $"Adaptive editor '{adapter.Name}' violated the lifecycle contract: {violation}.");
        }
    }

    private static int IndexOf<T>(this IReadOnlyList<T> values, T expected)
        where T : struct, Enum
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(values[index], expected))
            {
                return index;
            }
        }
        return -1;
    }

    private static int LastIndexOf<T>(this IReadOnlyList<T> values, T expected)
        where T : struct, Enum
    {
        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (EqualityComparer<T>.Default.Equals(values[index], expected))
            {
                return index;
            }
        }
        return -1;
    }
}
