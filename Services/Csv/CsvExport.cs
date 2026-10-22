namespace ShopCoursework.Services.Csv;

public static class CsvExport
{
    public static string Row(params string[] values) =>
        string.Join(",", values.Select(EscapeField));

    private static string EscapeField(string value)
    {
        var firstNonWhitespace = value.FirstOrDefault(character => !char.IsWhiteSpace(character));
        if (firstNonWhitespace is '=' or '+' or '-' or '@')
            value = "'" + value;

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
