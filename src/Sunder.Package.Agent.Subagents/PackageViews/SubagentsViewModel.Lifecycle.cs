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
        _tasks.Dispose();
        _operation.Dispose();
        ChatBinding.PropertyChanged -= OnModelBindingPropertyChanged;
        ChatBinding.Changed -= OnEditorSelectionChanged;
        Capabilities.Changed -= OnCapabilitiesChanged;
        if (_gateway is not null)
        {
            _gateway.SubagentsChanged -= OnSubagentsChanged;
            _gateway.CatalogChanged -= OnSelectableCapabilitiesChanged;
        }

        ChatBinding.Dispose();
    }
}
