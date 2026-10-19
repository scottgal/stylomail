using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StyloMail.Desktop.Views.Controls;

/// <summary>
/// Maps a dashboard severity name to a state colour.
/// </summary>
/// <remarks>
/// The colour is redundant with the severity word beside it. That is deliberate: a warning card has
/// to remain readable without colour, and the colour names a state rather than a person.
/// </remarks>
public sealed class SeverityBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Critical = new(Color.Parse("#D7443E"));
    private static readonly SolidColorBrush Warning = new(Color.Parse("#E8A33D"));
    private static readonly SolidColorBrush Information = new(Color.Parse("#0A84FF"));
    private static readonly SolidColorBrush Good = new(Color.Parse("#34A853"));
    private static readonly SolidColorBrush Neutral = new(Color.Parse("#86868B"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string severity
            ? severity.ToLowerInvariant() switch
            {
                "critical" => Critical,
                "warning" => Warning,
                "info" => Information,
                "good" => Good,
                _ => Neutral,
            }
            : Neutral;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("A severity colour is read-only.");
}
