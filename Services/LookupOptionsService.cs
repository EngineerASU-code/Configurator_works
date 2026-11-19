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
        else switch (fieldName.Trim().ToUpperInvariant())
        {
            case "AREA":
                lookupTable = "dbo.Areas";
                preferredKey = "AreaName";
                displayHint = "AreaNameL2";
                useAreaNameLookup = true;
                break;
            case "UNIT":
                lookupTable = "dbo.Units";
                preferredKey = "Unit_ID";
                displayHint = "Name";
                break;
            case "PLC":
                lookupTable = "dbo.PLC";
                preferredKey = "Record";
                displayHint = "Name";
                break;
            case "CLASS":
                lookupTable = "dbo.Classes";
                preferredKey = "Record";
                displayHint = "Name";
                break;
            case "PLCCLASSNUMBER":
                // Different project schemas use slightly different spellings for this
                // lookup table and its key column. Resolve the real table/key below.
                lookupTable = "dbo.PLCClasssNumbers";
                preferredKey = "PLCClassNumber";
                displayHint = "Name";
                break;
            case "TYPE":
                var definition = SystemLookupCatalog.Find(tableName, "Type") ?? SystemLookupCatalog.ForClass(className);
                lookupTable = definition?.LookupTable;
                preferredKey = definition?.LookupKeyField;
                displayHint = "Name";
                break;
        }

        _tableListCache ??= await _db.GetTableListAsync(ct);
        var tables = _tableListCache;
        string FullName(DataRow r) => $"{Convert.ToString(r["TABLE_SCHEMA"])}.{Convert.ToString(r["TABLE_NAME"])}";
        bool Exists(string candidate) => tables.Rows.Cast<DataRow>().Any(r => FullName(r).Equals(candidate, StringComparison.OrdinalIgnoreCase));

        // Older projects use PLC_Types / PLCTypes while the generated lookup rule
        // names dbo.PLCType. Resolve those known spellings before enforcing the rule.
        if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase)
            && string.Equals(className, "PLC", StringComparison.OrdinalIgnoreCase)
            && !Exists(lookupTable))
        {
            lookupTable = new[] { "dbo.PLCType", "dbo.PLC_Types", "dbo.PLCTypes", "PLCType", "PLC_Types", "PLCTypes" }
                .FirstOrDefault(Exists);
            if (!string.IsNullOrWhiteSpace(lookupTable))
            {
                if (!_columnCache.TryGetValue(lookupTable, out var plcTypeColumns))
                {
                    plcTypeColumns = await _db.GetTableColumnsAsync(lookupTable, ct);
                    _columnCache[lookupTable] = plcTypeColumns;
                }
                var names = plcTypeColumns.Select(c => c.Name).ToList();
                preferredKey = new[] { "Record", "Type", "ID", "Id" }
                    .Select(candidate => names.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
                displayHint = new[] { "Name", "NameL2", "NameL1", "Description", "TypeName" }
                    .Select(candidate => names.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            }
        }

        if (!Exists(lookupTable) && fieldName.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase))
        {
            lookupTable = new[] { "dbo.PLCClasssNumbers", "dbo.PLCClasssNumber", "dbo.PLCClassNumbers", "dbo.PLCClassNumber", "dbo.PLCClasses", "dbo.Classes" }
                .FirstOrDefault(Exists);
            preferredKey = "PLCClassNumber";
        }
        if (!Exists(lookupTable) && fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase) && configuredRule == null && automaticDiscovery)
        {
            // Prefer the class's canonical lookup table, but tolerate common schema
            // variations (singular/plural and abbreviated names) found in project DBs.
            var classDefinition = SystemLookupCatalog.Find(tableName, "Type") ?? SystemLookupCatalog.ForClass(className);
            var canonicalLookup = classDefinition?.LookupTable;
            if (!string.IsNullOrWhiteSpace(canonicalLookup))
            {
                var canonicalShort = ShortName(canonicalLookup);
                var canonicalStem = canonicalShort.EndsWith("Types", StringComparison.OrdinalIgnoreCase)
                    ? canonicalShort[..^5]
                    : canonicalShort.EndsWith("Type", StringComparison.OrdinalIgnoreCase) ? canonicalShort[..^4] : canonicalShort;
                lookupTable = new[] { canonicalLookup, $"dbo.{canonicalStem}Type", $"dbo.{canonicalStem}Types", $"dbo.{canonicalStem}_Type", $"dbo.{canonicalStem}_Types" }.FirstOrDefault(Exists);
            }
            var shortName = tableName?.Split('.').LastOrDefault() ?? "";
            var stem = shortName;
            if (shortName.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && shortName.Length > 3)
                stem = shortName[..^3] + "y";
            else if (shortName.EndsWith("ses", StringComparison.OrdinalIgnoreCase) && shortName.Length > 3)
                stem = shortName[..^2];
            else if (shortName.EndsWith("s", StringComparison.OrdinalIgnoreCase) && shortName.Length > 1)
                stem = shortName[..^1];
            var candidates = new[] { $"dbo.{stem}Type", $"dbo.{stem}Types", $"dbo.{shortName}Type", $"dbo.{shortName}Types" };
            lookupTable = lookupTable ?? candidates.FirstOrDefault(Exists);
            if (string.IsNullOrWhiteSpace(lookupTable))
            {
                var allTypeTables = tables.Rows.Cast<DataRow>().Select(FullName)
                    .Where(n => n.EndsWith("Type", StringComparison.OrdinalIgnoreCase) || n.EndsWith("Types", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                lookupTable = allTypeTables.FirstOrDefault(n =>
                    ShortName(n).Equals(stem + "Type", StringComparison.OrdinalIgnoreCase) ||
                    ShortName(n).Equals(stem + "Types", StringComparison.OrdinalIgnoreCase) ||
                    ShortName(n).Equals(shortName + "Type", StringComparison.OrdinalIgnoreCase) ||
                    ShortName(n).Equals(shortName + "Types", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(lookupTable))
                {
                    // Strict auto-discovery against the source table. Strip only the
                    // Type/Types suffix and compare the complete remaining stem, so
                    // Valve can match Valves_Type but never Valve2S_Type.
                    static string NormalizeTypeStem(string value)
                    {
                        var normalized = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                        if (normalized.EndsWith("TYPES", StringComparison.Ordinal)) normalized = normalized[..^5];
                        else if (normalized.EndsWith("TYPE", StringComparison.Ordinal)) normalized = normalized[..^4];
                        return normalized;
                    }
                    static IEnumerable<string> StemVariants(string value)
                    {
                        string stemValue = NormalizeTypeStem(value);
                        if (!string.IsNullOrWhiteSpace(stemValue)) yield return stemValue;
                        if (stemValue.EndsWith("S", StringComparison.Ordinal) && stemValue.Length > 1) yield return stemValue[..^1];
                        if (stemValue.EndsWith("IES", StringComparison.Ordinal) && stemValue.Length > 3) yield return stemValue[..^3] + "Y";
                    }
                    var sourceStemCandidates = StemVariants(shortName)
                        .Concat(StemVariants(className ?? string.Empty))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    lookupTable = allTypeTables.FirstOrDefault(n => sourceStemCandidates.Contains(NormalizeTypeStem(ShortName(n))));
                }
                if (string.IsNullOrWhiteSpace(lookupTable))
                {
                    // Last-resort schema matching: common legacy databases use names
                    // such as DiscretInType / AnaOutType that do not start with the
                    // short class label (DI / AI / AO).
                    var aliases = className?.Trim().ToUpperInvariant() switch
                    {
                        "DI" => new[] { "DiscretInType", "DiscreteInType", "DigitalInputType", "DIType" },
                        "DO" => new[] { "DiscretOutType", "DiscreteOutType", "DigitalOutputType", "DOType" },
                        "AI" => new[] { "AnaInType", "AnalogInType", "AnalogInputType", "AIType" },
                        "AO" => new[] { "AnaOutType", "AnalogOutType", "AnalogOutputType", "AOType" },
                        "PID" => new[] { "ReglerType", "ReglerTypes", "PIDType" },
                        "DSV" => new[] { "Valve2SType", "DSVType" },
                        _ => Array.Empty<string>()
                    };
                    lookupTable = allTypeTables.FirstOrDefault(n => aliases.Any(a =>
                        ShortName(n).Equals(a, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        if (configuredRule != null && !Exists(lookupTable)) return Array.Empty<LookupOption>();
        if (string.IsNullOrWhiteSpace(lookupTable) || !Exists(lookupTable)) return Array.Empty<LookupOption>();

        if (!_columnCache.TryGetValue(lookupTable, out var columns))
        {
            columns = await _db.GetTableColumnsAsync(lookupTable, ct);
            _columnCache[lookupTable] = columns;
        }
        if (columns.Count == 0) return Array.Empty<LookupOption>();
        if (useAreaNameLookup && !columns.Any(c => c.Name.Equals("AreaName", StringComparison.OrdinalIgnoreCase)))
            return Array.Empty<LookupOption>();

        string key = columns.FirstOrDefault(c => !string.IsNullOrWhiteSpace(preferredKey) && c.Name.Equals(preferredKey, StringComparison.OrdinalIgnoreCase))?.Name
                     ?? (fieldName.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase) ? columns.FirstOrDefault(c => c.Name.Equals("PLCClasssNumber", StringComparison.OrdinalIgnoreCase))?.Name : null)
                     ?? (fieldName.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase) ? columns.FirstOrDefault(c => c.Name.Equals("ClassNumber", StringComparison.OrdinalIgnoreCase))?.Name : null)
                     ?? (fieldName.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase) ? columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name : null)
                     ?? columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? columns.FirstOrDefault(c => c.Name.Equals("Type", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? columns.FirstOrDefault(c => c.Name.Equals("ID", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? columns.FirstOrDefault(c => c.Name.EndsWith("ID", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? (await _db.GetPrimaryKeyColumnsAsync(lookupTable, ct)).FirstOrDefault()
                     ?? columns.FirstOrDefault()?.Name;
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
                string keyText = Convert.ToString(row[key]) ?? "";
                if (string.IsNullOrWhiteSpace(keyText)) continue;
                string display = BuildDisplay(row, columns.Select(x => x.Name).ToList(), key, displayHint, combineAreaNames);
                // Prefer the meaningful pair requested for Configurator lookup combos:
                // Record is the stored key, Name is the human-readable value.
                string nameColumn = columns.FirstOrDefault(c => c.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Name
                    ?? columns.FirstOrDefault(c => c.Name.Equals("NameL1", StringComparison.OrdinalIgnoreCase))?.Name
                    ?? columns.FirstOrDefault(c => c.Name.Equals("Title", StringComparison.OrdinalIgnoreCase))?.Name;
                if (useAreaNameLookup)
                {
                    string areaLabelColumn = columns.FirstOrDefault(c => c.Name.Equals("AreaNameL2", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? columns.FirstOrDefault(c => c.Name.Equals("AreaNameL1", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? columns.FirstOrDefault(c => c.Name.Equals("NameL2", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? columns.FirstOrDefault(c => c.Name.Equals("Name_L2", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? columns.FirstOrDefault(c => c.Name.Equals("NameL1", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? columns.FirstOrDefault(c => c.Name.Equals("Name_L1", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? columns.FirstOrDefault(c => c.Name.Equals("AreaName", StringComparison.OrdinalIgnoreCase))?.Name;
                    string areaLabel = string.IsNullOrWhiteSpace(areaLabelColumn) || row[areaLabelColumn] == DBNull.Value
                        ? string.Empty
                        : Convert.ToString(row[areaLabelColumn]) ?? string.Empty;
                    string recordValue = !string.IsNullOrWhiteSpace(recordColumn) && row.Table.Columns.Contains(recordColumn) && row[recordColumn] != DBNull.Value
                        ? Convert.ToString(row[recordColumn]) ?? string.Empty
                        : string.Empty;
                    display = string.IsNullOrWhiteSpace(areaLabel) ? (string.IsNullOrWhiteSpace(recordValue) ? keyText : recordValue)
                        : string.IsNullOrWhiteSpace(recordValue) ? areaLabel : $"{recordValue}. {areaLabel}";
                }
                else if (!combineAreaNames && !string.IsNullOrWhiteSpace(recordColumn) && !string.IsNullOrWhiteSpace(nameColumn)
                    && row.Table.Columns.Contains(recordColumn) && row.Table.Columns.Contains(nameColumn))
                {
                    string recordValue = Convert.ToString(row[recordColumn]) ?? keyText;
                    string nameValue = Convert.ToString(row[nameColumn]) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(nameValue)) display = $"{recordValue} · {nameValue}";
                }
                options.Add(new LookupOption { Key = keyText, Display = display });
            }
            if (data.Rows.Count < lookupPageSize || options.Count >= maxLookupOptions) break;
        }

        var result = options
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
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

    private static string BuildDisplay(DataRow row, IReadOnlyList<string> names, string key, string hint, bool combineAreaNames)
    {
        if (combineAreaNames && row.Table.Columns.Contains("AreaName"))
        {
            string l1 = Convert.ToString(row["AreaName"]) ?? "";
            string l2 = row.Table.Columns.Contains("AreaNameL2") ? Convert.ToString(row["AreaNameL2"]) ?? "" : "";
            if (!string.IsNullOrWhiteSpace(l2) && !l2.Equals(l1, StringComparison.OrdinalIgnoreCase)) return $"{l1} · {l2}";
            return l1;
        }

        foreach (var candidate in new[] { hint, "Name", "NameL1", "Name_L1", "NameL2", "Name_L2", "Description", "DescriptionL1", "Title", "Caption", "Label", "TypeName", "Type_Name" })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !row.Table.Columns.Contains(candidate)) continue;
            string value = Convert.ToString(row[candidate]) ?? "";
            if (!string.IsNullOrWhiteSpace(value)) return $"{value}  [{Convert.ToString(row[key])}]";
        }
        return Convert.ToString(row[key]) ?? "";
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
