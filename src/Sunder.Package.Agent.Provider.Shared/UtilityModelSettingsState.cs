using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Shared;

internal sealed record ProviderUtilityModelOption(string ModelId, string DisplayName);

internal sealed partial class UtilityModelSettingsState : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDuration = TimeSpan.FromSeconds(4);
    private readonly IPackageContext _packageContext;
    private readonly string _configurationKey;
    private readonly string _defaultModelId;
    private readonly Func<string?, string?> _normalizeModelId;
    private readonly OperationState _operation = new();
    private readonly TimedStatusController _statusTimer;

    internal UtilityModelSettingsState(
        IPackageContext packageContext,
        string configurationKey,
        string defaultModelId,
        IEnumerable<(string ModelId, string DisplayName)> options,
        Func<string?, string?>? normalizeModelId = null,
        TimeProvider? timeProvider = null)
    {
        _packageContext = packageContext;
        _configurationKey = configurationKey;
        _defaultModelId = defaultModelId;
        _normalizeModelId = normalizeModelId ?? (static modelId => modelId);
        _statusTimer = new TimedStatusController(timeProvider);
        UtilityModels = new ObservableCollection<ProviderUtilityModelOption>(
            options.Select(option => new ProviderUtilityModelOption(option.ModelId, option.DisplayName)));
        if (UtilityModels.Count == 0 || UtilityModels.All(option => !ModelIdsEqual(option.ModelId, defaultModelId)))
        {
            throw new ArgumentException("Utility model options must contain the default model.", nameof(options));
        }

        _operation.PropertyChanged += OnOperationPropertyChanged;
        SelectedUtilityModel = ResolveOption(
            packageContext.Storage.State.GetValue(configurationKey)
            ?? packageContext.Configuration.GetValue(configurationKey));
    }

    public ObservableCollection<ProviderUtilityModelOption> UtilityModels { get; }

    public bool CanSaveUtilityModel => !_operation.IsBusy;

    public bool CanCancelUtilityModelSave => _operation.CanCancel;

    public bool HasOperationStatus => !string.IsNullOrWhiteSpace(_operation.Message);

    public string OperationStatus => _operation.Message;

    public bool IsOperationStatusSuccess => _operation.Severity == OperationSeverity.Success;

    public bool IsOperationStatusWarning => _operation.Severity == OperationSeverity.Warning;

    public bool IsOperationStatusError => _operation.Severity == OperationSeverity.Error;

    [ObservableProperty]
    private ProviderUtilityModelOption? _selectedUtilityModel;

    [RelayCommand]
    internal async Task SaveUtilityModelAsync()
    {
        if (_operation.IsBusy)
        {
            return;
        }

        _statusTimer.Cancel();
        var operation = _operation.Begin("Saving utility model...");
        try
        {
            var modelId = ResolveModelId(
                SelectedUtilityModel?.ModelId,
                _defaultModelId,
                UtilityModels.Select(option => option.ModelId));
            await _packageContext.Storage.State.SetValueAsync(
                _configurationKey,
                modelId,
                operation.CancellationToken);
            SelectedUtilityModel = ResolveOption(modelId);
            if (_operation.TryComplete(operation, "Utility model saved."))
            {
                _ = _statusTimer.ScheduleAsync(SuccessStatusDuration, _operation.ClearStatus);
            }
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            _operation.TryComplete(operation, "Utility model save canceled.", OperationSeverity.Warning);
        }
        catch (Exception ex)
        {
            _operation.TryComplete(operation, $"Utility model could not be saved: {ex.Message}", OperationSeverity.Error);
        }
    }

    [RelayCommand]
    private void CancelUtilityModelSave() => _operation.CancelCurrent("Utility model save canceled.");

    internal static string ResolveModelId(
        string? configuredModelId,
        string defaultModelId,
        IEnumerable<string> knownModelIds)
    {
        var configured = configuredModelId?.Trim();
        return !string.IsNullOrWhiteSpace(configured)
               && knownModelIds.Any(modelId => ModelIdsEqual(modelId, configured))
            ? configured
            : defaultModelId;
    }

    public void Dispose()
    {
        _operation.PropertyChanged -= OnOperationPropertyChanged;
        _operation.Dispose();
        _statusTimer.Dispose();
    }

    private ProviderUtilityModelOption ResolveOption(string? configuredModelId)
    {
        var modelId = ResolveModelId(
            _normalizeModelId(configuredModelId),
            _defaultModelId,
            UtilityModels.Select(option => option.ModelId));
        return UtilityModels.First(option => ModelIdsEqual(option.ModelId, modelId));
    }

    private static bool ModelIdsEqual(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void OnOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanSaveUtilityModel));
        OnPropertyChanged(nameof(CanCancelUtilityModelSave));
        OnPropertyChanged(nameof(HasOperationStatus));
        OnPropertyChanged(nameof(OperationStatus));
        OnPropertyChanged(nameof(IsOperationStatusSuccess));
        OnPropertyChanged(nameof(IsOperationStatusWarning));
        OnPropertyChanged(nameof(IsOperationStatusError));
    }
}
