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
    private bool _disposed;

    public ActivityTicker(TimeSpan? interval = null)
    {
        _timer = new DispatcherTimer
        {
            Interval = interval ?? TimeSpan.FromMilliseconds(420),
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public event Action? Tick;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        Tick = null;
    }

    private void OnTimerTick(object? sender, EventArgs e) => Tick?.Invoke();
}
