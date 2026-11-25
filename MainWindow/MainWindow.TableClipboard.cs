using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Configurator.Core.Projects;
using Configurator.Infrastructure;

namespace Configurator;

public partial class MainWindow
{
    private bool _tablePasteInProgress;

    private async void DataTableGrid_ClipboardKeyDown(object sender, KeyEventArgs e)
    {
        if (_currentMode == 1 && e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None &&
            sender is DataGrid tableGrid && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(tableGrid, source) is DataGridRow)
        {
            e.Handled = true;
            if (!e.IsRepeat) await DeleteSelectedTableRowsAsync();
            return;
        }

        if (_currentMode == 1 && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D &&
            sender is DataGrid duplicateGrid && e.OriginalSource is DependencyObject duplicateSource &&
            ItemsControl.ContainerFromElement(duplicateGrid, duplicateSource) is DataGridRow)
        {
            e.Handled = true;
            if (!e.IsRepeat && !_tablePasteInProgress && !_tableDeleteInProgress &&
                duplicateGrid.SelectedItems.Count == 1 && _rightPanel.BtnDuplicate.IsEnabled)
                BtnDuplicate_Click(sender, e);
            return;
        }

        if (_currentMode != 1 || Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.C or Key.V))
            return;
        e.Handled = true;
        await UseTableClipboardAsync((DataGrid)sender, e.Key);
    }

    private void OpenTableRowMenu(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (_currentMode != 1 || e.OriginalSource is not DependencyObject source ||
            FindVisualParent<DataGridCell>(source) is not { } cell ||
            ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row) return;
        e.Handled = true;
        if (!row.IsSelected) grid.SelectedItem = row.Item;
        grid.CurrentCell = new DataGridCellInfo(row.Item, cell.Column);
        var menu = new ContextMenu { PlacementTarget = cell, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        bool ready = !_tableDeleteInProgress && !_tablePasteInProgress;
        void Add(string title, bool enabled, RoutedEventHandler action)
        {
            var item = new MenuItem { Header = title, IsEnabled = ready && enabled };
            item.Click += action;
            menu.Items.Add(item);
        }
        Add("Добавить", _rightPanel.BtnAdd.IsEnabled, BtnAdd_Click);
        Add("Дублировать", grid.SelectedItems.Count == 1 && _rightPanel.BtnDuplicate.IsEnabled, BtnDuplicate_Click);
        Add("Копировать", grid.SelectedItems.Count > 0, async (_, _) => await UseTableClipboardAsync(grid, Key.C));
        Add("Вставить", _rightPanel.BtnAdd.IsEnabled || _rightPanel.BtnUpdate.IsEnabled, async (_, _) => await UseTableClipboardAsync(grid, Key.V));
        Add("Удалить", _rightPanel.BtnDelete.IsEnabled, BtnDelete_Click);
        menu.IsOpen = true;
    }

    private async Task UseTableClipboardAsync(DataGrid grid, Key key)
    {
        if (!UserAuthorizationService.Has(Permission.Mode1View) || _tablePasteInProgress ||
            (key == Key.V && _tableDeleteInProgress) ||
            grid.ItemsSource is not DataView view)
            return;

        var visible = grid.Columns.Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex).ToList();
        var names = visible.Select(c => c.SortMemberPath).ToArray();
        try
        {
            if (key == Key.C)
            {
                var selected = grid.SelectedItems.OfType<DataRowView>().ToHashSet();
                if (selected.Count > 0)
                    Clipboard.SetText(TableClipboard.Write(grid.Items.OfType<DataRowView>()
                        .Where(selected.Contains).Select(row => names.Select(name => row[name]))));
                return;
            }

            _tablePasteInProgress = true;
            if (!UserAuthorizationService.Has(Permission.Mode1Add) && !UserAuthorizationService.Has(Permission.Mode1Edit))
                throw new InvalidOperationException("У вас нет прав на добавление или изменение строк.");
            var cells = TableClipboard.Read(Clipboard.GetText());
            int start = cells[0].Length == names.Length ? 0 : Math.Max(0, visible.IndexOf(grid.CurrentColumn));
            if (cells[0].Length > names.Length - start)
                throw new FormatException("Блок из буфера не помещается. Выберите первый столбец диапазона.");
            await PasteTableRowsAsync(view.Table, names.Skip(start).Take(cells[0].Length).ToArray(), cells);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            var message = ex is FormatException or InvalidOperationException
                ? ex.Message : "Проверьте значения, ограничения таблицы и доступ к источнику данных.";
            ModernDialog.Show($"Операция с буфером обмена не завершена. {message}",
                "Буфер обмена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (key == Key.V) _tablePasteInProgress = false;
        }
    }

    private async Task PasteTableRowsAsync(DataTable data, string[] names, List<string[]> cells)
    {
        var offline = _isOfflineWorkspace;
        var vm = _activeVm;
        var dbName = _activeDbName;
        var table = offline ? _offlineCurrentTableName : vm?.CurrentTable;
        var columns = offline ? _offlineCurrentColumns : vm?.Columns;
        if (string.IsNullOrEmpty(table) || columns == null || (!offline && string.IsNullOrEmpty(dbName))) return;
        Configurator.Core.Projects.ProjectWriteGuard.EnsureDataTableWritable(table);
        var primaryKey = offline
            ? _openedLocalProject?.TableSchemas.FirstOrDefault(s => s.TableName.Equals(table, StringComparison.OrdinalIgnoreCase))?.KeyColumns?.ToArray()
                ?? Array.Empty<string>()
            : vm.KeyColumns.ToArray();
        var record = columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));
        if (record == null || !names.Contains(record.Name, StringComparer.OrdinalIgnoreCase))
            throw new FormatException("Диапазон для вставки должен включать столбец Record. Заголовки копировать не нужно.");

        var scope = columns.Where(c => c == record ||
            ((c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("Class", StringComparison.OrdinalIgnoreCase) ||
              IsClassNumberColumn(c)) &&
             (primaryKey.Contains(c.Name, StringComparer.OrdinalIgnoreCase) ||
              (primaryKey.Length == 0 && c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase))))).ToArray();
        var mapped = names.Select(name => columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var forcedClass = ResolveSystemClassNumber(table);
        var className = forcedClass.HasValue ? ResolveClassNumberColumn(columns) : null;
        bool canAdd = UserAuthorizationService.Has(Permission.Mode1Add);
        bool canEdit = UserAuthorizationService.Has(Permission.Mode1Edit);
        var database = offline ? null : new DatabaseService(_connection.ToConnectionString(dbName));
        var plan = new List<(Dictionary<string, object> Key, Dictionary<string, object> Values, Dictionary<string, object> Expected)>();
        var offlineTargets = new List<DataRow>();

        var conflicts = new List<PasteConflict>();
        var reserved = new List<Dictionary<string, object>>();
        var plannedOrder = new List<int>();

        bool Reserved(Dictionary<string, object> candidate) =>
            reserved.Any(key => candidate.All(field => PasteKeyEquals(key[field.Key], field.Value)));

        async Task<(int Count, DataRow Local, Dictionary<string, object> Online)> Find(Dictionary<string, object> key)
        {
            if (offline)
            {
                var rows = data.AsEnumerable().Where(row => key.All(field => PasteKeyEquals(row[field.Key], field.Value))).Take(2).ToList();
                return (rows.Count, rows.FirstOrDefault(), null);
            }
            var matches = await database.FindTablePasteRowsAsync(table, key);
            return (matches.Count, null, matches.FirstOrDefault());
        }

        void ValidateInsert(Dictionary<string, object> values, int number)
        {
            if (!canAdd || record.IsIdentity || record.IsReadOnly)
                throw new InvalidOperationException($"Строка {number}. Добавление с указанным Record недоступно.");
            var missing = columns.FirstOrDefault(c => !c.IsIdentity && !c.IsReadOnly && !c.IsNullable && !c.HasDefault &&
                (!values.TryGetValue(c.Name, out var value) || value == null));
            if (missing != null)
                throw new FormatException($"Строка {number}. Заполните обязательное поле «{missing.Name}».");
        }

        for (int rowIndex = 0; rowIndex < cells.Count; rowIndex++)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var key = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < mapped.Length; i++)
            {
                var column = mapped[i];
                if (column == null || (column != record && (column.IsIdentity || column.IsReadOnly))) continue;
                var value = ParseTablePasteValue(cells[rowIndex][i], column, rowIndex + 1);
                if (scope.Contains(column)) key[column.Name] = value;
                if (!column.IsIdentity && !column.IsReadOnly) values[column.Name] = value;
            }
            if (className != null)
            {
                var column = columns.First(c => c.Name == className);
                var value = ParseTablePasteValue(forcedClass.Value.ToString(CultureInfo.InvariantCulture), column, rowIndex + 1);
                if (!column.IsIdentity && !column.IsReadOnly) values[className] = value;
                if (scope.Contains(column)) key[className] = value;
            }
            if (scope.Any(c => !key.TryGetValue(c.Name, out var value) || value == null || string.IsNullOrWhiteSpace(Convert.ToString(value))))
                throw new FormatException($"Строка {rowIndex + 1}. Заполните поля ключа: {string.Join(", ", scope.Select(c => c.Name))}.");

            var found = await Find(key);
            bool duplicate = Reserved(key);
            reserved.Add(key);
            if (found.Count == 0 && !duplicate)
            {
                ValidateInsert(values, rowIndex + 1);
                plan.Add((key, values, null));
                offlineTargets.Add(null);
                plannedOrder.Add(rowIndex + 1);
                continue;
            }
            conflicts.Add(new PasteConflict
            {
                Number = rowIndex + 1, Key = key, Values = values, Local = found.Local, Online = found.Online,
                UniqueTarget = found.Count == 1, CanReplace = canEdit && found.Count == 1 && !duplicate,
                Reason = duplicate ? "Повтор в буфере" : found.Count > 1 ? "Несколько совпадений" :
                    canEdit ? "Запись существует" : "Нет права замены"
            });
        }

        async Task Resolve(List<PasteConflict> selected, bool replace)
        {
            var changed = new List<Dictionary<string, object>>();
            if (!replace)
            {
                foreach (var item in selected)
                {
                    var value = ParseTablePasteValue(item.NewRecord.Trim(), record, item.Number);
                    var key = new Dictionary<string, object>(item.Key, StringComparer.OrdinalIgnoreCase) { [record.Name] = value };
                    if (value == null || string.IsNullOrWhiteSpace(item.NewRecord) || Reserved(key) ||
                        changed.Any(other => key.All(field => PasteKeyEquals(other[field.Key], field.Value))) ||
                        (await Find(key)).Count != 0)
                        throw new FormatException($"Строка {item.Number}. Введите свободный Record.");
                    var values = new Dictionary<string, object>(item.Values, StringComparer.OrdinalIgnoreCase) { [record.Name] = value };
                    ValidateInsert(values, item.Number);
                    changed.Add(key);
                }
            }
            for (int i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                if (replace)
                {
                    foreach (var name in primaryKey.Concat(item.Key.Keys)) item.Values.Remove(name);
                }
                else
                {
                    reserved.Remove(item.Key);
                    reserved.Add(changed[i]);
                    item.Key = changed[i];
                    item.Values[record.Name] = item.Key[record.Name];
                }
                plan.Add((item.Key, item.Values, replace ? item.Online : null));
                offlineTargets.Add(replace ? item.Local : null);
                plannedOrder.Add(item.Number);
            }
            foreach (var pending in conflicts.Where(row => !row.Selected && row.UniqueTarget && !row.CanReplace && canEdit))
            {
                bool SameKey(Dictionary<string, object> key) => pending.Key.All(field => PasteKeyEquals(key[field.Key], field.Value));
                if (!plan.Any(row => SameKey(row.Key)) && !conflicts.Any(row => !row.Selected && row.Number < pending.Number && SameKey(row.Key)))
                {
                    pending.CanReplace = true;
                    pending.Reason = "Запись существует";
                }
            }
        }

        if (conflicts.Count > 0)
        {
            var dialog = new PasteConflictsWindow(conflicts, canAdd && !record.IsIdentity && !record.IsReadOnly, Resolve) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var order = Enumerable.Range(0, plan.Count).OrderBy(i => plannedOrder[i]).ToArray();
            plan = order.Select(i => plan[i]).ToList();
            offlineTargets = order.Select(i => offlineTargets[i]).ToList();
        }

        int inserted = plan.Where((row, index) => offline ? offlineTargets[index] == null : row.Expected == null).Count();
        if (_currentMode != 1 || _isOfflineWorkspace != offline ||
            (offline ? _offlineCurrentDataTable != data :
                _activeVm != vm || _activeDbName != dbName || vm.CurrentTable != table) ||
            !UserAuthorizationService.Has(Permission.Mode1View) ||
            (inserted > 0 && !UserAuthorizationService.Has(Permission.Mode1Add)) ||
            (inserted < plan.Count && !UserAuthorizationService.Has(Permission.Mode1Edit)))
            throw new InvalidOperationException("Таблица или права доступа изменились. Повторите вставку.");

        if (offline)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var row = offlineTargets[i] ?? data.NewRow();
                foreach (var field in plan[i].Values)
                    row[field.Key] = field.Value switch
                    {
                        null => DBNull.Value,
                        byte[] bytes => "0x" + Convert.ToHexString(bytes),
                        _ => Convert.ToString(field.Value, CultureInfo.InvariantCulture)
                    };
                if (offlineTargets[i] == null) data.Rows.Add(row);
            }
            _offlineGridDirty = true;
            CommitOfflineGridToProject();
            UpdateOfflineTableCount();
            SetOfflineEditorButtons();
        }
        else
        {
            var changes = await database.ApplyTablePasteAsync(table, primaryKey, plan);
            if (changes.Count > 0)
            {
                var added = changes.Count(change => change.Kind == ChangeJournalOperationKind.Insert);
                LogAction($"Вставка из буфера · {table} · добавлено: {added}, заменено: {changes.Count - added}",
                    category: "Вставка из буфера", source: table, journalChanges: changes, databaseName: dbName);
            }
            if (_currentMode == 1 && _activeVm == vm && _activeDbName == dbName && vm.CurrentTable == table && !_isOfflineWorkspace)
            {
                await vm.RefreshTotalCountAsync();
                await ReloadCurrentPageFastAsync();
            }
        }
        StatusText.Text = $"Обработано строк из буфера: {plan.Count}.";
    }

    private static bool PasteKeyEquals(object first, object second) => OfflineKeyEquals(
        Convert.ToString(first, CultureInfo.InvariantCulture), Convert.ToString(second, CultureInfo.InvariantCulture));

    private static object ParseTablePasteValue(string text, ColumnInfo column, int rowNumber)
    {
        var type = column.DataType?.ToLowerInvariant();
        bool stringType = type is "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml";
        if (text.Length == 0 && !stringType) return null;
        try
        {
            if (column.MaxLength > 0 && stringType && text.Length > column.MaxLength)
                throw new FormatException();
            if (type is "binary" or "varbinary" or "image")
                return Convert.FromHexString(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text);
            var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            if (type is "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real")
                text = text.Replace(separator, ".");
            return EditRowWindow.ConvertValue(text, column);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new FormatException($"Строка {rowNumber}. Недопустимое значение в столбце «{column.Name}».");
        }
    }
}
