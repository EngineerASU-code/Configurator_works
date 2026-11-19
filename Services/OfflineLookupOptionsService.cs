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
        ConfiguratorProject project,
        string sourceTable,
        IReadOnlyCollection<ColumnInfo> sourceColumns)
    {
        var result = new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
        if (project == null || string.IsNullOrWhiteSpace(sourceTable) || sourceColumns == null) return result;

        var sourceSchema = FindSchema(project, sourceTable);
        var sourceName = sourceSchema?.TableName ?? sourceTable;
        var className = FindClassName(project, sourceName);
        var settings = LookupGenerationSettingsStore.Load(project);

        foreach (var column in sourceColumns)
        {
            var fieldName = column?.Name;
            if (string.IsNullOrWhiteSpace(fieldName) || !LookupFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase)) continue;

            var rule = LookupGenerationRuleSelector.Find(settings, className, sourceName, fieldName);
            if (rule != null && !rule.Enabled) continue;

            var lookupTable = rule?.LookupTable;
            var preferredKey = rule?.KeyField;
            var displayHint = rule?.DisplayField;
            if (string.IsNullOrWhiteSpace(lookupTable))
                ResolveConventionalLookup(project, sourceName, className, fieldName, out lookupTable, out preferredKey, out displayHint);
            if (string.IsNullOrWhiteSpace(lookupTable)) continue;

            var lookupSchema = FindSchema(project, lookupTable);
            if (lookupSchema == null) continue;
            var lookupRows = project.LocalRecords
                .Where(record => SameTable(record.TableName, lookupSchema.TableName))
                .ToList();
            if (lookupRows.Count == 0) continue;

            var schemaColumns = lookupSchema.Columns.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var keyField = ResolveKey(schemaColumns, lookupSchema.KeyColumns, preferredKey);
            if (string.IsNullOrWhiteSpace(keyField)) continue;

            var displayField = ResolveDisplay(schemaColumns, displayHint, fieldName.Equals("Area", StringComparison.OrdinalIgnoreCase));
            var directTypeLookup = fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase)
                ? SystemLookupCatalog.Find(sourceName, fieldName)
                : null;
            var useDirectTypeDisplay = directTypeLookup != null && FindSchema(project, directTypeLookup.LookupTable) != null;
            var options = lookupRows
                .Select(record =>
                {
                    var key = GetValue(record.Values, keyField);
                    if (string.IsNullOrWhiteSpace(key)) return null;
                    var display = BuildDisplay(record.Values, key, keyField, displayField, fieldName, useDirectTypeDisplay);
                    return new LookupOption { Key = key, Display = display };
                })
                .Where(option => option != null)
                .GroupBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (options.Count > 0) result[fieldName] = options;
        }

        return result;
    }

    private static void ResolveConventionalLookup(ConfiguratorProject project, string sourceTable, string className, string fieldName,
        out string table, out string key, out string display)
    {
        var definition = SystemLookupCatalog.Find(sourceTable, fieldName)
            ?? (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase) ? SystemLookupCatalog.ForClass(className) : null);
        table = null;
        key = definition?.LookupKeyField;
        display = null;

        if (definition != null)
        {
            var canonicalShort = ShortName(definition.LookupTable);
            var canonicalStem = canonicalShort.EndsWith("Types", StringComparison.OrdinalIgnoreCase) ? canonicalShort[..^5]
                : canonicalShort.EndsWith("Type", StringComparison.OrdinalIgnoreCase) ? canonicalShort[..^4] : canonicalShort;
            table = FindTypeLookupTable(project, sourceTable, definition.LookupTable, canonicalStem);
            if (table != null) return;
        }

        if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
        {
            table = FindTypeLookupTable(project, sourceTable, null, null);
            key = "Record";
            display = "Name";
            if (table != null) return;
        }

        switch (fieldName.Trim().ToUpperInvariant())
        {
            case "PLC": table = FindNamedSchema(project, "dbo.PLC", "PLC"); key = "Record"; display = "Name"; break;
            case "CLASS": table = FindNamedSchema(project, "dbo.Classes", "Classes"); key = "Record"; display = "Name"; break;
            case "AREA": table = FindNamedSchema(project, "dbo.Areas", "Areas"); key = "AreaName"; display = "AreaNameL2"; break;
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

    private static string ResolveDisplay(IReadOnlyList<string> columns, string hint, bool area)
    {
        var candidates = area
            ? new[] { hint, "AreaNameL2", "AreaNameL1", "NameL2", "Name_L2", "NameL1", "Name_L1", "AreaName" }
            : new[] { hint, "Name", "NameL1", "Name_L1", "NameL2", "Name_L2", "Description", "DescriptionL1", "Title", "Caption", "Label", "TypeName", "Type_Name" };
        return candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => columns.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(name => name != null);
    }

    private static string BuildDisplay(JsonObject values, string key, string keyField, string displayField, string sourceField, bool directTypeLookup)
    {
        if (sourceField.Equals("Area", StringComparison.OrdinalIgnoreCase))
        {
            var area = GetValue(values, "AreaName") ?? key;
            var translated = GetValue(values, displayField) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(translated) && !translated.Equals(area, StringComparison.OrdinalIgnoreCase))
                return $"{area} · {translated}";
            return area;
        }

        if (directTypeLookup)
        {
            var directLabel = new[] { "Name", "NameL1", "Description", "DescriptionL1" }
                .Select(column => GetValue(values, column)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            var secondLanguage = GetValue(values, "NameL2");
            if (!string.IsNullOrWhiteSpace(secondLanguage))
                directLabel = string.IsNullOrWhiteSpace(directLabel) ? secondLanguage : $"{directLabel} · {secondLanguage}";
            return string.IsNullOrWhiteSpace(directLabel) ? key : $"{key} — {directLabel}";
        }

        var label = displayField == null ? null : GetValue(values, displayField);
        label = string.IsNullOrWhiteSpace(label)
            ? new[] { "Name", "NameL1", "Name_L1", "NameL2", "Name_L2", "Description", "DescriptionL1", "Title", "Caption", "Label", "TypeName", "Type_Name" }
                .Select(column => GetValue(values, column)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : label;
        if (string.IsNullOrWhiteSpace(label)) return key;
        var nameValue = new[] { "Name", "NameL1", "Title" }
            .Select(column => GetValue(values, column)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var record = GetValue(values, "Record");
        if (!string.IsNullOrWhiteSpace(nameValue) && !string.IsNullOrWhiteSpace(record)) return $"{record} · {nameValue}";
        return $"{label} [{key}]";
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
        => project.TableSchemas.FirstOrDefault(schema => SameTable(schema.TableName, tableName));

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
