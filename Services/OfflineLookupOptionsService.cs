using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;

namespace Configurator;

/// <summary>Builds lookup lists from a captured project snapshot without opening a SQL connection.</summary>
public static class OfflineLookupOptionsService
{
    private static readonly string[] LookupFields = { "Type", "PLC", "Area", "PLCClassNumber", "PLCClasssNumber", "Class", "Unit" };

    public static Dictionary<string, List<LookupOption>> Build(
        ConfiguratorProject project, string sourceTable, IReadOnlyCollection<ColumnInfo> sourceColumns)
    {
        var result = new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
        if (project == null || string.IsNullOrWhiteSpace(sourceTable) || sourceColumns == null) return result;
        var sourceName = FindSchema(project, sourceTable)?.TableName ?? sourceTable;
        var classes = new Configurator.Infrastructure.Architecture.TemplateLibraryStore(project).Load();
        var definition = classes.FirstOrDefault(c => c.PrimaryStorage != null && SameTable(c.PrimaryStorage.TableName, sourceName));
        var className = definition?.Name ?? FindClassName(project, sourceName);
        foreach (var column in sourceColumns)
        {
            if (string.IsNullOrWhiteSpace(column?.Name)) continue;
            var field = definition?.Fields?.FirstOrDefault(f => f.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase))
                ?? new FieldDefinition { Name = column.Name };
            var options = Get(project, className, sourceName, field);
            if (options.Count > 0) result[column.Name] = options.ToList();
        }
        return result;
    }

    public static IReadOnlyList<LookupOption> Get(ConfiguratorProject project, string className, string sourceTable, FieldDefinition field)
    {
        if (project == null || field == null || string.IsNullOrWhiteSpace(field.Name)) return Array.Empty<LookupOption>();
        var fieldName = field.Name;
        sourceTable = FindSchema(project, sourceTable)?.TableName ?? sourceTable;
        var settings = LookupGenerationSettingsStore.Load(project);
        var rule = LookupGenerationRuleSelector.Find(settings, className, sourceTable, fieldName);
        if (rule != null && !rule.Enabled) return Array.Empty<LookupOption>();
        if (rule == null && string.IsNullOrWhiteSpace(field.LookupTable)
            && !LookupFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase)) return Array.Empty<LookupOption>();

        var lookupTable = rule?.LookupTable ?? field.LookupTable;
        var preferredKey = rule?.KeyField ?? field.LookupKeyField;
        var displayHint = rule?.DisplayField ?? field.LookupDisplayField;
        bool useAreaNameLookup = false;
        if (string.IsNullOrWhiteSpace(lookupTable))
            ResolveConventionalLookup(project, sourceTable, className, fieldName, settings.AutomaticDiscoveryEnabled, out lookupTable, out preferredKey, out displayHint, out useAreaNameLookup);

        if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase)
            && string.Equals(className, "PLC", StringComparison.OrdinalIgnoreCase)
            && FindSchema(project, lookupTable) == null)
        {
            var alternative = new[] { "dbo.PLCType", "dbo.PLC_Types", "dbo.PLCTypes", "PLCType", "PLC_Types", "PLCTypes" }
                .Select(candidate => FindSchema(project, candidate)?.TableName)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            if (!string.IsNullOrWhiteSpace(alternative))
            {
                lookupTable = alternative;
                var names = FindSchema(project, alternative).Columns.Select(c => c.Name).ToList();
                preferredKey = new[] { "Record", "Type", "ID", "Id" }.FirstOrDefault(c => names.Contains(c, StringComparer.OrdinalIgnoreCase));
                displayHint = new[] { "Name", "NameL2", "NameL1", "Description", "TypeName" }.FirstOrDefault(c => names.Contains(c, StringComparer.OrdinalIgnoreCase));
            }
        }
        if (string.IsNullOrWhiteSpace(lookupTable)) return Array.Empty<LookupOption>();
        var schema = FindSchema(project, lookupTable);
        var rows = project.LocalRecords.Where(r => SameTable(r.TableName, schema?.TableName ?? lookupTable)).ToList();
        if (rows.Count > 0)
        {
            var names = schema?.Columns.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                ?? rows.SelectMany(r => r.Values.Select(v => v.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var keyField = ResolveKey(names, schema?.KeyColumns, preferredKey);
            if (!string.IsNullOrWhiteSpace(keyField))
            {
                var options = rows.Select(record =>
                {
                    var key = GetValue(record.Values, keyField);
                    if (string.IsNullOrWhiteSpace(key)) return null;
                    var display = LookupOptionDisplayFormatter.Format(names,
                        column => GetValue(record.Values, column) ?? string.Empty,
                        key, displayHint, combineAreaNames: false, useAreaNameLookup: useAreaNameLookup);
                    return new LookupOption { Key = key, Display = display };
                }).Where(o => o != null).GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                if (options.Count > 0) return options;
            }
        }
        // Text lists identify their source dictionary; a bare field name such as Type
        // is insufficient to distinguish lists belonging to different classes.
        var textList = project.TextLists.FirstOrDefault(list =>
            SameTable(list.SourceTable, lookupTable) || SameTable(list.Name, lookupTable));
        if (textList == null)
        {
            var matchingLists = project.TextLists.Where(list =>
                (!string.IsNullOrWhiteSpace(list.SourceTable) && ShortName(list.SourceTable).Equals(ShortName(lookupTable), StringComparison.OrdinalIgnoreCase)
                    && (!NormalizeTable(list.SourceTable).Contains('.') || !NormalizeTable(lookupTable).Contains('.'))) ||
                (!string.IsNullOrWhiteSpace(list.Name) && ShortName(list.Name).Equals(ShortName(lookupTable), StringComparison.OrdinalIgnoreCase)
                    && (!NormalizeTable(list.Name).Contains('.') || !NormalizeTable(lookupTable).Contains('.')))).Take(2).ToList();
            if (matchingLists.Count == 1) textList = matchingLists[0];
        }
        if (textList == null) return Array.Empty<LookupOption>();
        var listKey = string.IsNullOrWhiteSpace(textList.KeyColumn) ? preferredKey ?? "Record" : textList.KeyColumn;
        var listDisplay = string.IsNullOrWhiteSpace(textList.DisplayColumn) ? displayHint ?? "Name" : textList.DisplayColumn;
        return textList.Items.Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Select(item => new LookupOption
            {
                Key = item.Key,
                Display = LookupOptionDisplayFormatter.Format(new[] { listKey, listDisplay },
                    column => column.Equals(listKey, StringComparison.OrdinalIgnoreCase) ? item.Key : item.Text,
                    item.Key, listDisplay, combineAreaNames: false, useAreaNameLookup: useAreaNameLookup)
            })
            .GroupBy(option => option.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }

    private static void ResolveConventionalLookup(ConfiguratorProject project, string sourceTable, string className, string fieldName, bool automaticDiscovery,
        out string table, out string key, out string display, out bool useAreaNameLookup)
    {
        var definition = SystemLookupCatalog.Find(sourceTable, fieldName)
            ?? (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase) ? SystemLookupCatalog.ForClass(className) : null);
        table = null;
        key = definition?.LookupKeyField;
        display = null;
        useAreaNameLookup = false;

        if (definition != null)
        {
            var canonicalShort = ShortName(definition.LookupTable);
            var canonicalStem = canonicalShort.EndsWith("Types", StringComparison.OrdinalIgnoreCase) ? canonicalShort[..^5]
                : canonicalShort.EndsWith("Type", StringComparison.OrdinalIgnoreCase) ? canonicalShort[..^4] : canonicalShort;
            table = automaticDiscovery ? FindTypeLookupTable(project, sourceTable, definition.LookupTable, canonicalStem)
                : FindSchema(project, definition.LookupTable)?.TableName;
            if (table != null) return;
            // Retain the canonical dictionary identity for a packaged text list
            // even when its database schema was not captured.
            table = definition.LookupTable;
        }

        if (automaticDiscovery && fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
        {
            table = FindTypeLookupTable(project, sourceTable, null, null) ?? table;
            key = "Record";
            display = "Name";
            if (table != null) return;
        }

        switch (fieldName.Trim().ToUpperInvariant())
        {
            case "PLC": table = FindNamedSchema(project, "dbo.PLC", "PLC"); key = "Record"; display = "Name"; break;
            case "CLASS": table = FindNamedSchema(project, "dbo.Classes", "Classes"); key = "Record"; display = "Name"; break;
            case "AREA": table = FindNamedSchema(project, "dbo.Areas", "Areas"); key = "AreaName"; display = "AreaNameL2"; useAreaNameLookup = true; break;
            case "UNIT": table = FindNamedSchema(project, "dbo.Units", "Units"); key = "Unit_ID"; display = "Name"; break;
            case "PLCCLASSNUMBER":
            case "PLCCLASSSNUMBER": table = FindNamedSchema(project, "dbo.PLCClasssNumbers", "PLCClasssNumbers"); key = "PLCClassNumber"; display = "Name"; break;
        }
    }

    private static string FindTypeLookupTable(ConfiguratorProject project, string sourceTable, string canonical, string canonicalStem)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(canonical)) candidates.Add(canonical);
        var sourceShort = ShortName(sourceTable);
        var stems = new HashSet<string>(new[] { sourceShort, Singularize(sourceShort) }, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(canonicalStem)) stems.Add(canonicalStem);
        foreach (var stem in stems)
        {
            candidates.Add($"dbo.{stem}Type");
            candidates.Add($"dbo.{stem}Types");
            candidates.Add($"dbo.{stem}_Type");
            candidates.Add($"dbo.{stem}_Types");
        }
        foreach (var candidate in candidates)
        {
            var schema = FindSchema(project, candidate);
            if (schema != null) return schema.TableName;
        }

        var normalizedStems = stems.Select(NormalizeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in project.TableSchemas)
        {
            var normalized = NormalizeName(ShortName(schema.TableName));
            var typeStem = normalized.EndsWith("TYPES", StringComparison.OrdinalIgnoreCase) ? normalized[..^5]
                : normalized.EndsWith("TYPE", StringComparison.OrdinalIgnoreCase) ? normalized[..^4] : null;
            if (typeStem != null && normalizedStems.Contains(typeStem)) return schema.TableName;
        }
        return null;
    }

    private static string FindNamedSchema(ConfiguratorProject project, string qualifiedName, string shortName)
        => FindSchema(project, qualifiedName)?.TableName
            ?? project.TableSchemas.FirstOrDefault(schema => ShortName(schema.TableName).Equals(shortName, StringComparison.OrdinalIgnoreCase))?.TableName;

    private static string NormalizeName(string name)
        => new((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());

    private static string ResolveKey(IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, string preferred)
    {
        foreach (var candidate in new[] { preferred, "Record", "Type", "ID", "Id" }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var match = columns.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        var identity = columns.FirstOrDefault(name => name.EndsWith("ID", StringComparison.OrdinalIgnoreCase));
        return identity ?? keyColumns?.Select(key => columns.FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(name => name != null) ?? columns.FirstOrDefault();
    }

    private static string FindClassName(ConfiguratorProject project, string sourceTable)
    {
        var definition = SystemLookupCatalog.All.FirstOrDefault(item => SameTable(item.TargetTable, sourceTable));
        if (definition != null) return definition.ClassName;
        var classes = project.LocalRecords.Where(record => SameTable(record.TableName, "dbo.Classes"));
        foreach (var record in classes)
            if (SameTable(GetValue(record.Values, "DBName"), sourceTable))
                return GetValue(record.Values, "Name") ?? GetValue(record.Values, "ClassName") ?? GetValue(record.Values, "Class");
        return ShortName(sourceTable);
    }

    private static ProjectTableSchema FindSchema(ConfiguratorProject project, string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return null;
        var exact = project.TableSchemas.FirstOrDefault(schema => SameTable(schema.TableName, tableName));
        if (exact != null || NormalizeTable(tableName).Contains('.')) return exact;
        var matches = project.TableSchemas.Where(schema => ShortName(schema.TableName).Equals(tableName, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string GetValue(JsonObject values, string fieldName)
    {
        if (values == null || string.IsNullOrWhiteSpace(fieldName)) return null;
        foreach (var pair in values)
            if (pair.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                if (pair.Value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)) return text;
                return pair.Value?.ToJsonString();
            }
        return null;
    }

    private static bool SameTable(string left, string right)
        => string.Equals(NormalizeTable(left), NormalizeTable(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTable(string name)
        => string.Join('.', (name ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Trim('[', ']')));

    private static string ShortName(string name)
        => NormalizeTable(name).Split('.').LastOrDefault() ?? string.Empty;

    private static string Singularize(string name)
    {
        if (name.EndsWith("ies", StringComparison.OrdinalIgnoreCase)) return name[..^3] + "y";
        if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase) && name.Length > 1) return name[..^1];
        return name;
    }
}
