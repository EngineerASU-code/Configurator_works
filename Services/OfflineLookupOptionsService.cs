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
            LookupOptionRules.ResolveConventionalLookup(sourceTable, className, fieldName, out lookupTable, out preferredKey, out displayHint, out useAreaNameLookup);

        var available = project.TableSchemas.Select(table => table.TableName).ToArray();
        var resolved = LookupOptionRules.ResolveLegacyTable(available, sourceTable, className, fieldName, lookupTable,
            settings.AutomaticDiscoveryEnabled, rule != null || !string.IsNullOrWhiteSpace(field.LookupTable));
        lookupTable = resolved ?? lookupTable;
        if (string.IsNullOrWhiteSpace(lookupTable)) return Array.Empty<LookupOption>();
        var schema = FindSchema(project, lookupTable);
        var rows = project.LocalRecords.Where(r => SameTable(r.TableName, schema?.TableName ?? lookupTable)).ToList();
        if (rows.Count > 0)
        {
            var names = schema?.Columns.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                ?? rows.SelectMany(r => r.Values.Select(v => v.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var keyAliases = fieldName.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("PLCClasssNumber", StringComparison.OrdinalIgnoreCase)
                ? new[] { "PLCClasssNumber", "ClassNumber" } : Array.Empty<string>();
            if (useAreaNameLookup && !names.Contains("AreaName", StringComparer.OrdinalIgnoreCase))
                return Array.Empty<LookupOption>();
            var keyField = LookupOptionRules.ResolveKeyField(names, schema?.KeyColumns, preferredKey, keyAliases);
            if (!string.IsNullOrWhiteSpace(keyField))
            {
                var options = LookupOptionRules.KeepUnambiguousKeys(rows.Select(record =>
                {
                    var key = GetValue(record.Values, keyField);
                    if (string.IsNullOrWhiteSpace(key)) return null;
                    var display = LookupOptionDisplayFormatter.Format(names,
                        column => GetValue(record.Values, column) ?? string.Empty,
                        key, displayHint, combineAreaNames: false, useAreaNameLookup: useAreaNameLookup, isType: fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase));
                    return new LookupOption { Key = key, Display = display };
                }));
                return options;
            }
            return Array.Empty<LookupOption>();
        }
        if (schema != null) return Array.Empty<LookupOption>();

        // Text lists identify their source dictionary; a bare field name such as Type
        // is insufficient to distinguish lists belonging to different classes.
        var exactTextLists = project.TextLists.Where(list =>
            SameTable(list.SourceTable, lookupTable) || SameTable(list.Name, lookupTable)).Take(2).ToList();
        var textList = exactTextLists.Count == 1 ? exactTextLists[0] : null;
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
        return LookupOptionRules.KeepUnambiguousKeys(textList.Items.Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Select(item => new LookupOption
            {
                Key = item.Key,
                Display = LookupOptionDisplayFormatter.Format(new[] { listKey, listDisplay },
                    column => column.Equals(listKey, StringComparison.OrdinalIgnoreCase) ? item.Key : item.Text,
                    item.Key, listDisplay, combineAreaNames: false, useAreaNameLookup: useAreaNameLookup, isType: fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
            }));
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

}
