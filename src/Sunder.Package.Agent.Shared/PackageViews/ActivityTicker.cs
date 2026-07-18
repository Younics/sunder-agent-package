using Avalonia.Threading;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal interface IActivityTicker
{
    event Action? Tick;
}

internal sealed class NullActivityTicker : IActivityTicker
{
    public static NullActivityTicker Instance { get; } = new();

    public event Action? Tick
    {
        add { }
        remove { }
    }
}

internal sealed class ActivityTicker : IActivityTicker, IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action? _tick;
    private bool _isEnabled = true;
    private bool _disposed;

    public ActivityTicker(TimeSpan? interval = null)
    {
        _timer = new DispatcherTimer
        {
            Interval = interval ?? TimeSpan.FromMilliseconds(420),
        };
        _timer.Tick += OnTimerTick;
    }

    public event Action? Tick
    {
        add
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _tick += value;
            UpdateTimerState();
        }
        remove
        {
            _tick -= value;
            UpdateTimerState();
        }
    }

    public void SetEnabled(bool isEnabled)
    {
        if (_disposed || _isEnabled == isEnabled)
        {
            return;
        }

        _isEnabled = isEnabled;
        UpdateTimerState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _tick = null;
    }

    private void UpdateTimerState()
    {
        if (!_disposed && _isEnabled && _tick is not null)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e) => _tick?.Invoke();
}
