using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;

namespace Configurator;

public sealed class LookupOptionsService
{
    private readonly DatabaseService _db;
    private readonly Dictionary<string, IReadOnlyList<LookupOption>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DataTable _tableListCache;
    private readonly Dictionary<string, List<ColumnInfo>> _columnCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _classNameCache = new(StringComparer.OrdinalIgnoreCase);

    public LookupOptionsService(DatabaseService db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<IReadOnlyList<LookupOption>> TryGetAsync(string className, string tableName, string fieldName, CancellationToken ct = default, string explicitLookupTable = null, string explicitLookupKeyField = null, string explicitLookupDisplayField = null)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return Array.Empty<LookupOption>();
        bool isPlcClassNumber = fieldName.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("PLCClasssNumber", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(className))
            className = await ResolveClassNameAsync(tableName, ct);

        string lookupTable = string.IsNullOrWhiteSpace(explicitLookupTable) ? null : explicitLookupTable.Trim();
        string preferredKey = string.IsNullOrWhiteSpace(explicitLookupKeyField) ? null : explicitLookupKeyField.Trim();
        string displayHint = string.IsNullOrWhiteSpace(explicitLookupDisplayField) ? null : explicitLookupDisplayField.Trim();
        bool combineAreaNames = false;
        bool useAreaNameLookup = false;
        LookupGenerationRule configuredRule = null;

        // Project rules apply to any source field (not only "Type"). The most specific rule wins and a
        // disabled winner means: do not generate a lookup for this field.
        var generationSettings = LookupGenerationSettingsRuntime.Current;
        bool automaticDiscovery = generationSettings?.AutomaticDiscoveryEnabled ?? true;
        var matchedRule = LookupGenerationRuleSelector.Find(generationSettings, className, tableName, fieldName);
        if (matchedRule != null)
        {
            if (!matchedRule.Enabled) return Array.Empty<LookupOption>();
            configuredRule = matchedRule;
            lookupTable = configuredRule.LookupTable;
            preferredKey = configuredRule.KeyField;
            displayHint = configuredRule.DisplayField;
        }

        if (!string.IsNullOrWhiteSpace(lookupTable))
        {
            // Explicit class-field metadata has priority over naming conventions.
            displayHint = displayHint ?? "Name";
        }
        else
        {
            LookupOptionRules.ResolveConventionalLookup(tableName, className, fieldName,
                out lookupTable, out preferredKey, out displayHint, out useAreaNameLookup);
        }

        _tableListCache ??= await _db.GetTableListAsync(ct);
        var tables = _tableListCache.Rows.Cast<DataRow>()
            .Select(row => $"{row["TABLE_SCHEMA"]}.{row["TABLE_NAME"]}").ToArray();
        lookupTable = LookupOptionRules.ResolveLegacyTable(tables, tableName, className, fieldName, lookupTable,
            automaticDiscovery, configuredRule != null || !string.IsNullOrWhiteSpace(explicitLookupTable));
        if (lookupTable == null) return Array.Empty<LookupOption>();

        if (!_columnCache.TryGetValue(lookupTable, out var columns))
        {
            columns = await _db.GetTableColumnsAsync(lookupTable, ct);
            _columnCache[lookupTable] = columns;
        }
        if (columns.Count == 0) return Array.Empty<LookupOption>();
        if (useAreaNameLookup && !columns.Any(c => c.Name.Equals("AreaName", StringComparison.OrdinalIgnoreCase)))
            return Array.Empty<LookupOption>();

        var columnNames = columns.Select(column => column.Name).ToList();
        var keyAliases = isPlcClassNumber ? new[] { "PLCClasssNumber", "ClassNumber" } : Array.Empty<string>();
        string key = LookupOptionRules.ResolveKeyField(columnNames,
            await _db.GetPrimaryKeyColumnsAsync(lookupTable, ct), preferredKey, keyAliases);
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<LookupOption>();

        string cacheKey = lookupTable + "|" + key + "|" + (combineAreaNames ? "area" : displayHint ?? "");
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        // Fetch both the stored key and human-readable columns. Without selecting
        // Name/Description, BuildDisplay can only fall back to numeric lookup keys.
        var recordColumn = columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name;
        var order = new[] { key, displayHint, useAreaNameLookup ? recordColumn : null, "Name", "NameL1", "Name_L1", "NameL2", "Name_L2", "Description", "DescriptionL1", "Title", "Caption", "Label", "TypeName", "Type_Name", "NameL1", "AreaName", "AreaNameL2" }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(candidate => columns.FirstOrDefault(c => c.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))?.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var options = new List<LookupOption>();
        const int lookupPageSize = 2000;
        const int maxLookupOptions = 50_000;   // protects the UI from huge tables selected by a rule
        for (int pageNumber = 1; ; pageNumber++)
        {
            var data = await _db.GetTableDataPageAsync(lookupTable, pageNumber, lookupPageSize, order, null, ct);
            foreach (DataRow row in data.Rows)
            {
                if (row[key] == DBNull.Value) continue;
                string keyText = Convert.ToString(row[key], System.Globalization.CultureInfo.InvariantCulture) ?? "";
                if (string.IsNullOrWhiteSpace(keyText)) continue;
                var rowColumns = row.Table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray();
                string display = LookupOptionDisplayFormatter.Format(rowColumns,
                    column => row[column] == DBNull.Value ? string.Empty : Convert.ToString(row[column]),
                    keyText, displayHint, combineAreaNames, useAreaNameLookup);
                options.Add(new LookupOption { Key = keyText, Display = display });
            }
            if (data.Rows.Count < lookupPageSize || options.Count >= maxLookupOptions) break;
        }

        var result = LookupOptionRules.KeepUnambiguousKeys(options);
        _cache[cacheKey] = result;
        return result;
    }

