using System.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
public sealed class AgentActivityGroupRowViewModel : AgentTranscriptRowViewModel, IDisposable
{
    private readonly TranscriptActivityGroupState _state;
    internal AgentActivityGroupRowViewModel(AgentTranscriptActivityGroup group, TranscriptActivityGroupState state)
        : base(group.First.TurnId, group.First.CreatedAtUtc, state.Anchor)
    {
        _state = state;
        state.PropertyChanged += OnStateChanged;
    }
    internal AgentActivityGroupRowViewModel(AgentTranscriptRowViewModel first, TranscriptActivityGroupState state)
        : base(first.RowId, first.CreatedAtUtc, state.Anchor)
    {
        _state = state;
        state.PropertyChanged += OnStateChanged;
    }
    public string Summary => _state.Summary;
    public string StatusText => _state.StatusText;
    public bool IsRunning => _state.IsRunning;
    public Avalonia.Media.IBrush? StateBrush => AgentThemeBrushes.Resolve(TranscriptToolStates.Brush(_state.State));
    public string StatusIconText => _state.State == TranscriptToolState.Completed ? TranscriptToolStates.Icon(_state.State) : string.Empty;
    public string ExpandGlyph => _state.ExpandGlyph;
    public string AccessibilityText => _state.AccessibilityText;
    public string LoadingText => _state.LoadingText;
    public bool IsExpanded { get => _state.IsExpanded; set => _state.IsExpanded = value; }
    private void OnStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(args.PropertyName);
        if (args.PropertyName == nameof(TranscriptActivityGroupState.State))
        {
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(StatusIconText));
        }
    }
    public void Dispose() => _state.PropertyChanged -= OnStateChanged;
}
