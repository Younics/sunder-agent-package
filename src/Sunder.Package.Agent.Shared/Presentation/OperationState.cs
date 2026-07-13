using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sunder.Package.Agent.Shared.Presentation;

internal enum OperationSeverity
{
    None = 0,
    Info,
    Success,
    Warning,
    Error,
}

internal readonly record struct OperationGeneration(long Id, CancellationToken CancellationToken);

internal sealed class OperationState : INotifyPropertyChanged, IDisposable
{
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private bool _isBusy;
    private bool _canCancel;
    private string _message = string.Empty;
    private OperationSeverity _severity;
    private double? _progress;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get { lock (_syncRoot) { return _isBusy; } }
    }

    public bool CanCancel
    {
        get { lock (_syncRoot) { return _canCancel; } }
    }

    public string Message
    {
        get { lock (_syncRoot) { return _message; } }
    }

    public OperationSeverity Severity
    {
        get { lock (_syncRoot) { return _severity; } }
    }

    public double? Progress
    {
        get { lock (_syncRoot) { return _progress; } }
    }

    public bool IsIndeterminate => IsBusy && Progress is null;

    public OperationGeneration Begin(
        string message = "",
        OperationSeverity severity = OperationSeverity.Info,
        bool canCancel = true,
        double? progress = null)
    {
        CancellationTokenSource? previous;
        OperationGeneration generation;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _cancellation;
            _cancellation = new CancellationTokenSource();
            generation = new OperationGeneration(++_generation, _cancellation.Token);
            _isBusy = true;
            _canCancel = canCancel;
            _message = message;
            _severity = severity;
            _progress = NormalizeProgress(progress);
        }

        CancelAndDispose(previous);
        NotifyAll();
        return generation;
    }

    public bool TryReport(
        OperationGeneration generation,
        string? message = null,
        OperationSeverity? severity = null,
        double? progress = null)
    {
        lock (_syncRoot)
        {
            if (!IsCurrentGeneration(generation))
            {
                return false;
            }

            if (message is not null)
            {
                _message = message;
            }

            if (severity is not null)
            {
                _severity = severity.Value;
            }

            _progress = NormalizeProgress(progress);
        }

        NotifyAll();
        return true;
    }

    public bool IsCurrent(OperationGeneration generation)
    {
        lock (_syncRoot)
        {
            return IsCurrentGeneration(generation);
        }
    }

    public bool TryComplete(
        OperationGeneration generation,
        string message = "",
        OperationSeverity severity = OperationSeverity.Success)
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            if (!IsCurrentGeneration(generation))
            {
                return false;
            }

            cancellation = _cancellation;
            _cancellation = null;
            _isBusy = false;
            _canCancel = false;
            _message = message;
            _severity = string.IsNullOrWhiteSpace(message) ? OperationSeverity.None : severity;
            _progress = null;
        }

        cancellation?.Dispose();
        NotifyAll();
        return true;
    }

    public bool CancelCurrent(string message = "")
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            if (!_isBusy)
            {
                return false;
            }

            cancellation = _cancellation;
            _cancellation = null;
            _generation++;
            _isBusy = false;
            _canCancel = false;
            _message = message;
            _severity = string.IsNullOrWhiteSpace(message) ? OperationSeverity.None : OperationSeverity.Warning;
            _progress = null;
        }

        CancelAndDispose(cancellation);
        NotifyAll();
        return true;
    }

    public void ClearStatus()
    {
        lock (_syncRoot)
        {
            if (_disposed || _isBusy)
            {
                return;
            }

            _message = string.Empty;
            _severity = OperationSeverity.None;
        }

        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Severity));
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            _cancellation = null;
            _generation++;
            _isBusy = false;
            _canCancel = false;
            _progress = null;
        }

        CancelAndDispose(cancellation);
    }

    private bool IsCurrentGeneration(OperationGeneration generation)
        => !_disposed
           && _isBusy
           && generation.Id == _generation
           && !generation.CancellationToken.IsCancellationRequested;

    private static double? NormalizeProgress(double? progress)
        => progress is null ? null : Math.Clamp(progress.Value, 0, 100);

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class OperationState<TOperation> : INotifyPropertyChanged, IDisposable
    where TOperation : struct, Enum
{
    private readonly OperationState _state = new();
    private TOperation? _current;

    public OperationState()
    {
        _state.PropertyChanged += OnStatePropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TOperation? Current => _current;

    public bool IsBusy => _state.IsBusy;

    public bool CanCancel => _state.CanCancel;

    public string Message => _state.Message;

    public OperationSeverity Severity => _state.Severity;

    public double? Progress => _state.Progress;

    public OperationGeneration Begin(
        TOperation operation,
        string message = "",
        bool canCancel = true,
        double? progress = null)
    {
        _current = operation;
        OnPropertyChanged(nameof(Current));
        return _state.Begin(message, OperationSeverity.Info, canCancel, progress);
    }

    public bool TryComplete(
        OperationGeneration generation,
        string message = "",
        OperationSeverity severity = OperationSeverity.Success)
    {
        if (!_state.TryComplete(generation, message, severity))
        {
            return false;
        }

        _current = null;
        OnPropertyChanged(nameof(Current));
        return true;
    }

    public bool CancelCurrent(string message = "")
    {
        if (!_state.CancelCurrent(message))
        {
            return false;
        }

        _current = null;
        OnPropertyChanged(nameof(Current));
        return true;
    }

    public void ClearStatus() => _state.ClearStatus();

    public bool IsCurrent(OperationGeneration generation) => _state.IsCurrent(generation);

    public void Dispose()
    {
        _state.PropertyChanged -= OnStatePropertyChanged;
        _state.Dispose();
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        => PropertyChanged?.Invoke(this, e);

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
