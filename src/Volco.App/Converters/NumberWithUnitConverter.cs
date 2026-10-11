using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;

namespace Volco.App.Converters;

/// <summary>
/// A NumericUpDown.TextConverter that shows a whole number with its unit ("120 s") and reads back
/// whatever number is typed, with or without the unit ("30", "30s", "30 s"). Without it, a
/// FormatString such as "0 s" only accepts text whose non-digit characters match the shown ones.
/// NumericUpDown calls the converter backwards: Convert turns text into a value, ConvertBack turns
/// the value into text.
/// </summary>
public sealed partial class NumberWithUnitConverter : IValueConverter
{
    /// <summary>The unit after the number, such as "s", "ms" or "%".</summary>
    public string Unit { get; set; } = "";

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex Number();

    /// <summary>Text to value: the first whole number in the text. Text with no number throws,
    /// which NumericUpDown treats as invalid input and keeps the previous value.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Parse(value as string);

    /// <summary>Value to text: "120 s", or empty for no value.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Format(value as decimal?);

    internal decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Number().Match(text);
        if (!match.Success) throw new FormatException($"No number in \"{text}\".");
        return decimal.Parse(match.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    internal string Format(decimal? value)
    {
        if (value is not { } v) return "";
        var number = Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        return Unit.Length == 0 ? number : $"{number} {Unit}";
    }
}
