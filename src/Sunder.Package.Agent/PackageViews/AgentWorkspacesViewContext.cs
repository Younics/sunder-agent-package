using Avalonia.Threading;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

public sealed class AgentWorkspacesViewContext : IDisposable
{
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly PresentationTaskScope _tasks;
    private bool _disposed;

    public AgentWorkspacesViewContext()
        : this(PresentationDispatcher.Capture())
    {
    }

    internal AgentWorkspacesViewContext(IPresentationDispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
        _tasks = new PresentationTaskScope(exception => _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                Failed?.Invoke(exception);
            }
        }));
    }

    internal event Action<Exception>? Failed;

    internal string? PendingPathEditId { get; set; }

    internal string? PendingDocumentEditId { get; set; }

    internal void Run(Func<CancellationToken, Task> operation) => _tasks.Run(operation);

    internal void Queue(Action action, DispatcherPriority priority)
        => _tasks.Run(async cancellationToken =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    action();
                }
            }, priority);
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Failed = null;
        PendingPathEditId = null;
        PendingDocumentEditId = null;
        _tasks.Dispose();
    }
}
