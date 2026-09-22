using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Views.Controls;

/// <summary>
/// Maps a <see cref="HostStatusKind"/> to the dot in the status bar.
/// </summary>
/// <remarks>
/// A converter rather than a brush on the model, so <c>Models</c> stays free of
/// Avalonia. That is not tidiness for its own sake: <see cref="HostStatus"/> is
/// covered by tests that run with no display, and the moment it returns an
/// <see cref="IBrush"/> those tests need a rendering platform to pass.
/// </remarks>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Neutral = new(Color.Parse("#86868B"));
    private static readonly SolidColorBrush Good = new(Color.Parse("#34A853"));
    private static readonly SolidColorBrush Warning = new(Color.Parse("#E8A33D"));
    private static readonly SolidColorBrush Danger = new(Color.Parse("#D7443E"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is HostStatusKind kind
            ? kind switch
            {
                HostStatusKind.Ready => Good,

                // Not ready is a warning and not a failure: the Host is up and
                // is deliberately refusing mail, which is a state it means to
                // be in.
                HostStatusKind.NotReady => Warning,

                // Nothing has been asked yet. Neutral rather than red, because
                // a console that has not looked has discovered nothing.
                HostStatusKind.Unknown => Neutral,

                _ => Danger,
            }
            : Neutral;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("The status dot is read-only.");
}
