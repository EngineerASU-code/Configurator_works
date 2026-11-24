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
        var maps = new SortedDictionary<string, PlcAddressMap>(StringComparer.OrdinalIgnoreCase);
        var tables = await workspace.GetTablesAsync(ct).ConfigureAwait(false);
        var labels = await ProjectClassCatalog.ReadTablesAsync(workspace, tables, ct).ConfigureAwait(false);
        foreach (var table in tables)
        {
            string bare = table.TableName.Split('.').Last().Trim('[', ']');
            bool controllers = bare.Equals("PLC", StringComparison.OrdinalIgnoreCase) || bare.Equals("PLCs", StringComparison.OrdinalIgnoreCase);
            labels.TryGetValue(table.TableName, out var binding);
            var plcColumn = controllers ? "Record" : "PLC";
            var fields = table.Columns.Select(c => c.Name).Where(IsAddressField).ToList();
            if (!controllers && (binding == null || Excluded.Contains(binding.Title) || Excluded.Contains(bare) || fields.Count == 0)) continue;
            if (!table.Columns.Any(c => c.Name.Equals(plcColumn, StringComparison.OrdinalIgnoreCase))) continue;

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
                        var classNumber = ProjectClassCatalog.Read(row, "Class") ?? ProjectClassCatalog.Read(row, "ClassNumber");
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
                        var kind = field.Replace("_", "").ToUpperInvariant();
                        var signal = AnalogArea(title) ?? AnalogArea(bare);
                        var area = kind switch
                        {
                            "ADDRESSINPUT" => 'I', "ADDRESSOUTPUT" or "ADDRESSLIFTUP" or "ADDRESSLIFTDOWN" => 'Q',
                            _ when kind.StartsWith("ADDRESSFEEDBACK", StringComparison.Ordinal) => 'I',
                            _ => signal
                        };
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

    internal static bool IsAddressField(string name)
    {
        var normalized = name.Replace("_", "").ToUpperInvariant();
        return normalized is "ADDRESS" or "ADDRESSINPUT" or "ADDRESSOUTPUT" or "ADDRESSLIFTUP" or "ADDRESSLIFTDOWN" or "ADDRESSMS"
            || normalized.StartsWith("ADDRESSFEEDBACK", StringComparison.Ordinal);
    }
}

