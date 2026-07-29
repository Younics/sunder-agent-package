using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

public sealed partial class LocalShellRowViewModel : ObservableObject
{
    private bool _suppressRevisionTracking;
    private long _displayNameRevision;
    private long _pathRevision;
    private long _syntaxRevision;

    public LocalShellRowViewModel(
        string shellId,
        string displayName,
        string executablePath,
        string syntaxKind,
        bool isDetected,
        IReadOnlyList<ShellSyntaxOption> syntaxOptions)
    {
        ShellId = shellId;
        _displayName = displayName;
        _executablePath = executablePath;
        IsDetected = isDetected;
        SyntaxOptions = syntaxOptions;
        _selectedSyntax = ResolveSyntax(syntaxKind);
    }

    public string ShellId { get; }

    public bool IsDetected { get; }

    public bool CanEdit => !IsDetected;

    public bool IsDraft => !IsDetected && string.IsNullOrWhiteSpace(ExecutablePath);

    public IReadOnlyList<ShellSyntaxOption> SyntaxOptions { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _executablePath;

    [ObservableProperty]
    private ShellSyntaxOption? _selectedSyntax;

    internal bool ApplySelectedExecutablePath(string path)
    {
        if (!CanEdit || path.Length is 0 or > 1024 || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        ExecutablePath = path;
        return true;
    }

    internal LocalShellRowRevision CaptureRevision()
        => new(_displayNameRevision, _pathRevision, _syntaxRevision);

    internal bool HasChangedSince(LocalShellRowRevision revision)
        => CaptureRevision() != revision;

    internal void Apply(LocalShellDefinition shell, LocalShellRowRevision expectedRevision)
    {
        if (!string.Equals(ShellId, shell.ShellId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot reconcile shell rows with different identifiers.");
        }

        _suppressRevisionTracking = true;
        try
        {
            if (IsDetected || _displayNameRevision == expectedRevision.DisplayName)
            {
                DisplayName = shell.DisplayName;
            }
            if (IsDetected || _pathRevision == expectedRevision.ExecutablePath)
            {
                ExecutablePath = shell.ExecutablePath;
            }
            if (IsDetected || _syntaxRevision == expectedRevision.Syntax)
            {
                SelectedSyntax = ResolveSyntax(shell.SyntaxKind);
            }
        }
        finally
        {
            _suppressRevisionTracking = false;
        }
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (!_suppressRevisionTracking)
        {
            _displayNameRevision++;
        }
    }

    partial void OnExecutablePathChanged(string value)
    {
        if (!_suppressRevisionTracking)
        {
            _pathRevision++;
        }
        OnPropertyChanged(nameof(IsDraft));
    }

    partial void OnSelectedSyntaxChanged(ShellSyntaxOption? value)
    {
        if (!_suppressRevisionTracking)
        {
            _syntaxRevision++;
        }
    }

    private ShellSyntaxOption ResolveSyntax(string? syntaxKind)
        => SyntaxOptions.FirstOrDefault(option => string.Equals(
               option.SyntaxKind,
               syntaxKind,
               StringComparison.OrdinalIgnoreCase))
           ?? SyntaxOptions.First(option => option.SyntaxKind == AgentShellSyntaxKinds.Custom);
}

internal readonly record struct LocalShellRowRevision(
    long DisplayName,
    long ExecutablePath,
    long Syntax);

public sealed record ShellSyntaxOption(string SyntaxKind, string DisplayName);
