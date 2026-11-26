#nullable enable
using Configurator.Core.Projects;
using Configurator.Core.Workspace;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    internal static IReadOnlyList<RecordCheckScope> LocalScopes(OfflineProjectDataSource source,
        IReadOnlyList<RecordCheckClass> classes, CancellationToken ct)
    {
        var scopes = new List<RecordCheckScope>();
        foreach (var group in classes)
        {
            var controllers = LocalRows(source, group, ct).Select(row => row.Plc).Distinct();
            scopes.AddRange(controllers.Select(plc => new RecordCheckScope(group, plc)));
        }
        return scopes;
    }

    internal static RecordCheckResult CheckLocal(OfflineProjectDataSource source, RecordCheckScope scope, CancellationToken ct)
    {
        var occupied = new SortedSet<int>();
        foreach (var row in LocalRows(source, scope.Class, ct))
        {
            if (!Equals(row.Plc, scope.Plc)) continue;
            var raw = row.Record.ToString();
            object value = row.Record.GetValueKind() == JsonValueKind.Number &&
                decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) ? numeric : raw;
            if (!DatabaseService.TryReadIntegralValue(value, out var number))
                throw new InvalidOperationException($"В {scope.Class.TableName} найдены значения Record вне диапазона целых чисел Int32.");
            occupied.Add(number);
        }
        return RecordCheckResult.FromOccupied(occupied);
    }

    private static IEnumerable<(string Plc, JsonNode Record)> LocalRows(OfflineProjectDataSource source,
        RecordCheckClass group, CancellationToken ct)
    {
        foreach (var row in source.GetRecords(group.TableName))
        {
            ct.ThrowIfCancellationRequested();
            var plc = ProjectClassCatalog.Read(row, group.PlcColumn);
            if (string.IsNullOrWhiteSpace(plc) || ProjectClassCatalog.ReadNode(row, group.RecordColumn) is not { } record) continue;
            if (group.ClassColumn != null && ProjectClassCatalog.Read(row, group.ClassColumn) != group.ClassValue) continue;
            yield return (plc, record);
        }
    }

    private static string? FindColumn(ProjectTableSchema table, string name) =>
        table.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name;
}
