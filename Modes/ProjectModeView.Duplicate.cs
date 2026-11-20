#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Configurator.Core.Projects;

namespace Configurator;

public partial class ProjectModeView
{
    private async void DuplicateObject_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Require(Permission.AddData) || _selectedRow == null || _selectedClass?.PrimaryStorage == null) return;
        var sourceRow = _selectedRow;
        var selectedClass = _selectedClass;
        var offlineProject = _offlineProject;
        var offlineSource = _offlineSource;
        var generation = _workspaceGeneration;
        try
        {
            var crud = RequireCrudSource();
            var workspace = RequireWorkspace();
            var source = SnapshotRow(sourceRow);
            var sourceName = PickName(sourceRow, selectedClass);
            var tableName = selectedClass.PrimaryStorage.TableName;
            var db = offlineProject == null ? new DatabaseService(_connection.ToConnectionString(_selectedDatabase)) : null;
            var columns = await ReadObjectColumnsAsync(tableName, db!);

            IEnumerable<IReadOnlyDictionary<string, object?>> allRows = Array.Empty<IReadOnlyDictionary<string, object?>>();
            int? nextRecord = null;
            if (offlineProject != null)
            {
                var localTable = offlineSource!.ResolveTableName(tableName);
                allRows = offlineProject.LocalRecords.Where(r => r.TableName.Equals(localTable, StringComparison.OrdinalIgnoreCase))
                    .Select(r => (IReadOnlyDictionary<string, object?>)r.Values.ToDictionary(
                        pair => pair.Key, pair => (object?)pair.Value?.ToString(), StringComparer.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                var recordColumn = columns.FirstOrDefault(column => column.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));
                if (recordColumn != null)
                {
                    var plcColumn = columns.FirstOrDefault(column => column.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase));
                    nextRecord = plcColumn == null
                        ? checked(await db!.GetMaxRecordValueAsync(tableName, recordColumn.Name) + 1)
                        : await db!.GetNextScopedRecordValueAsync(tableName, recordColumn.Name, plcColumn.Name,
                            sourceRow[plcColumn.Name] == DBNull.Value ? DBNull.Value : sourceRow[plcColumn.Name]);
                }
            }

            var values = Mode3DuplicateValues.Build(sourceRow, columns, allRows, nextRecord);
            if (crud is not IProjectCreateWithKeyDataSource keyed)
                throw new NotSupportedException("Источник проекта не может вернуть ключ созданной записи.");
            var previousKeys = offlineProject == null ? new HashSet<string>(StringComparer.Ordinal) : CaptureOfflineRecordKeys(tableName);
            if (generation != _workspaceGeneration) return;
            var key = await keyed.CreateRecordAndGetKeyAsync(tableName, values);
            if (generation != _workspaceGeneration) return;
            foreach (var pair in key) values[pair.Key] = pair.Value;
            var targetName = PickName(ToDataRow(values, sourceRow), selectedClass);
            var duplicateValues = values.ToDictionary(pair => pair.Key,
                pair => pair.Value ?? (object)DBNull.Value, StringComparer.OrdinalIgnoreCase);
            var description = DescribeDuplicate(sourceName, targetName, duplicateValues);
            RecordCreatedMode3Object(tableName, values, previousKeys, description, generation,
                ChangeJournalOperationKind.Duplicate, source);
            await LoadObjectsAsync();
            if (generation != _workspaceGeneration) return;
            await SelectDuplicatedObjectAsync(key, tableName, workspace, generation);
            if (generation != _workspaceGeneration) return;
            TxtStatus.Text = offlineProject == null ? "Объект продублирован" : "Объект скопирован в локальном проекте";
            ShowActionFeedback(description);
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Дублирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SelectDuplicatedObjectAsync(IReadOnlyDictionary<string, object?> key, string tableName,
        IProjectWorkspace workspace, int generation)
    {
        if (key.Count == 0 || key.Keys.Any(column => !_objects.Columns.Contains(column))) return;
        CmbPlcFilter.SelectedIndex = 0;
        CmbAreaFilter.SelectedIndex = 0;
        if (!_objects.Rows.Cast<DataRow>().Any(row => HasKey(row, key)))
        {
            var page = await workspace.ReadPageAsync(new ProjectReadQuery
            {
                TableName = tableName,
                PageSize = 1,
                Filters = key.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            });
            if (generation != _workspaceGeneration || page.Items.Count != 1 || !HasKey(page.Items[0], key)) return;
            var record = page.Items[0];
            var created = _objects.NewRow();
            foreach (DataColumn column in _objects.Columns)
            {
                if (!record.Values.TryGetPropertyValue(column.ColumnName, out var node) || node == null)
                {
                    created[column] = DBNull.Value;
                    continue;
                }
                created[column] = JsonSerializer.Deserialize(node.ToJsonString(), column.DataType) ?? DBNull.Value;
            }
            _objects.Rows.Add(created);
            RebuildObjectFilters();
        }
        ApplyObjectFilters();
        var match = _objects.Rows.Cast<DataRow>().FirstOrDefault(row => HasKey(row, key));
        if (match == null) return;
        var rowIndex = _objects.Rows.IndexOf(match);
        var item = ObjectList.Items.OfType<ObjectItem>().FirstOrDefault(entry => entry.RowIndex == rowIndex);
        if (item == null) return;
        ObjectList.SelectedItem = item;
        ObjectList.ScrollIntoView(item);
    }

    private static bool HasKey(DataRow row, IReadOnlyDictionary<string, object?> key) => key.All(pair =>
        string.Equals(Convert.ToString(row[pair.Key], CultureInfo.InvariantCulture),
            Convert.ToString(pair.Value, CultureInfo.InvariantCulture), StringComparison.Ordinal));

    private static bool HasKey(ProjectRecord record, IReadOnlyDictionary<string, object?> key) => key.All(pair =>
        record.Values.TryGetPropertyValue(pair.Key, out var node) && string.Equals(
            node?.ToString(), Convert.ToString(pair.Value, CultureInfo.InvariantCulture), StringComparison.Ordinal));

    private static DataRow ToDataRow(IReadOnlyDictionary<string, object?> values, DataRow source)
    {
        var table = source.Table;
        var row = table.NewRow();
        foreach (DataColumn column in table.Columns) row[column] = source[column];
        foreach (var pair in values)
            if (table.Columns.Contains(pair.Key)) row[pair.Key] = pair.Value ?? DBNull.Value;
        return row;
    }
}

