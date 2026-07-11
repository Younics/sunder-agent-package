using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed class EditableDocumentState<T> : INotifyPropertyChanged
{
    private readonly IEqualityComparer<T> _comparer;
    private T _cleanValue;
    private T _value;

    public EditableDocumentState(T value, IEqualityComparer<T>? comparer = null)
    {
        _value = value;
        _cleanValue = value;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public T Value
    {
        get => _value;
        set
        {
            var wasDirty = IsDirty;
            if (_comparer.Equals(_value, value))
            {
                return;
            }

            _value = value;
            OnPropertyChanged();
            if (wasDirty != IsDirty)
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool IsDirty => !_comparer.Equals(_value, _cleanValue);

    public void MarkClean()
    {
        var wasDirty = IsDirty;
        _cleanValue = _value;
        if (wasDirty)
        {
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public void Revert()
    {
        if (!IsDirty)
        {
            return;
        }

        _value = _cleanValue;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsDirty));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
