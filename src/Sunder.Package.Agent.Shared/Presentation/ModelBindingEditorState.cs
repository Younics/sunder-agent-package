using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.Presentation;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class ModelBindingEditorState : INotifyPropertyChanged, IDisposable
{
    private readonly ProviderModelLoader _loader;
    private readonly ModelBindingEditorOptions _options;
    private readonly IPresentationDispatcher _dispatcher;
    private readonly PresentationTaskScope _tasks;
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
    private string? _invalidSettingsJson;
    private string? _removedReasoningVariantId;
    private string? _catalogWarning;

    public ModelBindingEditorState(
        ProviderModelLoader loader,
        ModelBindingEditorOptions options,
        IPresentationDispatcher? dispatcher = null)
    {
        _loader = loader;
        _options = options;
        _dispatcher = dispatcher ?? PresentationDispatcher.Capture();
        _tasks = new PresentationTaskScope((exception, token) => _dispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                StatusText = $"{_options.LoadFailurePrefix}: Failed - {exception.Message}";
                SetWarning($"{_options.LoadFailurePrefix} could not be loaded: {exception.Message}");
            }
        }, token), _dispatcher.ShutdownToken);
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

            var previousSettings = _invalidSettingsJson is null ? SettingsJson : null;
            _selectedModel = value;
            OnPropertyChanged();
            ApplyModelOptions(value, _suppressSelectionChanges ? null : previousSettings);
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
            if (!_suppressSelectionChanges)
            {
                _invalidSettingsJson = null;
                _removedReasoningVariantId = null;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsJson));
            RefreshSelectionWarning();
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
            RefreshSelectionWarning();
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
            if (!_suppressSelectionChanges) _invalidSettingsJson = null;
            if (!_suppressSelectionChanges && value?.DisablesReasoning == true && SelectedReasoningOption?.VariantId is not null)
            {
                SetSelectionSilently(() => SelectedReasoningOption = ReasoningOptions.FirstOrDefault());
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReasoningSelectionEnabled));
            OnPropertyChanged(nameof(SettingsJson));
            RefreshSelectionWarning();
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

    public bool ShowModelSelection => HasSelectedProvider && (!HasWarning || Models.Count > 0);

    public bool HasReasoningOptions => ReasoningOptions.Count > 0;

    public bool HasSpeedOptions => SpeedOptions.Count > 0;

    public bool HasModeOptions => ModeOptions.Count > 0;

    public bool ShowReasoningOptions => ShowModelSelection && HasReasoningOptions;

    public bool ShowSpeedOptions => false; // Speed is selected with the model, not through a competing control.

    public bool ShowModeOptions => ShowModelSelection && HasModeOptions;

    public bool IsReasoningSelectionEnabled => SelectedModeOption?.DisablesReasoning != true;

    public bool CanOpenProviderSettings =>
        ShowProviderWarning && !string.IsNullOrWhiteSpace(SelectedProvider?.PackageId);

    public string? SettingsJson => _invalidSettingsJson ?? AgentChatModelSettingsJson.Serialize(new AgentChatModelSettings(
        _removedReasoningVariantId ?? (HasReasoningOptions ? SelectedReasoningOption?.VariantId : null),
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
        IReadOnlyList<ProviderCatalogOption> providers;
        string? catalogFailure = null;
        try { providers = await _loader.ListProvidersAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            providers = [];
            catalogFailure = $"{_options.LoadFailurePrefix} could not be loaded: {exception.Message}";
        }
        if (!string.IsNullOrWhiteSpace(selection.ProviderId) && !providers.Any(provider => string.Equals(provider.Id, selection.ProviderId, StringComparison.OrdinalIgnoreCase)))
            providers = providers.Append(new ProviderCatalogOption(selection.ProviderId, selection.ProviderId + " (unavailable)") { IsAvailable = false }).ToArray();
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
            if (catalogFailure is not null)
            {
                _loader.Cancel();
                _loadGeneration++;
                SetSelectionSilently(() => PreserveUnavailableSelection(selection.ModelId, selection.SettingsJson));
                StatusText = catalogFailure;
                _catalogWarning = catalogFailure;
                SetWarning(catalogFailure);
                IsLoading = false;
            }
        }).ConfigureAwait(false);

        if (_disposed || catalogFailure is not null)
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
            SetSelectionSilently(() => PreserveUnavailableSelection(selectedModelId, settingsJson));
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

                SetSelectionSilently(() =>
                {
                    Models.Clear();
                    _catalogWarning = result.WarningText;
                    var settings = AgentChatModelSettingsJson.Parse(settingsJson);
                    foreach (var model in result.Models.SelectMany(ExpandModelChoices))
                    {
                        Models.Add(model);
                    }

                    if (!string.IsNullOrWhiteSpace(selectedModelId) && !Models.Any(model => string.Equals(model.Id, selectedModelId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(model.SpeedOptionId, settings.SpeedOptionId, StringComparison.OrdinalIgnoreCase)))
                        Models.Add(new ProviderModelCatalogOption(selectedModelId, selectedModelId + " (unavailable)")
                        { SpeedOptionId = settings.SpeedOptionId, IsAvailable = false });

                    SelectedModel = Models.FirstOrDefault(model => string.Equals(
                            model.Id,
                            selectedModelId,
                            StringComparison.OrdinalIgnoreCase) && string.Equals(model.SpeedOptionId, settings.SpeedOptionId, StringComparison.OrdinalIgnoreCase))
                        ?? Models.FirstOrDefault();
                    ApplyModelOptions(SelectedModel, settingsJson);
                    StatusText = result.StatusText;
                    RefreshSelectionWarning();
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
                    SetSelectionSilently(() => PreserveUnavailableSelection(selectedModelId, settingsJson));
                    StatusText = $"{_options.LoadFailurePrefix}: Failed - {ex.Message}";
                    _catalogWarning = $"{_options.LoadFailurePrefix} could not be loaded: {ex.Message}";
                    SetWarning(_catalogWarning);
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
        var valid = AgentChatModelSettingsJson.TryParse(settingsJson, out var settings);
        _invalidSettingsJson = valid ? null : settingsJson;
        // Keep old explicit None values outside the menu until the user chooses
        // their replacement. A refresh or speed change must not become Default.
        _removedReasoningVariantId = IsRemovedReasoningVariant(settings.ReasoningVariantId) ? settings.ReasoningVariantId : null;
        SetSelectionSilently(() =>
        {
            ReasoningOptions.Clear();
            SpeedOptions.Clear();
            ModeOptions.Clear();

            if (_options.IncludeSpeedInModelPicker && model is not null)
            {
                ReasoningOptions.Add(new ModelReasoningOption(
                    null,
                    "Default",
                    model.DefaultReasoningVariantId is { } defaultId ? $"Provider default: {defaultId}. No explicit effort is sent." : _options.DefaultReasoningDescription));
                foreach (var id in new[] { "low", "medium", "high", "xhigh", "max" })
                {
                    var variant = model.Variants?.FirstOrDefault(item => string.Equals(item.VariantId, id, StringComparison.OrdinalIgnoreCase));
                    ReasoningOptions.Add(new ModelReasoningOption(
                        id, id == "xhigh" ? "Extra high" : char.ToUpperInvariant(id[0]) + id[1..],
                        variant?.Description ?? "Not supported by this model and connection.", variant is not null));
                }
                foreach (var variant in (model.Variants ?? []).Where(item => !IsRemovedReasoningVariant(item.VariantId)
                    && item.VariantId.ToLowerInvariant() is not ("low" or "medium" or "high" or "xhigh" or "max")))
                    ReasoningOptions.Add(new ModelReasoningOption(variant.VariantId, variant.DisplayName, variant.Description));
                if (!string.IsNullOrWhiteSpace(settings.ReasoningVariantId) && _removedReasoningVariantId is null
                    && !ReasoningOptions.Any(item => string.Equals(item.VariantId, settings.ReasoningVariantId, StringComparison.OrdinalIgnoreCase)))
                    ReasoningOptions.Add(new ModelReasoningOption(settings.ReasoningVariantId, settings.ReasoningVariantId + " (unavailable)", "Choose a supported reasoning setting.", false));
            }

            if (model?.SpeedOptions?.Count > 0)
            {
                SpeedOptions.Add(new ModelSpeedOption(null, "Normal", "Use standard processing without changing reasoning effort."));
                foreach (var option in model.SpeedOptions.Where(item => !string.IsNullOrWhiteSpace(item.SpeedOptionId)))
                {
                    SpeedOptions.Add(new ModelSpeedOption(
                        option.SpeedOptionId,
                        option.DisplayName,
                        option.Description));
                }
            }
            if (!string.IsNullOrWhiteSpace(model?.SpeedOptionId) && !SpeedOptions.Any(item => item.SpeedOptionId == model.SpeedOptionId))
                SpeedOptions.Add(new ModelSpeedOption(model.SpeedOptionId, model.SpeedOptionId + " (unavailable)", "Choose a supported model/speed combination.", false));

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
            if (!string.IsNullOrWhiteSpace(settings.ModeOptionId) && !ModeOptions.Any(item => string.Equals(item.ModeOptionId, settings.ModeOptionId, StringComparison.OrdinalIgnoreCase)))
            {
                if (ModeOptions.Count == 0) ModeOptions.Add(new ModelModeOption(null, "Default", "Use the provider's default execution mode."));
                ModeOptions.Add(new ModelModeOption(settings.ModeOptionId, settings.ModeOptionId + " (unavailable)", "Choose a supported operating mode.", IsAvailable: false));
            }

            SelectedReasoningOption = _removedReasoningVariantId is not null ? null : FindOption(
                ReasoningOptions,
                settings.ReasoningVariantId,
                option => option.VariantId);
            SelectedSpeedOption = FindOption(
                SpeedOptions,
                model?.SpeedOptionId,
                option => option.SpeedOptionId);
            SelectedModeOption = FindOption(
                ModeOptions,
                settings.ModeOptionId,
                option => option.ModeOptionId);
        });
        RefreshSelectionWarning();
    }

    private void PreserveUnavailableSelection(string? modelId, string? settingsJson)
    {
        ClearModelsAndOptions();
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var settings = AgentChatModelSettingsJson.Parse(settingsJson);
            var model = new ProviderModelCatalogOption(modelId, modelId + " (unavailable)")
            { SpeedOptionId = settings.SpeedOptionId, IsAvailable = false };
            Models.Add(model);
            SelectedModel = model;
        }
        ApplyModelOptions(SelectedModel, settingsJson);
    }

    private IEnumerable<ProviderModelCatalogOption> ExpandModelChoices(ProviderModelCatalogOption model)
    {
        if (!_options.IncludeSpeedInModelPicker) { yield return model; yield break; }
        yield return model with { SpeedOptionId = null };
        foreach (var speed in model.SpeedOptions ?? [])
            yield return model with { Label = model.Label + " · " + speed.DisplayName, SpeedOptionId = speed.SpeedOptionId, Description = speed.Description };
    }

    private void RefreshSelectionWarning()
    {
        var invalid = _invalidSettingsJson is not null ? "The saved model settings are invalid. Select a model and repair its options."
            : SelectedProvider?.IsAvailable == false || SelectedModel?.IsAvailable == false ? "The saved model or connection is unavailable. Select a supported model."
            : _removedReasoningVariantId is not null ? "The saved None reasoning option has been preserved. Choose Default or another supported reasoning setting before running."
            : SelectedReasoningOption?.IsAvailable == false || SelectedSpeedOption?.IsAvailable == false || SelectedModeOption?.IsAvailable == false
                ? "A saved model option is unavailable. It has been preserved; choose a supported option before running."
                : SelectedModeOption?.DisablesReasoning == true && SelectedReasoningOption?.VariantId is not null
                    ? "The saved operating mode cannot use explicit reasoning. Choose Default reasoning or another mode." : _catalogWarning;
        SetWarning(invalid);
        OnPropertyChanged(nameof(ShowModelSelection));
    }

    private static bool IsRemovedReasoningVariant(string? id) => string.Equals(id, "none", StringComparison.OrdinalIgnoreCase);

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
        _invalidSettingsJson = null;
        _removedReasoningVariantId = null;
        _catalogWarning = null;
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

}