    private static string ShortName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        int dot = fullName.LastIndexOf('.');
        return dot >= 0 ? fullName[(dot + 1)..] : fullName;
    }

    private async Task<string> ResolveClassNameAsync(string tableName, CancellationToken ct)
    {
        var cacheKey = tableName ?? string.Empty;
        if (_classNameCache.TryGetValue(cacheKey, out var cached)) return cached;
        var definition = SystemLookupCatalog.Find(tableName, "Type");
        if (definition != null)
        {
            _classNameCache[cacheKey] = definition.ClassName;
            return definition.ClassName;
        }

        const string classesTable = "dbo.Classes";
        _tableListCache ??= await _db.GetTableListAsync(ct);
        bool classesExist = _tableListCache.Rows.Cast<DataRow>().Any(row =>
            $"{Convert.ToString(row["TABLE_SCHEMA"])}.{Convert.ToString(row["TABLE_NAME"])}".Equals(classesTable, StringComparison.OrdinalIgnoreCase));
        if (classesExist)
        {
            var columns = await _db.GetTableColumnsAsync(classesTable, ct);
            var databaseNameColumn = columns.FirstOrDefault(column => column.Name.Equals("DBName", StringComparison.OrdinalIgnoreCase))?.Name;
            var classNameColumn = columns.FirstOrDefault(column => column.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Name
                ?? columns.FirstOrDefault(column => column.Name.Equals("ClassName", StringComparison.OrdinalIgnoreCase))?.Name;
            if (databaseNameColumn != null && classNameColumn != null)
            {
                for (var page = 1; page <= 20; page++)
                {
                    var rows = await _db.GetTableDataPageAsync(classesTable, page, 500, new[] { databaseNameColumn }, null, ct);
                    foreach (DataRow row in rows.Rows)
                    {
                        var databaseName = Convert.ToString(row[databaseNameColumn]);
                        if (SameTableName(databaseName, tableName))
                        {
                            var resolved = Convert.ToString(row[classNameColumn]);
                            if (!string.IsNullOrWhiteSpace(resolved))
                            {
                                _classNameCache[cacheKey] = resolved;
                                return resolved;
                            }
                        }
                    }
                    if (rows.Rows.Count < 500) break;
                }
            }
        }

        var fallback = ShortName(tableName);
        _classNameCache[cacheKey] = fallback;
        return fallback;
    }

    private static bool SameTableName(string left, string right)
    {
        static string Normalize(string value) => string.Join('.', (value ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim().Trim('[', ']')));
        return Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

}

/// <summary>Formats an Area label without changing the persisted lookup key.</summary>
public static class AreaDisplayFormatter
{
    public static string ExtractName(string key, string display)
    {
        if (string.IsNullOrWhiteSpace(display)) return string.Empty;
        string prefix = (key?.Trim() ?? string.Empty) + " - ";
        if (display.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return display[prefix.Length..].Trim();
        prefix = (key?.Trim() ?? string.Empty) + " + ";
        if (display.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return display[prefix.Length..].Trim();
        string suffix = " [" + (key?.Trim() ?? string.Empty) + "]";
        if (display.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return display[..^suffix.Length].Trim();
        suffix = " · " + (key?.Trim() ?? string.Empty);
        if (display.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return display[..^suffix.Length].Trim();
        return display.Equals(key, StringComparison.OrdinalIgnoreCase) ? string.Empty : display.Trim();
    }

    public static string Format(string key, string name)
    {
        key = key?.Trim() ?? string.Empty;
        name = name?.Trim() ?? string.Empty;
        if (key.Length == 0) return string.Empty;
        return name.Length == 0 || name.Equals(key, StringComparison.OrdinalIgnoreCase) ? key : $"{key} + {name}";
    }
}
