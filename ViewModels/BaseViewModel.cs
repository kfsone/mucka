using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mucka.ViewModels;

public abstract class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Raises PropertyChanged for each name in <paramref name="names"/>.
    /// Use when multiple derived properties must be notified after a backing field changes.
    /// </summary>
    protected void OnPropertiesChanged(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>
    /// Sets the backing field and, if it changed, raises PropertyChanged for the property
    /// itself plus each additional dependent property in <paramref name="also"/>.
    /// </summary>
    protected bool SetAndNotify<T>(ref T field, T value, string[] also, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        foreach (var dep in also)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(dep));
        return true;
    }
}
