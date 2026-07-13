namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class AdaptiveEditorStateCache<TState>
{
    private readonly Dictionary<(bool Wide, bool Expanded), TState> _states = [];

    public void Save(bool wide, bool expanded, TState state)
        => _states[(wide, expanded)] = state;

    public bool TryRestore(bool wide, bool expanded, out TState state)
        => _states.TryGetValue((wide, expanded), out state!);
}
