using System;
using System.Collections.Generic;

namespace Configurator;

public partial class MainWindow
{
    private static int? ResolveSystemClassNumber(string tableName)
        => ProjectArchitectureResolver.ResolveSystemClassNumber(tableName);

    private static string ResolveClassNumberColumn(IEnumerable<ColumnInfo> columns)
    {
        return columns?.FirstOrDefault(IsClassNumberColumn)?.Name;
    }

    private static bool IsClassNumberColumn(ColumnInfo column)
    {
        if (column == null || string.IsNullOrWhiteSpace(column.Name))
            return false;

        string normalized = NormalizeIdentifier(column.Name);
        return normalized is "classnumber" or "plcclassnumber" or "plcclasssnumber";
    }
    private static string NormalizeIdentifier(string value)
        => new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
