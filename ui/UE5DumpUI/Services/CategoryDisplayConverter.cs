using System.Globalization;
using Avalonia.Data.Converters;

namespace UE5DumpUI.Services;

/// <summary>
/// Renders <see cref="FunctionCategory"/>? values for the chip-filter
/// ComboBox in the Interesting Functions Finder. Null → "All" (the
/// "no filter" sentinel); concrete values use the same DisplayName the
/// score chip column uses, keeping the chip + dropdown labels consistent.
/// </summary>
public sealed class CategoryDisplayConverter : IValueConverter
{
    public static readonly CategoryDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "All";
        if (value is FunctionCategory fc) return KeywordScoringTable.DisplayName(fc);
        if (value is PropertyCategory pc) return PropertyScoringTable.DisplayName(pc);
        return value.ToString() ?? "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
