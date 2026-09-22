using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StyloMail.Desktop.Models;

/// <summary>
/// The minimum a bound object needs, written out rather than pulled in.
/// </summary>
/// <remarks>
/// A source-generator package would save these twenty lines and add a build
/// dependency and an analyzer to a project whose whole value is being small and
/// legible. mylo takes the same line, with its own RelayCommand rather than a
/// toolkit.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(propertyName);
        return true;
    }
}
