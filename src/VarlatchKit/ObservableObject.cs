using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VarlatchKit;

/// <summary>Raises <see cref="PropertyChanged"/>, then <see cref="Changed"/>, when a property changes.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>After any change, for whatever shows all of it (the tray icon, the debug state).</summary>
    public event Action? Changed;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Announce(name);
        return true;
    }

    protected void Announce([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke();
    }
}
