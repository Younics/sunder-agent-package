using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed record ModelBindingSelection(
    string? ProviderId,
    string? ModelId,
    string? SettingsJson = null);

internal sealed record ModelBindingEditorOptions(
    bool SelectFirstProvider,
    string NoProvidersText,
    string NoProviderSelectedText,
    string LoadingText,
    string LoadFailurePrefix,
    string? EmptyProviderLabel = null,
    string DefaultReasoningDescription = "Use the selected model's default reasoning behavior.");

internal sealed record ModelReasoningOption(
    string? VariantId,
    string Label,
    string? Description = null)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

internal sealed record ModelSpeedOption(
    string? SpeedOptionId,
    string Label,
    string? Description = null)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

internal sealed record ModelModeOption(
    string? ModeOptionId,
    string Label,
    string? Description = null,
    bool DisablesReasoning = false)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

internal sealed class ModelBindingEditorState : INotifyPropertyChanged, IDisposable
{
    private readonly ProviderModelLoader _loader;
    private readonly ModelBindingEditorOptions _options;
    private readonly IPresentationDispatcher _dispatcher;
    private readonly PresentationTaskScope _tasks = new();
    private ProviderCatalogOption? _selectedProvider;
    private ProviderModelCatalogOption? _selectedModel;
    private ModelReasoningOption? _selectedReasoningOption;
    private ModelSpeedOption? _selectedSpeedOption;
    private ModelModeOption? _selectedModeOption;
    private string _statusText;
    private string _warningText = string.Empty;
    private bool _hasProviders;
    private bool _hasWarning;
    private bool _isLoading;
    private bool _suppressSelectionChanges;
    private bool _disposed;
    private int _loadGeneration;

