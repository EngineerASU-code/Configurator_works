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
        if (_currentMode != 1 || Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.C or Key.V))
            return;
        e.Handled = true;
        if (!UserAuthorizationService.Has(Permission.Mode1View) || _tablePasteInProgress ||
            sender is not DataGrid grid || grid.ItemsSource is not DataView view)
            return;

        var visible = grid.Columns.Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex).ToList();
        var names = visible.Select(c => c.SortMemberPath).ToArray();
        try
        {
            if (e.Key == Key.C)
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
            if (e.Key == Key.V) _tablePasteInProgress = false;
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

            while (true)
            {
                DataRow[] localMatches = offline
                    ? data.AsEnumerable().Where(row => key.All(field => PasteKeyEquals(row[field.Key], field.Value))).Take(2).ToArray()
                    : Array.Empty<DataRow>();
                var onlineMatches = offline ? null : await database.FindTablePasteRowsAsync(table, key);
                int matches = offline ? localMatches.Length : onlineMatches.Count;
                bool alreadyPlanned = plan.Any(row => key.All(field => PasteKeyEquals(row.Key[field.Key], field.Value)));
                if (matches == 0 && !alreadyPlanned)
                {
                    if (!canAdd || record.IsIdentity || record.IsReadOnly)
                        throw new InvalidOperationException($"Строка {rowIndex + 1}. Добавление с указанным Record недоступно.");
                    var missing = columns.FirstOrDefault(c => !c.IsIdentity && !c.IsReadOnly && !c.IsNullable && !c.HasDefault &&
                        (!values.TryGetValue(c.Name, out var value) || value == null));
                    if (missing != null)
                        throw new FormatException($"Строка {rowIndex + 1}. Заполните обязательное поле «{missing.Name}».");
                    plan.Add((key, values, null));
                    offlineTargets.Add(null);
                    break;
                }

                bool canReplace = canEdit && matches == 1 && !alreadyPlanned;
                var conflict = ResolveTablePasteConflict(rowIndex + 1, key, record,
                    canAdd && !record.IsIdentity && !record.IsReadOnly, canReplace, alreadyPlanned);
                if (conflict.Cancelled) return;
                if (conflict.Replace)
                {
                    foreach (var name in primaryKey.Concat(key.Keys))
                        values.Remove(name);
                    plan.Add((key, values, offline ? null : onlineMatches[0]));
                    offlineTargets.Add(offline ? localMatches[0] : null);
                    break;
                }

                key[record.Name] = values[record.Name] = conflict.Record;
            }
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
            await database.ApplyTablePasteAsync(table, primaryKey, plan);
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

    private (bool Cancelled, bool Replace, object Record) ResolveTablePasteConflict(int rowNumber,
        Dictionary<string, object> key, ColumnInfo record, bool canChange, bool canReplace, bool alreadyPlanned)
    {
        var dialog = new Window
        {
            Owner = this, Title = "Конфликт Record", SizeToContent = SizeToContent.Height,
            Width = 510, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        dialog.Content = panel;
        var description = alreadyPlanned ? "Этот ключ уже встречается в блоке вставки." :
            canReplace ? "Запись с таким ключом уже существует. Можно ввести новый Record или заменить значения вставляемых полей, сохранив ключ строки." :
            "Запись с таким ключом уже существует. Замена недоступна.";
        panel.Children.Add(new TextBlock
        {
            Text = $"Строка {rowNumber} · {string.Join(", ", key.Select(field => $"{field.Key}={field.Value}"))}\n{description}",
            TextWrapping = TextWrapping.Wrap
        });
        var input = new TextBox { Text = Convert.ToString(key[record.Name]), IsEnabled = canChange, Margin = new Thickness(0, 12, 0, 8) };
        panel.Children.Add(input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(buttons);
        var change = new Button { Content = "Изменить Record", IsEnabled = canChange, IsDefault = canChange };
        var replace = new Button { Content = "Заменить запись", IsEnabled = canReplace, Margin = new Thickness(8, 0, 8, 0) };
        var cancel = new Button { Content = "Отменить вставку", IsCancel = true };
        buttons.Children.Add(change);
        buttons.Children.Add(replace);
        buttons.Children.Add(cancel);
        object changedRecord = null;
        bool replaceExisting = false;
        change.Click += (_, _) =>
        {
            try
            {
                var value = ParseTablePasteValue(input.Text.Trim(), record, rowNumber);
                if (value == null || input.Text.Trim().Length == 0 || PasteKeyEquals(key[record.Name], value))
                    throw new FormatException("Введите другое значение Record.");
                changedRecord = value;
                dialog.DialogResult = true;
            }
            catch (FormatException ex) { error.Text = ex.Message; }
        };
        replace.Click += (_, _) =>
        {
            replaceExisting = true;
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { if (canChange) { input.Focus(); input.SelectAll(); } };
        return (dialog.ShowDialog() != true, replaceExisting, changedRecord);
    }
}
