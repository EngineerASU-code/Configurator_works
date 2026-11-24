using System;
using System.Globalization;

namespace Configurator.Core.Projects;

public static class ProjectWriteGuard
{
    public const string ArchitectureReadOnly = "Архитектура проекта и реестр классов доступны только для просмотра.";

    public static bool IsDataTableWritable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return false;
        var parts = tableName.Replace("[", "").Replace("]", "").Split('.', StringSplitOptions.TrimEntries);
        var leaf = parts[^1];
        return parts.Length <= 2 && leaf.Length > 0
            && !IsIdentifier(leaf, "Classes") && !IsIdentifier(leaf, "__ConfiguratorSavedQueries")
            && (parts.Length == 1 || (parts[0].Length > 0 &&
                !IsIdentifier(parts[0], "sys") && !IsIdentifier(parts[0], "INFORMATION_SCHEMA")));
    }

    public static void EnsureDataTableWritable(string tableName)
    {
        if (!IsDataTableWritable(tableName))
            throw new InvalidOperationException($"Таблица «{tableName}» доступна только для просмотра.");
    }

    private static bool IsIdentifier(string value, string expected) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(value, expected,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth) == 0;
}
