#nullable enable
using Configurator.Core.Projects;
using Configurator.Core.Workspace;

namespace Configurator;

internal static class PlcAddressCatalog
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Step", "Prog_Step", "ProgramStep", "Matrix", "Matrices", "Matrix_List", "Program", "Programs", "Prog",
        "Area", "Areas", "PLC", "PLCs"
    };

    internal static async Task<SortedDictionary<string, PlcAddressMap>> LoadAsync(IProjectWorkspace workspace, CancellationToken ct)
    {
        var tables = await workspace.GetTablesAsync(ct).ConfigureAwait(false);
        var labels = await ProjectClassCatalog.ReadTablesAsync(workspace, tables, ct).ConfigureAwait(false);
        return await LoadMapsAsync(workspace, tables, labels, ct).ConfigureAwait(false);
    }

    internal static async Task<Dictionary<string, List<LookupOption>>> SuggestAddressesAsync(IProjectWorkspace workspace, string tableName, string plc,
        string? classNumber, CancellationToken ct)
    {
        var suggestions = new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(plc)) return suggestions;
        var tables = await workspace.GetTablesAsync(ct).ConfigureAwait(false);
        var labels = await ProjectClassCatalog.ReadTablesAsync(workspace, tables, ct).ConfigureAwait(false);
        var resolved = GraphRelationDefinition.ResolveTableName(labels.Keys, tableName);
        if (resolved == null || !labels.TryGetValue(resolved, out var binding)) return suggestions;
        string? title = binding.Title;
        if (binding.SharedClasses is { } classes &&
            (classNumber == null || !classes.TryGetValue(classNumber, out title))) return suggestions;
        var bare = tableName.Split('.').Last().Trim('[', ']');
        var area = AnalogArea(title!) ?? AnalogArea(bare);
        var defaultArea = area ?? DiscreteArea(title!) ?? DiscreteArea(bare);
        var fields = tables.First(t => t.TableName.Equals(resolved, StringComparison.OrdinalIgnoreCase)).Columns
            .Where(c => !c.IsReadOnly && !c.IsComputed && !c.IsIdentity && IsSuggestedField(c.Name)).ToList();
        if (fields.Count == 0) return suggestions;
        var maps = await LoadMapsAsync(workspace, tables, labels, ct).ConfigureAwait(false);
        if (!maps.TryGetValue(plc, out var map)) map = new();
        foreach (var field in fields)
        {
            if (FieldArea(field.Name, defaultArea) is not char target) continue;
            bool word = area.HasValue;
            bool text = field.DataType.Contains("char", StringComparison.OrdinalIgnoreCase)
                || field.DataType.Contains("text", StringComparison.OrdinalIgnoreCase);
            var options = map.FreeAddresses(target, text && !word, word ? 2 : 1, !text)
                .Select(value => new LookupOption { Key = value, Display = PlcAddressMap.DescribeAddress(value, target, word).Label }).ToList();
            if (options.Count == 0) continue;
            suggestions[field.Name] = options;
            map.Add(options[0].Key, field.Name, target, word);
        }
        return suggestions;
    }

    private static async Task<SortedDictionary<string, PlcAddressMap>> LoadMapsAsync(IProjectWorkspace workspace,
        IReadOnlyList<ProjectTableSchema> tables, Dictionary<string, ProjectClassTable> labels, CancellationToken ct)
    {
        var maps = new SortedDictionary<string, PlcAddressMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            string bare = table.TableName.Split('.').Last().Trim('[', ']');
            bool controllers = bare.Equals("PLC", StringComparison.OrdinalIgnoreCase) || bare.Equals("PLCs", StringComparison.OrdinalIgnoreCase);
            labels.TryGetValue(table.TableName, out var binding);
            var plcColumn = controllers ? "Record" : "PLC";
            var fields = table.Columns.Select(c => c.Name).Where(IsAddressField).ToList();
            if (!controllers && (binding == null || Excluded.Contains(binding.Title) || Excluded.Contains(bare) || fields.Count == 0)) continue;
            if (!table.Columns.Any(c => c.Name.Equals(plcColumn, StringComparison.OrdinalIgnoreCase))) continue;

            var scopeClass = ProjectClassCatalog.FindRecordClassColumn(table.KeyColumns);
            int rowNumber = 0;
            for (int number = 1; ; number++)
            {
                var page = await workspace.ReadPageAsync(new ProjectReadQuery
                {
                    TableName = table.TableName, PageNumber = number, PageSize = 1000
                }, ct).ConfigureAwait(false);
                foreach (var row in page.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    rowNumber++;
                    var plc = ProjectClassCatalog.Read(row, plcColumn);
                    if (plc == null) continue;
                    if (!maps.TryGetValue(plc, out var map)) maps.Add(plc, map = new());
                    if (controllers) continue;
                    var record = ProjectClassCatalog.Read(row, "Record") ?? (row.Key.Length > 0 ? row.Key : $"строка {rowNumber}");
                    var title = binding!.Title;
                    if (binding.SharedClasses is { } classes)
                    {
                        var classNumber = (scopeClass == null ? null : ProjectClassCatalog.Read(row, scopeClass))
                            ?? ProjectClassCatalog.Read(row, "Class") ?? ProjectClassCatalog.Read(row, "ClassNumber");
                        if (classNumber == null || !classes.TryGetValue(classNumber, out title))
                        {
                            map.Issues.Add($"{table.TableName} · {record} · не удалось определить класс элемента");
                            continue;
                        }
                        if (Excluded.Contains(title)) continue;
                    }
                    var name = ProjectClassCatalog.Read(row, "Name");
                    foreach (var field in fields)
                    {
                        if (ProjectClassCatalog.Read(row, field) is not { } raw) continue;
                        var signal = AnalogArea(title) ?? AnalogArea(bare);
                        var area = FieldArea(field, signal ?? DiscreteArea(title) ?? DiscreteArea(bare));
                        var display = $"{title} · {record}{(name == null ? "" : " · " + name)} · {field}";
                        map.Add(raw, display, area, signal != null, new(table.TableName, field, plc, raw, table, row, display, area, signal != null));
                    }
                }
                if (number >= page.TotalPages || page.Items.Count == 0) break;
            }
        }
        return maps;
    }

    private static char? AnalogArea(string name) => name.ToUpperInvariant() switch
    {
        "AI" or "ANAIN" or "ANALOGIN" or "ANALOGINPUT" => 'I',
        "AO" or "ANAOUT" or "ANALOGOUT" or "ANALOGOUTPUT" => 'Q',
        _ => null
    };

    private static char? DiscreteArea(string name) => name.ToUpperInvariant() switch
    {
        "DO" or "DISCRETOUT" or "DISCRETEOUT" => 'Q',
        "DI" or "DISCRETIN" or "DISCRETEIN" => 'I',
        _ => null
    };

    private static char? FieldArea(string name, char? analogArea) => name.Replace("_", "").ToUpperInvariant() switch
    {
        "ADDRESSINPUT" or "ADDRESSMS" => 'I',
        "ADDRESSOUTPUT" or "ADDRESSLIFTUP" or "ADDRESSLIFTDOWN" => 'Q',
        var field when field.StartsWith("ADDRESSFEEDBACK", StringComparison.Ordinal) => 'I',
        _ => analogArea
    };

    internal static bool IsSuggestedField(string name) => IsAddressField(name);

    internal static bool IsAddressField(string name)
    {
        var normalized = name.Replace("_", "").ToUpperInvariant();
        return normalized is "ADDRESS" or "ADRESS" or "ADDRESSINPUT" or "ADDRESSOUTPUT" or "ADDRESSLIFTUP" or "ADDRESSLIFTDOWN" or "ADDRESSMS"
            || normalized.StartsWith("ADDRESSFEEDBACK", StringComparison.Ordinal);
    }
}

