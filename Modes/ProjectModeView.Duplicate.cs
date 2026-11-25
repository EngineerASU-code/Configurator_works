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
using Configurator.Core.Workspace;

namespace Configurator;

public partial class ProjectModeView
{
    private const string ObjectClipboardFormat = "GASS.Configurator.Mode3.Object";
    private (Guid Token, string ClassName, string Table, int Generation, DataRow Row)? _copiedObject;

    private void CopySelectedObject()
    {
        if (_selectedRow == null || ObjectList.SelectedItems.Count != 1 || _selectedClass?.PrimaryStorage == null) return;
        try
        {
            var snapshot = _selectedRow.Table.Clone();
            snapshot.ImportRow(_selectedRow);
            var token = Guid.NewGuid();
            Clipboard.SetData(ObjectClipboardFormat, token.ToString());
            _copiedObject = (token, _selectedClass.Name, _selectedClass.PrimaryStorage.TableName, _workspaceGeneration, snapshot.Rows[0]);
            ShowActionFeedback($"Скопирован объект класса {_selectedClass.Name}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowActionFeedback("Буфер обмена занят", isError: true);
        }
    }

    private async void PasteCopiedObject()
    {
        if (_copiedObject is not { } copy || _selectedClass?.PrimaryStorage == null) return;
        try
        {
            if (!Equals(Clipboard.GetData(ObjectClipboardFormat), copy.Token.ToString())) return;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowActionFeedback("Буфер обмена занят", isError: true);
            return;
        }
        if (copy.Generation != _workspaceGeneration || copy.ClassName != _selectedClass.Name ||
            copy.Table != _selectedClass.PrimaryStorage.TableName)
        {
            ShowActionFeedback($"Вставка доступна только в исходном проекте и классе {copy.ClassName}", isError: true);
            return;
        }

        await DuplicateObjectAsync(copy.Row);
    }

    private async Task DuplicateObjectAsync(DataRow sourceRow)
    {
        if (!BtnNewObject.IsEnabled || !UserAuthorizationService.Require(Permission.AddData)) return;
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
            var keys = offlineSource != null ? offlineSource.GetSchema(tableName)?.KeyColumns ?? new List<string>()
                : await db!.GetPrimaryKeyColumnsAsync(tableName);
            if (keys.Count == 0) throw new InvalidOperationException("Не удалось определить ключ копии.");
            var recordScopeClass = ProjectClassCatalog.FindRecordClassColumn(keys);

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
                    nextRecord = await db!.GetFreeScopedRecordValueAsync(tableName, recordColumn.Name, plcColumn?.Name,
                        plcColumn == null ? null : sourceRow[plcColumn.Name], default,
                        recordScopeClass, recordScopeClass == null ? null : sourceRow[recordScopeClass]);
                }
            }

            var values = Mode3DuplicateValues.Build(sourceRow, columns, allRows, nextRecord, recordScopeClass);
            if (generation != _workspaceGeneration) return;
            if (_configurationKind != ConfigurationKind.Class || _selectedClass != selectedClass) return;
            if (values.TryGetValue("Record", out var suggested))
            {
                var dialog = new TextInputWindow("Вставка объекта", "Record нового объекта",
                    Convert.ToString(suggested, CultureInfo.InvariantCulture) ?? "") { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() != true) return;
                if (!int.TryParse(dialog.Result, NumberStyles.None, CultureInfo.InvariantCulture, out var record))
                {
                    ShowActionFeedback("Record должен быть целым неотрицательным числом", isError: true);
                    return;
                }
                values["Record"] = Convert.ChangeType(record, sourceRow.Table.Columns["Record"]!.DataType, CultureInfo.InvariantCulture);
            }
            if (generation != _workspaceGeneration || _configurationKind != ConfigurationKind.Class || _selectedClass != selectedClass) return;
            var inserted = (await ((IProjectBatchCrudDataSource)crud).CreateRecordsAsync(tableName, new[] { values })).Single();
            var snapshot = inserted.ToDictionary(pair => pair.Key, pair => pair.Value ?? DBNull.Value, StringComparer.OrdinalIgnoreCase);
            if (generation != _workspaceGeneration) return;
            var key = SelectIdentity(snapshot, keys);
            var targetName = PickName(ToDataRow(inserted, sourceRow), selectedClass);
            var description = DescribeDuplicate(sourceName, targetName, snapshot);
            RecordMode3Mutation(tableName, ChangeJournalOperationKind.Duplicate, description, source, snapshot,
                SelectIdentity(source, keys), key, generation);
            await LoadObjectsAsync();
            if (generation != _workspaceGeneration) return;
            await SelectDuplicatedObjectAsync(key.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase), tableName, workspace, generation);
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
        IEnumerable<IReadOnlyDictionary<string, object?>> allRows, int? nextRecord = null, string? classColumn = null)
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
        var suggestion = new ScopedRecordSuggestion();
        foreach (var row in allRows)
        {
            if (plcColumn != null && !SameScopeValue(source[plcColumn], row.GetValueOrDefault(plcColumn.ColumnName))) continue;
            if (classColumn != null && !SameScopeValue(source[classColumn], row.GetValueOrDefault(classColumn))) continue;
            if (row.TryGetValue(recordColumn.Name, out var value)) suggestion.Add(value);
        }
        var next = suggestion.GetValue();
        values[recordColumn.Name] = recordColumn.DataType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ||
            recordColumn.DataType.Equals("varchar", StringComparison.OrdinalIgnoreCase)
            ? next.ToString(CultureInfo.InvariantCulture)
            : Convert.ChangeType(next, source.Table.Columns[recordColumn.Name]!.DataType, CultureInfo.InvariantCulture);
        return values;
    }

    internal static bool SameScopeValue(object? left, object? right)
    {
        var leftText = left == null || left == DBNull.Value ? null : Convert.ToString(left, CultureInfo.InvariantCulture)?.Trim();
        var rightText = right == null || right == DBNull.Value ? null : Convert.ToString(right, CultureInfo.InvariantCulture)?.Trim();
        return decimal.TryParse(leftText, NumberStyles.Number, CultureInfo.InvariantCulture, out var first) &&
            decimal.TryParse(rightText, NumberStyles.Number, CultureInfo.InvariantCulture, out var second)
            ? first == second
            : string.Equals(leftText, rightText, StringComparison.OrdinalIgnoreCase);
    }
}
