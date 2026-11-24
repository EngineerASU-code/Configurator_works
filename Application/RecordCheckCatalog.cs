#nullable enable
using Configurator.Core.Projects;

namespace Configurator;

internal sealed record RecordCheckClass(string Title, string TableName, string PlcColumn, string RecordColumn,
    string? ClassColumn = null, string? ClassValue = null);

internal sealed record RecordCheckScope(RecordCheckClass Class, object Plc);

internal static class RecordCheckCatalog
{
    internal static async Task<IReadOnlyList<RecordCheckClass>> LoadAsync(IProjectWorkspace workspace, CancellationToken ct)
    {
        var tables = await workspace.GetTablesAsync(ct).ConfigureAwait(false);
        var labels = await ProjectClassCatalog.ReadTablesAsync(workspace, tables, ct).ConfigureAwait(false);
        var classes = new List<RecordCheckClass>();
        foreach (var table in tables)
        {
            if (!labels.TryGetValue(table.TableName, out var title)) continue;
            var plcColumn = FindColumn(table, "PLC");
            var recordColumn = FindColumn(table, "Record");
            if (plcColumn == null || recordColumn == null) continue;
            var classColumn = FindColumn(table, "Class") ?? FindColumn(table, "ClassNumber");
            if (title.SharedClasses is { Count: > 0 } namedClasses && classColumn != null)
            {
                foreach (var (value, name) in namedClasses)
                    classes.Add(new RecordCheckClass(name, table.TableName, plcColumn, recordColumn, classColumn, value));
            }
            else
                classes.Add(new RecordCheckClass(title.Title, table.TableName, plcColumn, recordColumn));
        }
        return classes.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.TableName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindColumn(ProjectTableSchema table, string name) =>
        table.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name;
}
