using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

public sealed partial class LocalExecutionSettingsViewModel : ObservableObject
{
    private readonly LocalExecutionAppRuntimeClient _runtimeClient;

    internal LocalExecutionSettingsViewModel(LocalExecutionAppRuntimeClient runtimeClient)
    {
        _runtimeClient = runtimeClient;
        SyntaxOptions =
        [
            new ShellSyntaxOption(AgentShellSyntaxKinds.PowerShell, "PowerShell"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.Cmd, "Command Prompt"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.PosixSh, "POSIX sh"),
            new ShellSyntaxOption(AgentShellSyntaxKinds.Custom, "Custom"),
        ];
        TimeoutSeconds = LocalExecutionConfiguration.DefaultTimeoutSeconds;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ReloadAsync(cancellationToken);
    }

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [LocalExecutionConfiguration.TimeoutKey];

    public ObservableCollection<LocalShellRowViewModel> Shells { get; } = [];

    public ObservableCollection<ShellSyntaxOption> SyntaxOptions { get; }

    [ObservableProperty]
    private LocalShellRowViewModel? _selectedShell;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _timeoutSeconds;

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
    private async Task DeleteSelectedShellAsync()
    {
        if (SelectedShell is not { IsDetected: false } shell)
        {
            return;
        }

        Shells.Remove(shell);
        SelectedShell = Shells.FirstOrDefault(candidate => !candidate.IsDetected) ?? Shells.FirstOrDefault();
        await SaveShellsAsync();
    }

    [RelayCommand]
    private async Task SaveShellsAsync()
    {
        var shells = new List<LocalShellDefinition>();
        foreach (var shell in Shells.Where(shell => !shell.IsDetected))
        {
            if (string.IsNullOrWhiteSpace(shell.ExecutablePath))
            {
                continue;
            }

            var path = shell.ExecutablePath.Trim();

            shells.Add(new LocalShellDefinition(
                shell.ShellId,
                string.IsNullOrWhiteSpace(shell.DisplayName) ? Path.GetFileNameWithoutExtension(path) : shell.DisplayName.Trim(),
                path,
                shell.SelectedSyntax?.SyntaxKind ?? AgentShellSyntaxKinds.Custom,
                IsDetected: false));
        }

        try
        {
            var response = await _runtimeClient.InvokeAsync(new LocalExecutionOperationRequest(
                LocalExecutionOperationKind.SaveShells,
                Shells: shells));
            ApplyShells(response.Shells ?? []);
            StatusText = response.Message ?? "Shell settings saved.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveExecutionSettingsAsync(CancellationToken cancellationToken)
    {
        if (!BoundedValue.TryParseInt32(
                TimeoutSeconds,
                minimum: 1,
                maximum: BoundedProcessRunner.MaximumTimeoutSeconds,
                out var timeoutSeconds))
        {
            StatusText = $"Local shell timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.";
            return;
        }

        try
        {
            var response = await _runtimeClient.InvokeAsync(new LocalExecutionOperationRequest(
                LocalExecutionOperationKind.SaveSettings,
                TimeoutSeconds: timeoutSeconds.ToString()), cancellationToken);
            TimeoutSeconds = response.TimeoutSeconds ?? timeoutSeconds.ToString();
            StatusText = response.Message ?? "Local execution settings saved.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanDeleteSelectedShell()
        => SelectedShell is { IsDetected: false };

    partial void OnSelectedShellChanged(LocalShellRowViewModel? value)
        => DeleteSelectedShellCommand.NotifyCanExecuteChanged();

    private async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var response = await _runtimeClient.InvokeAsync(
            new LocalExecutionOperationRequest(LocalExecutionOperationKind.GetSettings),
            cancellationToken);
        TimeoutSeconds = response.TimeoutSeconds ?? LocalExecutionConfiguration.DefaultTimeoutSeconds;
        ApplyShells(response.Shells ?? []);
    }

    private void ApplyShells(IReadOnlyList<LocalShellDefinition> shells)
    {
        Shells.Clear();

        foreach (var shell in shells)
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
        ExecutablePath = executablePath;
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

    private string _executablePath = string.Empty;

    public string ExecutablePath
    {
        get => _executablePath;
        private set => SetProperty(ref _executablePath, value);
    }

    internal bool ApplySelectedExecutablePath(string path)
    {
        if (path.Length is 0 or > 1024 || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        ExecutablePath = path;
        return true;
    }

    [ObservableProperty]
    private ShellSyntaxOption? _selectedSyntax;
}

public sealed record ShellSyntaxOption(string SyntaxKind, string DisplayName);
