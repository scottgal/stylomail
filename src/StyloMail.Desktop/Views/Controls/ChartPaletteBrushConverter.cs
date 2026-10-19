using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views.Controls;

/// <summary>
/// Maps a legend's palette index to the same brush the chart control draws with.
/// </summary>
/// <remarks>
/// Both sides read <see cref="ChartPalette.Hex"/>, so a line and its key cannot disagree about colour.
/// </remarks>
public sealed class ChartPaletteBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int index)
        {
            return new SolidColorBrush(Color.Parse(ChartPalette.Hex[0]));
        }

        var safe = Math.Abs(index) % ChartPalette.Hex.Length;
        return new SolidColorBrush(Color.Parse(ChartPalette.Hex[safe]));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("A chart colour is read-only.");
}
