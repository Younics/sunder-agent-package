using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.TestSupport;

public sealed class CapturingHistoryShellViewService : IPackageShellViewService
{
    public string? OpenedViewId { get; private set; }
    public IReadOnlyDictionary<string, string?>? Parameters { get; private set; }
    public IReadOnlyList<PackageHotbarView> ListHotbarViews() => [];
    public bool IsViewInHotbar(string viewId) => false;
    public ValueTask<bool> AddViewToDefaultHotbarAsync(string viewId, bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
    public ValueTask<bool> AddViewToHotbarAsync(string viewId, PackageViewPlacement placement, int? index = null,
        bool openPanel = false, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
    public ValueTask<bool> RemoveViewFromHotbarAsync(string viewId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
    public ValueTask<bool> OpenViewPanelAsync(string viewId, IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        OpenedViewId = viewId;
        Parameters = parameters;
        return ValueTask.FromResult(true);
    }
    public ValueTask<bool> CloseViewPanelAsync(string viewId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
}
