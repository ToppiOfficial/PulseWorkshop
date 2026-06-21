using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PulseWorkshop.App;

/// <summary>Non-empty string -> Visible, null/empty/whitespace -> Collapsed.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true -> Visible, false -> Collapsed. ConverterParameter="invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>
/// Compacts a long file path so the filename is always visible, collapsing the middle of the
/// directory with an ellipsis, e.g. "D:\Development\game-mods\...\survival_bill.vpk". The optional
/// ConverterParameter sets the max character budget (default 48). Plain end-trimming hides the
/// filename - the part that actually identifies the file - so we keep head + tail instead.
/// </summary>
public sealed class PathEllipsisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var max = 48;
        if (parameter is string p && int.TryParse(p, out var parsed) && parsed > 0)
            max = parsed;

        if (path.Length <= max)
            return path;

        const string ellipsis = "...";
        var sep = path.Contains('\\') ? '\\' : '/';
        var fileName = path[(path.LastIndexOf(sep) + 1)..];

        // Always keep the full filename; fill the remaining budget with the head of the path.
        var tail = sep + fileName;
        var headBudget = max - ellipsis.Length - tail.Length;
        if (headBudget <= 0)
            // Filename alone already exceeds the budget - trim the filename's middle instead.
            return fileName.Length <= max
                ? fileName
                : fileName[..(max - ellipsis.Length)] + ellipsis;

        return string.Concat(path.AsSpan(0, headBudget), ellipsis, tail);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>First non-whitespace character of a string, upper-cased (avatar placeholder initial).</summary>
public sealed class FirstLetterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string)?.Trim();
        return string.IsNullOrEmpty(s) ? "?" : char.ToUpper(s[0], culture).ToString();
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// null -> Collapsed, non-null -> Visible (for showing the editor only when present).
/// Pass ConverterParameter="invert" to flip the result (show only when null).
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