    public ModelBindingEditorState(
        ProviderModelLoader loader,
        ModelBindingEditorOptions options,
        IPresentationDispatcher? dispatcher = null)
    {
        _loader = loader;
        _options = options;
        _dispatcher = dispatcher ?? PresentationDispatcher.Capture();
        _statusText = options.NoProviderSelectedText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Changed;

    public ObservableCollection<ProviderCatalogOption> Providers { get; } = [];

    public ObservableCollection<ProviderModelCatalogOption> Models { get; } = [];

    public ObservableCollection<ModelReasoningOption> ReasoningOptions { get; } = [];

    public ObservableCollection<ModelSpeedOption> SpeedOptions { get; } = [];

    public ObservableCollection<ModelModeOption> ModeOptions { get; } = [];

    public ProviderCatalogOption? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (Equals(_selectedProvider, value))
            {
                return;
            }

            _selectedProvider = value;
            NotifyProviderStateChanged();
            if (_suppressSelectionChanges)
            {
                return;
            }

            Changed?.Invoke();
            _tasks.Run(_ => LoadProviderAsync(
                value?.Id,
                selectedModelId: null,
                settingsJson: null,
                notifyChanged: true));
        }
    }

    public ProviderModelCatalogOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (Equals(_selectedModel, value))
            {
                return;
            }

            _selectedModel = value;
            OnPropertyChanged();
            ApplyModelOptions(value, settingsJson: null);
            NotifyOptionStateChanged();
            if (!_suppressSelectionChanges)
            {
                Changed?.Invoke();
            }
        }
    }

    public ModelReasoningOption? SelectedReasoningOption
    {
        get => _selectedReasoningOption;
        set
        {
            if (Equals(_selectedReasoningOption, value))
            {
                return;
            }

            _selectedReasoningOption = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsJson));
            if (!_suppressSelectionChanges)
            {
                Changed?.Invoke();
            }
        }
    }

    public ModelSpeedOption? SelectedSpeedOption
    {
        get => _selectedSpeedOption;
        set
        {
            if (Equals(_selectedSpeedOption, value))
            {
                return;
            }

            _selectedSpeedOption = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsJson));
            if (!_suppressSelectionChanges)
            {
                Changed?.Invoke();
            }
        }
    }

    public ModelModeOption? SelectedModeOption
    {
        get => _selectedModeOption;
        set
        {
            if (Equals(_selectedModeOption, value))
            {
                return;
            }

            _selectedModeOption = value;
            if (value?.DisablesReasoning == true && SelectedReasoningOption?.VariantId is not null)
            {
                SetSelectionSilently(() => SelectedReasoningOption = ReasoningOptions.FirstOrDefault());
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReasoningSelectionEnabled));
            OnPropertyChanged(nameof(SettingsJson));
            if (!_suppressSelectionChanges)
            {
                Changed?.Invoke();
            }
        }
    }

    public bool HasProviders
    {
        get => _hasProviders;
        private set
        {
            if (_hasProviders == value)
            {
                return;
            }

            _hasProviders = value;
            NotifyProviderStateChanged();
        }
    }

    public bool HasWarning
    {
        get => _hasWarning;
        private set
        {
            if (_hasWarning == value)
            {
                return;
            }

            _hasWarning = value;
            NotifyProviderStateChanged();
        }
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (_warningText == value)
            {
                return;
            }

            _warningText = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
        }
    }

    public bool HasNoProviders => !HasProviders;

    public bool HasSelectedProvider => !string.IsNullOrWhiteSpace(SelectedProvider?.Id);

    public bool ShowProviderPicker => HasProviders;

    public bool ShowProviderWarning => HasWarning;

    public bool ShowModelSelection => HasSelectedProvider && !HasWarning;

    public bool HasReasoningOptions => ReasoningOptions.Count > 0;

    public bool HasSpeedOptions => SpeedOptions.Count > 0;

    public bool HasModeOptions => ModeOptions.Count > 0;

    public bool ShowReasoningOptions => ShowModelSelection && HasReasoningOptions;

    public bool ShowSpeedOptions => ShowModelSelection && HasSpeedOptions;

    public bool ShowModeOptions => ShowModelSelection && HasModeOptions;

    public bool IsReasoningSelectionEnabled => SelectedModeOption?.DisablesReasoning != true;

    public bool CanOpenProviderSettings =>
        ShowProviderWarning && !string.IsNullOrWhiteSpace(SelectedProvider?.PackageId);

    public string? SettingsJson => AgentChatModelSettingsJson.Serialize(new AgentChatModelSettings(
        HasReasoningOptions ? SelectedReasoningOption?.VariantId : null,
        HasSpeedOptions ? SelectedSpeedOption?.SpeedOptionId : null,
        HasModeOptions ? SelectedModeOption?.ModeOptionId : null));

    public ModelBindingSelection Selection => new(
        SelectedProvider?.Id,
        SelectedModel?.Id,
        SettingsJson);

    internal void SetWarningState(bool hasWarning, string? warningText = null)
    {
        WarningText = warningText?.Trim() ?? WarningText;
        HasWarning = hasWarning;
    }

    public async Task RefreshAsync(
        ModelBindingSelection selection,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var providers = await _loader.ListProvidersAsync(cancellationToken).ConfigureAwait(false);
        var selectedProviderId = ResolveProviderId(providers, selection.ProviderId);
        string? providerId = null;
        var hasProviders = false;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            SetSelectionSilently(() =>
            {
                Providers.Clear();
                HasProviders = providers.Count > 0;
                if (HasProviders && !string.IsNullOrWhiteSpace(_options.EmptyProviderLabel))
                {
                    Providers.Add(new ProviderCatalogOption(null, _options.EmptyProviderLabel));
                }

                foreach (var provider in providers)
                {
                    Providers.Add(provider);
                }

                SelectedProvider = Providers.FirstOrDefault(provider => string.Equals(
                        provider.Id,
                        selectedProviderId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? Providers.FirstOrDefault();
            });
            hasProviders = HasProviders;
            providerId = SelectedProvider?.Id;
        }).ConfigureAwait(false);

        if (_disposed)
        {
            return;
        }

        if (!hasProviders)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    ClearLoadedProvider(_options.NoProvidersText);
                }
            }).ConfigureAwait(false);
            return;
        }

        await LoadProviderAsync(
            providerId,
            selection.ModelId,
            selection.SettingsJson,
            notifyChanged: false,
            cancellationToken).ConfigureAwait(false);
    }

    public void Clear()
    {
        _loader.Cancel();
        _loadGeneration++;
        SetSelectionSilently(() =>
        {
            Providers.Clear();
            HasProviders = false;
            SelectedProvider = null;
            ClearModelsAndOptions();
            ClearWarning();
            StatusText = _options.NoProviderSelectedText;
        });
        IsLoading = false;
    }

    private async Task LoadProviderAsync(
        string? providerId,
        string? selectedModelId,
        string? settingsJson,
        bool notifyChanged,
        CancellationToken cancellationToken = default)
    {
        var generation = 0;
        var shouldLoad = false;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            generation = ++_loadGeneration;
            SetSelectionSilently(ClearModelsAndOptions);
            if (string.IsNullOrWhiteSpace(providerId))
            {
                _loader.Cancel();
                ClearWarning();
                StatusText = _options.NoProviderSelectedText;
                IsLoading = false;
                NotifyProviderStateChanged();
                if (notifyChanged)
                {
                    Changed?.Invoke();
                }

                return;
            }

            IsLoading = true;
            StatusText = _options.LoadingText;
            ClearWarning();
            shouldLoad = true;
        }).ConfigureAwait(false);

        if (!shouldLoad)
        {
            return;
        }

        var activeProviderId = providerId!;
        try
        {
            var result = await _loader.LoadAsync(activeProviderId, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || generation != _loadGeneration || !IsSelectedProvider(activeProviderId))
                {
                    return;
                }

                var settings = AgentChatModelSettingsJson.Parse(settingsJson);
                var legacySelection = ResolveLegacyFastSelection(
                    selectedModelId,
                    settings.SpeedOptionId,
                    result.Models);
                settings = settings with { SpeedOptionId = legacySelection.SpeedOptionId };
                SetSelectionSilently(() =>
                {
                    Models.Clear();
                    foreach (var model in result.Models)
                    {
                        Models.Add(model);
                    }

                    SelectedModel = Models.FirstOrDefault(model => string.Equals(
                            model.Id,
                            legacySelection.ModelId,
                            StringComparison.OrdinalIgnoreCase))
                        ?? Models.FirstOrDefault();
                    ApplyModelOptions(SelectedModel, AgentChatModelSettingsJson.Serialize(settings));
                    StatusText = result.StatusText;
                    SetWarning(result.WarningText);
                });
                NotifyOptionStateChanged();
                if (notifyChanged)
                {
                    Changed?.Invoke();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed && generation == _loadGeneration && IsSelectedProvider(activeProviderId))
                {
                    SetSelectionSilently(ClearModelsAndOptions);
                    StatusText = $"{_options.LoadFailurePrefix}: Failed - {ex.Message}";
                    SetWarning($"{_options.LoadFailurePrefix} could not be loaded: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed && generation == _loadGeneration)
                {
                    IsLoading = false;
                }
            }).ConfigureAwait(false);
        }
    }

    private void ApplyModelOptions(ProviderModelCatalogOption? model, string? settingsJson)
    {
        var settings = AgentChatModelSettingsJson.Parse(settingsJson);
        SetSelectionSilently(() =>
        {
            ReasoningOptions.Clear();
            SpeedOptions.Clear();
            ModeOptions.Clear();

            if (model?.Variants?.Count > 0)
            {
                ReasoningOptions.Add(new ModelReasoningOption(
                    null,
                    "Default",
                    _options.DefaultReasoningDescription));
                foreach (var variant in model.Variants.Where(item => !string.IsNullOrWhiteSpace(item.VariantId)))
                {
                    ReasoningOptions.Add(new ModelReasoningOption(
                        variant.VariantId,
                        variant.DisplayName,
                        variant.Description));
                }
            }

            if (model?.SpeedOptions?.Count > 0)
            {
                SpeedOptions.Add(new ModelSpeedOption(null, "Default", "Use the provider's default speed."));
                foreach (var option in model.SpeedOptions.Where(item => !string.IsNullOrWhiteSpace(item.SpeedOptionId)))
                {
                    SpeedOptions.Add(new ModelSpeedOption(
                        option.SpeedOptionId,
                        option.DisplayName,
                        option.Description));
                }
            }

            if (model?.ModeOptions?.Count > 0)
            {
                ModeOptions.Add(new ModelModeOption(
                    null,
                    "Default",
                    "Use the provider's default execution mode."));
                foreach (var option in model.ModeOptions.Where(item => !string.IsNullOrWhiteSpace(item.ModeOptionId)))
                {
                    ModeOptions.Add(new ModelModeOption(
                        option.ModeOptionId,
                        option.DisplayName,
                        option.Description,
                        option.DisablesReasoning));
                }
            }

            SelectedReasoningOption = FindOption(
                ReasoningOptions,
                settings.ReasoningVariantId,
                option => option.VariantId);
            SelectedSpeedOption = FindOption(
                SpeedOptions,
                settings.SpeedOptionId,
                option => option.SpeedOptionId);
            SelectedModeOption = FindOption(
                ModeOptions,
                settings.ModeOptionId,
                option => option.ModeOptionId);
            if (SelectedModeOption?.DisablesReasoning == true)
            {
                SelectedReasoningOption = ReasoningOptions.FirstOrDefault();
            }
        });
    }

    private string? ResolveProviderId(
        IReadOnlyList<ProviderCatalogOption> providers,
        string? selectedProviderId)
    {
        if (!string.IsNullOrWhiteSpace(selectedProviderId)
            && providers.Any(provider => string.Equals(
                provider.Id,
                selectedProviderId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return selectedProviderId;
        }

        return _options.SelectFirstProvider ? providers.FirstOrDefault()?.Id : null;
    }

    private void ClearLoadedProvider(string statusText)
    {
        SetSelectionSilently(() =>
        {
            SelectedProvider = null;
            ClearModelsAndOptions();
            ClearWarning();
            StatusText = statusText;
        });
        IsLoading = false;
        NotifyProviderStateChanged();
    }

    private void ClearModelsAndOptions()
    {
        Models.Clear();
        ReasoningOptions.Clear();
        SpeedOptions.Clear();
        ModeOptions.Clear();
        _selectedModel = null;
        _selectedReasoningOption = null;
        _selectedSpeedOption = null;
        _selectedModeOption = null;
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedReasoningOption));
        OnPropertyChanged(nameof(SelectedSpeedOption));
        OnPropertyChanged(nameof(SelectedModeOption));
        NotifyOptionStateChanged();
    }

    private void SetWarning(string? warningText)
    {
        WarningText = warningText?.Trim() ?? string.Empty;
        HasWarning = !string.IsNullOrWhiteSpace(WarningText);
    }

    private void ClearWarning() => SetWarning(null);

    private bool IsSelectedProvider(string providerId) => string.Equals(
        SelectedProvider?.Id,
        providerId,
        StringComparison.OrdinalIgnoreCase);

    private void NotifyProviderStateChanged()
    {
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(HasProviders));
        OnPropertyChanged(nameof(HasNoProviders));
        OnPropertyChanged(nameof(HasSelectedProvider));
        OnPropertyChanged(nameof(ShowProviderPicker));
        OnPropertyChanged(nameof(ShowProviderWarning));
        OnPropertyChanged(nameof(ShowModelSelection));
        OnPropertyChanged(nameof(ShowReasoningOptions));
        OnPropertyChanged(nameof(ShowSpeedOptions));
        OnPropertyChanged(nameof(ShowModeOptions));
        OnPropertyChanged(nameof(CanOpenProviderSettings));
    }

    private void NotifyOptionStateChanged()
    {
        OnPropertyChanged(nameof(HasReasoningOptions));
        OnPropertyChanged(nameof(HasSpeedOptions));
        OnPropertyChanged(nameof(HasModeOptions));
        OnPropertyChanged(nameof(ShowReasoningOptions));
        OnPropertyChanged(nameof(ShowSpeedOptions));
        OnPropertyChanged(nameof(ShowModeOptions));
        OnPropertyChanged(nameof(IsReasoningSelectionEnabled));
        OnPropertyChanged(nameof(SettingsJson));
        OnPropertyChanged(nameof(Selection));
    }

    private void SetSelectionSilently(Action action)
    {
        var wasSuppressed = _suppressSelectionChanges;
        _suppressSelectionChanges = true;
        try
        {
            action();
        }
        finally
        {
            _suppressSelectionChanges = wasSuppressed;
        }
    }

    private static TOption? FindOption<TOption>(
        IEnumerable<TOption> options,
        string? selectedId,
        Func<TOption, string?> getId)
        where TOption : class
        => options.FirstOrDefault(option => !string.IsNullOrWhiteSpace(selectedId)
                && string.Equals(getId(option), selectedId, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault();

    private static LegacyModelSelection ResolveLegacyFastSelection(
        string? modelId,
        string? speedOptionId,
        IReadOnlyList<ProviderModelCatalogOption> availableModels)
    {
        const string fastSuffix = "-fast";
        if (!string.IsNullOrWhiteSpace(modelId)
            && modelId.EndsWith(fastSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var baseModelId = modelId[..^fastSuffix.Length];
            if (availableModels.Any(model => string.Equals(
                model.Id,
                baseModelId,
                StringComparison.OrdinalIgnoreCase)))
            {
                return new LegacyModelSelection(baseModelId, speedOptionId ?? "fast");
            }
        }

        return new LegacyModelSelection(modelId, speedOptionId);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadGeneration++;
        _tasks.Dispose();
        _loader.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record LegacyModelSelection(string? ModelId, string? SpeedOptionId);
}
