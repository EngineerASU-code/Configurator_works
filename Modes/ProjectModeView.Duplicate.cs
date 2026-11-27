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

    private bool SupportsObjectCopy => _configurationKind is ConfigurationKind.Plc or ConfigurationKind.Area ||
        (_configurationKind == ConfigurationKind.Class && _selectedClass?.PrimaryStorage != null);
    private string CopyTable => _configurationKind == ConfigurationKind.Class ? _selectedClass.PrimaryStorage.TableName : _configurationTableName;
    private string CopyClass => _configurationKind == ConfigurationKind.Class ? _selectedClass.Name : _configurationKind.ToString();

    private Task DuplicateSelectionAsync(DataRow row) => _configurationKind == ConfigurationKind.Class
        ? DuplicateObjectAsync(new[] { row }) : AddConfigurationValueAsync(row);

    private Task DuplicateListAsync()
    {
        if (_configurationKind != ConfigurationKind.Class) return DuplicateSelectionAsync(_selectedRow);
        var rows = ObjectList.SelectedItems.Cast<ObjectItem>().Select(item => _objects.Rows[item.RowIndex]).ToArray();
        return DuplicateObjectAsync(rows);
    }

    private void CopySelectedObject()
    {
        if (_selectedRow == null || ObjectList.SelectedItems.Count != 1 || !SupportsObjectCopy) return;
        try
        {
            var snapshot = _selectedRow.Table.Clone();
            snapshot.ImportRow(_selectedRow);
            var token = Guid.NewGuid();
            Clipboard.SetData(ObjectClipboardFormat, token.ToString());
            _copiedObject = (token, CopyClass, CopyTable, _workspaceGeneration, snapshot.Rows[0]);
            ShowActionFeedback($"Скопирован объект {CopyClass}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowActionFeedback("Буфер обмена занят", isError: true);
        }
    }

    private async void PasteCopiedObject()
    {
        if (_copiedObject is not { } copy || !SupportsObjectCopy || !BtnNewObject.IsEnabled) return;
        try
        {
            if (!Equals(Clipboard.GetData(ObjectClipboardFormat), copy.Token.ToString())) return;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowActionFeedback("Буфер обмена занят", isError: true);
            return;
        }
        if (copy.Generation != _workspaceGeneration || copy.ClassName != CopyClass ||
            copy.Table != CopyTable)
        {
            ShowActionFeedback($"Вставка доступна только в исходном проекте и классе {copy.ClassName}", isError: true);
            return;
        }

        await DuplicateSelectionAsync(copy.Row);
    }

    private async Task DuplicateObjectAsync(DataRow[] sources)
    {
        if (sources.Length == 0 || !BtnNewObject.IsEnabled || !UserAuthorizationService.Require(Permission.AddData)) return;
        var selectedClass = _selectedClass;
        var offlineProject = _offlineProject;
        var offlineSource = _offlineSource;
        var generation = _workspaceGeneration;
        try
        {
            var crud = RequireCrudSource();
            var workspace = RequireWorkspace();
            var tableName = selectedClass.PrimaryStorage.TableName;
            var db = offlineProject == null ? new DatabaseService(_connection.ToConnectionString(_selectedDatabase)) : null;
            var columns = await ReadObjectColumnsAsync(tableName, db!);
            var keys = offlineSource != null ? offlineSource.GetSchema(tableName)?.KeyColumns ?? new List<string>()
                : await db!.GetPrimaryKeyColumnsAsync(tableName);
            bool legacy = (keys.Count == 0 || offlineSource?.GetSchema(tableName)?.HasStableKey == false) && tableName.Equals("dbo.Valves2S", StringComparison.OrdinalIgnoreCase);
            if (legacy) keys = new List<string> { "PLC", "PLC_Class_Number", "Record" };
            if (keys.Count == 0) throw new InvalidOperationException("Не удалось определить ключ копии.");
            var classColumn = ProjectClassCatalog.FindRecordClassColumn(keys);
            bool Current() => generation == _workspaceGeneration && _selectedClass == selectedClass && _configurationKind == ConfigurationKind.Class;
            var occupied = new List<IReadOnlyDictionary<string, object?>>();
            if (columns.Any(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase)))
            {
                var records = await AssemblyRowsAsync(workspace, tableName, new(), Current);
                foreach (var record in records)
                    occupied.Add(record.Values.ToDictionary(p => p.Key, p => (object?)p.Value?.ToString(), StringComparer.OrdinalIgnoreCase));
            }
            if (!Current()) return;
            var existing = occupied.ToArray();
            var lookups = await LoadObjectLookupOptionsAsync(selectedClass,columns,db);
            if (!Current()) return;
            var fixedClass = classColumn ?? columns.FirstOrDefault(c => new[] { "PLCClassNumber", "PLC_Class_Number", "PLCClasssNumber" }
                .Contains(c.Name,StringComparer.OrdinalIgnoreCase))?.Name;
            var drafts = new DataTable();
            foreach (var row in sources)
            {
                var values = Mode3DuplicateValues.Build(row, columns, occupied, classColumn: classColumn);
                if (fixedClass != null && values.ContainsKey(fixedClass))
                    values[fixedClass] = Convert.ChangeType(selectedClass.ClassNumber,row.Table.Columns[fixedClass]!.DataType,CultureInfo.InvariantCulture);
                occupied.Add(values);
                if (drafts.Columns.Count == 0)
                    foreach (var field in values.Keys) drafts.Columns.Add(field, row.Table.Columns[field]!.DataType);
                var draft = drafts.NewRow();
                foreach (var field in values) draft[field.Key] = field.Value ?? DBNull.Value;
                drafts.Rows.Add(draft);
            }
            foreach (DataColumn column in drafts.Columns)
                column.ReadOnly = column.ColumnName.Equals(fixedClass,StringComparison.OrdinalIgnoreCase) ||
                    column.ColumnName.Equals("Area",StringComparison.OrdinalIgnoreCase);
            string ValidateDrafts()
            {
                var reserved = existing.ToList();
                foreach (DataRow draft in drafts.Rows)
                {
                    foreach (var column in columns.Where(c => drafts.Columns.Contains(c.Name)))
                    {
                        var value = draft[column.Name];
                        if (value == DBNull.Value && !column.IsNullable) return $"Укажите значение {column.Name} для копии {drafts.Rows.IndexOf(draft)+1}.";
                        if (lookups.TryGetValue(column.Name,out var choices) && value != DBNull.Value &&
                            !Equals(value,sources[drafts.Rows.IndexOf(draft)][column.Name]) &&
                            !choices.Any(c => Mode3DuplicateValues.SameScopeValue(c.Key,value)))
                            return $"Выберите {column.Name} из справочника (копия {drafts.Rows.IndexOf(draft)+1}).";
                        if (InputPlaceholder.IsFlag(column) && value != DBNull.Value && !Equals(value,sources[drafts.Rows.IndexOf(draft)][column.Name]) &&
                            value is not bool && !new[] { "0", "1" }.Contains(Convert.ToString(value)))
                            return $"Поле {column.Name} принимает 0 или 1.";
                        if (value is string text && column.MaxLength > 0)
                        {
                            int limit = column.DataType.ToLowerInvariant() is "nvarchar" or "nchar" ? column.MaxLength / 2 : column.MaxLength;
                            if (text.Length > limit) return $"{column.Name}: максимум {limit} символов (копия {drafts.Rows.IndexOf(draft)+1}).";
                        }
                    }
                    if (!drafts.Columns.Contains("Record")) continue;
                    if (!int.TryParse(Convert.ToString(draft["Record"]), NumberStyles.None, CultureInfo.InvariantCulture,out _))
                        return "Record должен быть целым неотрицательным числом.";
                    var scope = new[] { "PLC", classColumn, "Record" }.Where(k => k != null && drafts.Columns.Contains(k)).ToArray();
                    if (reserved.Any(r => scope.All(k => Mode3DuplicateValues.SameScopeValue(r.GetValueOrDefault(k!), draft[k!]))))
                        return $"Record {draft["Record"]} уже занят в выбранном контексте (копия {drafts.Rows.IndexOf(draft)+1}).";
                    reserved.Add(scope.ToDictionary(k => k!, k => (object?)draft[k!],StringComparer.OrdinalIgnoreCase));
                }
                return "";
            }
            bool analogInput = selectedClass.Name.Equals("AI", StringComparison.OrdinalIgnoreCase) || tableName.Equals("dbo.AnaIN", StringComparison.OrdinalIgnoreCase);
            bool analogOutput = selectedClass.Name.Equals("AO", StringComparison.OrdinalIgnoreCase) || tableName.Equals("dbo.AnaOUT", StringComparison.OrdinalIgnoreCase);
            bool isInput = analogInput || selectedClass.Name.Equals("DI",StringComparison.OrdinalIgnoreCase) || tableName.Equals("dbo.DiscretIN",StringComparison.OrdinalIgnoreCase);
            bool isOutput = analogOutput || selectedClass.Name.Equals("DO",StringComparison.OrdinalIgnoreCase) || tableName.Equals("dbo.DiscretOUT",StringComparison.OrdinalIgnoreCase);
            var addressColumn = drafts.Columns.Cast<DataColumn>().FirstOrDefault(c =>
                !c.ReadOnly && (analogInput || analogOutput || c.DataType == typeof(string)) && new[] { "Address", "Adress", isInput ? "Address_Input" : "Address_Output" }.Contains(c.ColumnName, StringComparer.OrdinalIgnoreCase));
            Func<Task> assignAddresses = null!;
            if (addressColumn != null && drafts.Columns.Contains("PLC") && (isInput || isOutput))
            {
                assignAddresses = async () =>
                {
                    var maps = await PlcAddressCatalog.LoadAsync(workspace, default);
                    if (!Current()) throw new InvalidOperationException("Выбранный проект изменился.");
                    Mode3DuplicateValues.AssignAddresses(drafts, addressColumn, maps, isInput ? 'I' : 'Q', analogInput || analogOutput);
                };
            }
            if (!DuplicateDraftWindow.Edit(Window.GetWindow(this), selectedClass.Name, drafts, sources, ValidateDrafts, lookups, assignAddresses, addressColumn?.ColumnName, isInput) || !Current() ||
                !UserAuthorizationService.Require(Permission.AddData)) return;
            var batch = drafts.Rows.Cast<DataRow>().Select(row => drafts.Columns.Cast<DataColumn>().ToDictionary(
                c => c.ColumnName, c => row.IsNull(c) ? null : row[c], StringComparer.OrdinalIgnoreCase)).ToList();
            if (workspace.IsReadOnly) throw new InvalidOperationException("Проект открыт только для чтения.");
            var inserted = await ((IProjectBatchCrudDataSource)crud).CreateRecordsAsync(tableName, batch);
            if (!Current()) return;
            var description = $"Создано копий {selectedClass.Name}: {inserted.Count}";
            var changes = DuplicateJournalChanges(tableName, inserted, keys);
            var key = changes[0].IdentityAfter.ToDictionary(p => p.Key, p => p.Value.ToObject());
            PublishMode3Mutation(tableName, ChangeJournalOperationKind.Duplicate, description, changes, key,
                string.Join(", ", keys) + $" · {changes.Count} записей", true);
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

    internal static List<ChangeJournalRowChange> DuplicateJournalChanges(string table,
        IReadOnlyList<IReadOnlyDictionary<string,object?>> inserted, IReadOnlyList<string> keys)
    {
        return inserted.Select(row =>
        {
            var snapshot = row.ToDictionary(p => p.Key, p => p.Value ?? DBNull.Value, StringComparer.OrdinalIgnoreCase);
            return new ChangeJournalRowChange
            {
                TableName = table, Kind = ChangeJournalOperationKind.Duplicate,
                After = JournalValues(snapshot), IdentityAfter = JournalValues(SelectIdentity(snapshot, keys)),
                AfterIsCompleteOfflineRecord = true
            };
        }).ToList();
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
    internal static void AssignAddresses(DataTable drafts, DataColumn address, IReadOnlyDictionary<string,PlcAddressMap> maps, char area, bool analog = false)
    {
        var plan = new List<(DataRow Row, string Address)>();
        foreach (var group in drafts.Rows.Cast<DataRow>().GroupBy(r => Convert.ToString(r["PLC"], CultureInfo.InvariantCulture)))
        {
            if (!int.TryParse(group.Key, out var plc) || plc < 0)
                throw new InvalidOperationException("Для каждой копии выберите PLC.");
            var map = maps.TryGetValue(plc.ToString(CultureInfo.InvariantCulture), out var found) ? found : new PlcAddressMap();
            var rows = group.ToArray();
            var free = map.FreeAddresses(area, bit: !analog, size: analog ? 2 : 1, numeric: analog && address.DataType != typeof(string), limit: rows.Length, aligned: analog);
            if (free.Count != rows.Length)
                throw new InvalidOperationException($"PLC {plc}: недостаточно подтверждённых свободных {area}-адресов. Проверьте карту адресов.");
            for (int i = 0; i < rows.Length; i++) plan.Add((rows[i],free[i]));
        }
        foreach (var item in plan) item.Row[address] = item.Address;
    }

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
