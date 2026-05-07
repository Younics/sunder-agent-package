using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

public sealed partial class LocalExecutionSettingsViewModel : ObservableObject
{
    private readonly LocalShellCatalogService _shellCatalogService;

    public LocalExecutionSettingsViewModel(LocalShellCatalogService shellCatalogService)
    {
        _shellCatalogService = shellCatalogService;
        SyntaxOptions =
        [
            new ShellSyntaxOption(AgentShellSyntaxKinds.PowerShell, "PowerShell"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.Cmd, "Command Prompt"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.PosixSh, "POSIX sh"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.Custom, "Custom"),
        ];
        Reload();
    }

    public ObservableCollection<LocalShellRowViewModel> Shells { get; } = [];

    public ObservableCollection<ShellSyntaxOption> SyntaxOptions { get; }

    [ObservableProperty]
    private LocalShellRowViewModel? _selectedShell;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [RelayCommand]
    private void AddShell()
    {
        var row = new LocalShellRowViewModel(
            "custom-" + Guid.NewGuid().ToString("N"),
            "Shell",
            string.Empty,
            AgentShellSyntaxKinds.Custom,
            false,
            SyntaxOptions);
        Shells.Add(row);
        SelectedShell = row;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedShell))]
    private void DeleteSelectedShell()
    {
        if (SelectedShell is not { IsDetected: false } shell)
        {
            return;
        }

        Shells.Remove(shell);
        SelectedShell = Shells.FirstOrDefault(candidate => !candidate.IsDetected) ?? Shells.FirstOrDefault();
        SaveShells();
    }

    [RelayCommand]
    private void SaveShells()
    {
        var shells = new List<LocalShellDefinition>();
        foreach (var shell in Shells.Where(shell => !shell.IsDetected))
        {
            if (string.IsNullOrWhiteSpace(shell.ExecutablePath))
            {
                continue;
            }

            var path = Environment.ExpandEnvironmentVariables(shell.ExecutablePath.Trim());
            if (!File.Exists(path))
            {
                StatusText = $"Shell executable does not exist: {path}";
                return;
            }

            shells.Add(new LocalShellDefinition(
                shell.ShellId,
                string.IsNullOrWhiteSpace(shell.DisplayName) ? Path.GetFileNameWithoutExtension(path) : shell.DisplayName.Trim(),
                path,
                shell.SelectedSyntax?.SyntaxKind ?? AgentShellSyntaxKinds.Custom,
                IsDetected: false));
        }

        _shellCatalogService.SaveCustomShells(shells);
        Reload();
        StatusText = "Shell settings saved.";
    }

    private bool CanDeleteSelectedShell()
        => SelectedShell is { IsDetected: false };

    partial void OnSelectedShellChanged(LocalShellRowViewModel? value)
        => DeleteSelectedShellCommand.NotifyCanExecuteChanged();

    private void Reload()
    {
        Shells.Clear();

        foreach (var shell in _shellCatalogService.ListShells())
        {
            Shells.Add(CreateRow(shell));
        }

        SelectedShell = Shells.FirstOrDefault(shell => !shell.IsDetected) ?? Shells.FirstOrDefault();
    }

    private LocalShellRowViewModel CreateRow(LocalShellDefinition shell)
        => new(shell.ShellId, shell.DisplayName, shell.ExecutablePath, shell.SyntaxKind, shell.IsDetected, SyntaxOptions);
}

public sealed partial class LocalShellRowViewModel : ObservableObject
{
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
        _selectedSyntax = SyntaxOptions.FirstOrDefault(option => string.Equals(option.SyntaxKind, syntaxKind, StringComparison.OrdinalIgnoreCase))
                          ?? SyntaxOptions.First(option => option.SyntaxKind == AgentShellSyntaxKinds.Custom);
    }

    public string ShellId { get; }

    public bool IsDetected { get; }

    public bool CanEdit => !IsDetected;

    public IReadOnlyList<ShellSyntaxOption> SyntaxOptions { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _executablePath;

    [ObservableProperty]
    private ShellSyntaxOption? _selectedSyntax;
}

public sealed record ShellSyntaxOption(string SyntaxKind, string DisplayName);