internal static class Mode3DuplicateValues
{
    internal static Dictionary<string, object?> Build(DataRow source, IReadOnlyList<ColumnInfo> columns,
        IEnumerable<IReadOnlyDictionary<string, object?>> allRows, int? nextRecord = null)
    {
        var writable = columns.Where(column => !column.IsIdentity && !column.IsComputed && !column.IsReadOnly &&
                source.Table.Columns.Contains(column.Name))
            .ToList();
        var values = writable.ToDictionary(column => column.Name,
            column => source[column.Name] == DBNull.Value ? null : source[column.Name], StringComparer.OrdinalIgnoreCase);
        var recordColumn = writable.FirstOrDefault(column => column.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));
        if (recordColumn == null) return values;

        if (nextRecord.HasValue)
        {
            values[recordColumn.Name] = Convert.ChangeType(nextRecord.Value,
                source.Table.Columns[recordColumn.Name]!.DataType, CultureInfo.InvariantCulture);
            return values;
        }

        var plcColumn = source.Table.Columns.Cast<DataColumn>().FirstOrDefault(column => column.ColumnName.Equals("PLC", StringComparison.OrdinalIgnoreCase));
        var sourcePlc = plcColumn == null || source[plcColumn] == DBNull.Value ? null : Convert.ToString(source[plcColumn], CultureInfo.InvariantCulture);
        long max = 0;
        foreach (var row in allRows)
        {
            if (plcColumn != null && !string.Equals(sourcePlc,
                    row.TryGetValue(plcColumn.ColumnName, out var plc) && plc != null && plc != DBNull.Value
                        ? Convert.ToString(plc, CultureInfo.InvariantCulture) : null,
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (row.TryGetValue(recordColumn.Name, out var value) && long.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var record))
                max = Math.Max(max, record);
        }
        var next = checked(max + 1);
        values[recordColumn.Name] = recordColumn.DataType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ||
            recordColumn.DataType.Equals("varchar", StringComparison.OrdinalIgnoreCase)
            ? next.ToString(CultureInfo.InvariantCulture)
            : Convert.ChangeType(next, source.Table.Columns[recordColumn.Name]!.DataType, CultureInfo.InvariantCulture);
        return values;
    }
}
