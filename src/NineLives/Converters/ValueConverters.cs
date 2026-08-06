using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Blackcat.NineLives.Converters;

/// <summary>
/// Renders a tag name as a GitHub-style pill brush.
///
/// GitHub's LIGHT theme fills a label with the raw swatch and picks black or white text. Against
/// this app's dark background a solid #B60205 is punishing to look at, so this follows GitHub's
/// DARK theme treatment instead: a low-alpha fill of the colour, a coloured border, and a
/// lightened version of the colour as the text. Same palette, appropriate rendering.
///
/// ConverterParameter selects which part: "fill", "border", or text (the default).
/// </summary>
public class TagBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = Services.TagPalette.ColorFor(value as string);
        var baseColor = (Color)ColorConverter.ConvertFromString(hex);

        return (parameter as string)?.ToLowerInvariant() switch
        {
            "fill" => new SolidColorBrush(Color.FromArgb(0x33, baseColor.R, baseColor.G, baseColor.B)),
            "border" => new SolidColorBrush(Color.FromArgb(0x99, baseColor.R, baseColor.G, baseColor.B)),
            _ => new SolidColorBrush(Lighten(baseColor, 0.55))
        };
    }

    /// <summary>
    /// Mixes toward white so the text clears a sensible contrast floor on a dark background. The
    /// darker swatches (#0052CC, #5319E7) are close to unreadable at full saturation here.
    /// </summary>
    private static Color Lighten(Color c, double amount)
        => Color.FromRgb(
            (byte)(c.R + (255 - c.R) * amount),
            (byte)(c.G + (255 - c.G) * amount),
            (byte)(c.B + (255 - c.B) * amount));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Turns the comma-separated tag text being typed into the parsed list, so the edit form can
/// show a live preview of the actual pills. Uses the same parser as save, so what is previewed
/// is exactly what is stored.
/// </summary>
public class TagPreviewConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Services.TagPalette.ParseTags(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "Invert";
        bool flag = value is bool b && b;
        if (invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "Invert";
        bool isNull = value is null || (value is string s && string.IsNullOrEmpty(s));
        if (invert) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToBrushConverter : IValueConverter
{
    public Brush? TrueBrush { get; set; }
    public Brush? FalseBrush { get; set; }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BackupTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var type = value?.ToString() ?? "";
        return type switch
        {
            "Full" => new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
            "Differential" => new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)),
            "TransactionLog" => new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60)),
            _ => new SolidColorBrush(Color.FromRgb(0x88, 0x90, 0xA4))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Picks black or white text for whatever colour it is given, by measured contrast (#126).
///
/// Used where the fill is DATA rather than theme - the path-element pills take their colour from
/// the element, so no single hardcoded foreground can be right for all of them. White on the
/// orange one scored 2.19:1 and on the green one 2.87:1, both well under the 4.5:1 minimum, while
/// the blue and purple ones were fine. This asks the colour rather than assuming.
/// </summary>
public class ContrastingTextConverter : IValueConverter
{
    private static readonly SolidColorBrush Dark = new(Color.FromRgb(0x0F, 0x13, 0x1E));
    private static readonly SolidColorBrush Light = new(Colors.White);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var colour = value switch
        {
            Color c => c,
            SolidColorBrush b => b.Color,
            string hex when TryParse(hex, out var parsed) => parsed,
            _ => Colors.Gray
        };

        // Compare both candidates and take the better, rather than thresholding on luminance. The
        // usual 0.179 crossover assumes PURE black and white; this app's dark is #0F131E, which
        // moves the crossover - and picking by the textbook threshold chose dark text at 4.12:1
        // for an orange where white would have given 4.50:1.
        return Contrast(Dark.Color, colour) >= Contrast(Light.Color, colour) ? Dark : Light;
    }

    private static bool TryParse(string hex, out Color colour)
    {
        try
        {
            colour = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            colour = Colors.Gray;
            return false;
        }
    }

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class DateTimeToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        if (value is DateTimeOffset dto)
            return dto.ToString("yyyy-MM-dd HH:mm:ss");
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts (timelinePosition, containerWidth, row) into a Margin for positioning dots.
/// Dots are positioned horizontally by ratio and stack vertically from the bottom.
/// </summary>
public class TimelinePositionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3
            || values[0] is not double ratio
            || values[1] is not double width
            || values[2] is not int row
            || width <= 0)
            return new Thickness(0);

        double dotSize = 14;
        double left = ratio * Math.Max(0, width - dotSize);
        double bottom = row * (dotSize + 4);
        return new Thickness(Math.Max(0, left), 0, 0, bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts (position, containerWidth) into a left Margin for tick marks.
/// </summary>
public class TickPositionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is not double ratio
            || values[1] is not double width
            || width <= 0)
            return new Thickness(0);

        double left = ratio * width;
        return new Thickness(Math.Max(0, left), 0, 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(color);
            }
            catch { }
        }
        return new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToEyeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "Hide" : "Show";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BackupSourceTypeToDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "Standalone" => "Standalone databases",
            "AvailabilityGroup" => "AG databases",
            "Mixed" => "Mix",
            _ => value?.ToString() ?? ""
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is not string paramStr) return false;
        return value?.ToString() == paramStr;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is string paramStr && targetType.IsEnum)
            return Enum.Parse(targetType, paramStr);
        return Binding.DoNothing;
    }
}
