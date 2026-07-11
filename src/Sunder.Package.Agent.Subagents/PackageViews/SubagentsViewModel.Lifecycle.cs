namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubagentsViewModel
{
    public Task InitializeAsync() => _initialization;

    private async Task InitializeCoreAsync()
    {
        try
        {
            await ReloadAsync(null);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    SetStatus(ex.Message, SubagentStatusKind.Error);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadVersion++;
        _statusClear.Dispose();
        ChatBinding.PropertyChanged -= OnModelBindingPropertyChanged;
        ChatBinding.Changed -= OnEditorSelectionChanged;
        Capabilities.Changed -= OnCapabilitiesChanged;
        if (_subagentService is not null)
        {
            _subagentService.SubagentsChanged -= OnSubagentsChanged;
        }

        if (_capabilityCatalog is not null)
        {
            _capabilityCatalog.Changed -= OnSelectableCapabilitiesChanged;
            _capabilityCatalog.Dispose();
        }

        ChatBinding.Dispose();
    }
}
