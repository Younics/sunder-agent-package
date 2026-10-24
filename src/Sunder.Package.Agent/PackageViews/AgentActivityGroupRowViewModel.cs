using System.ComponentModel;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
public sealed class AgentActivityGroupRowViewModel : AgentTranscriptRowViewModel, IDisposable
{
    private readonly TranscriptActivityGroupState _state;
    internal AgentActivityGroupRowViewModel(AgentTranscriptRowViewModel first, TranscriptActivityGroupState state)
        : base(first.RowId, first.CreatedAtUtc, state.Anchor)
    {
        _state = state;
        state.PropertyChanged += OnStateChanged;
    }
    public string Summary => _state.Summary;
    public string StatusText => _state.StatusText;
    public bool IsRunning => _state.IsRunning;
    public string ExpandGlyph => _state.ExpandGlyph;
    public string AccessibilityText => _state.AccessibilityText;
    public bool IsExpanded { get => _state.IsExpanded; set => _state.IsExpanded = value; }
    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) => OnPropertyChanged(args.PropertyName);
    public void Dispose() => _state.PropertyChanged -= OnStateChanged;
}
