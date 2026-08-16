using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CitusManager.Services;

internal static class PostgreSqlValueFormatter
{
    internal static string Format(object value) => value switch
    {
        string text => text,
        byte[] bytes => $"\\x{Convert.ToHexString(bytes).ToLowerInvariant()}",
        Array array => FormatArray(array),
        IDictionary dictionary => FormatDictionary(dictionary),
        bool boolean => boolean ? "true" : "false",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        JsonDocument document => document.RootElement.GetRawText(),
        JsonElement element => element.GetRawText(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static string FormatArray(Array array)
    {
        var indexes = new int[array.Rank];
        return FormatArrayDimension(array, 0, indexes);
    }

    private static string FormatArrayDimension(Array array, int dimension, int[] indexes)
    {
        var result = new StringBuilder("{");
        for (var index = array.GetLowerBound(dimension); index <= array.GetUpperBound(dimension); index++)
        {
            if (index > array.GetLowerBound(dimension)) result.Append(',');
            indexes[dimension] = index;
            if (dimension + 1 < array.Rank) result.Append(FormatArrayDimension(array, dimension + 1, indexes));
            else result.Append(FormatArrayElement(array.GetValue(indexes)));
        }
        return result.Append('}').ToString();
    }

    private static string FormatArrayElement(object? value)
    {
        if (value is null) return "NULL";
        if (value is byte[] bytes) return QuoteArrayElement(Format(bytes));
        if (value is Array nested) return FormatArray(nested);
        var text = Format(value);
        return NeedsArrayQuotes(text) ? QuoteArrayElement(text) : text;
    }

    private static bool NeedsArrayQuotes(string value) =>
        value.Length == 0 || value.Equals("NULL", StringComparison.OrdinalIgnoreCase) ||
        value.Any(character => char.IsWhiteSpace(character) || character is ',' or '{' or '}' or '"' or '\\');

    private static string QuoteArrayElement(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string FormatDictionary(IDictionary dictionary)
    {
        var entries = new List<string>();
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = QuoteMapValue(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
            var value = entry.Value is null ? "NULL" : QuoteMapValue(Format(entry.Value));
            entries.Add($"{key}=>{value}");
        }
        return string.Join(", ", entries);
    }

    private static string QuoteMapValue(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
