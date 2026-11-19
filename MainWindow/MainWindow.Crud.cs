using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;
using Microsoft.Data.SqlClient;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Configurator
{
    public partial class MainWindow
    {
        // Mode 1 access is permission-based. Do not add a separate administrator-only gate.
        private bool RequireMode1Permission(Permission permission) => UserAuthorizationService.Require(permission);

        private void ClearActiveTable()
        {
            _activeVm = null;
            _activeDbName = "";

            if (_rightPanel.DataTableGrid != null) _rightPanel.DataTableGrid.ItemsSource = null;
            if (_rightPanel.TableTitle != null) _rightPanel.TableTitle.Text = "Выберите таблицу слева";

            _fieldsBuilder?.Clear();
            UpdateFilterButtonState();
            SafeSetButtonEnabled(_rightPanel.BtnFilter, false);

            if (_rightPanel.InputPanel != null)
                _rightPanel.InputPanel.Visibility = _fieldsPanelExpanded ? Visibility.Visible : Visibility.Collapsed;

            SafeSetButtonEnabled(_rightPanel.BtnAdd, false);
            SafeSetButtonEnabled(_rightPanel.BtnUpdate, false);
            SafeSetButtonEnabled(_rightPanel.BtnDuplicate, false);
            SafeSetButtonEnabled(_rightPanel.BtnDelete, false);

            SetPaginationButtons(false, false, false, false);

            if (_rightPanel.PageInfo != null) _rightPanel.PageInfo.Text = "";
        }

        private async Task LoadDataAsync(int preferredRowIndex = -1, Dictionary<string, object> preferredIdentity = null)
        {
            if (_activeVm == null) return;

            var vm = _activeVm;
            var tableTitle = $"{_activeDbName} → {vm.CurrentTable}";
            int generation = ++_loadGeneration;

            _dataLoadCts?.Cancel();
            _dataLoadCts = new CancellationTokenSource();
            var ct = _dataLoadCts.Token;

            if (_rightPanel.DataTableGrid != null)
                _rightPanel.DataTableGrid.ItemsSource = null;

            _fieldsBuilder.Clear();
            vm.ClearCurrentRow();
            _rightPanel.FieldsEmptyHint.Visibility = Visibility.Visible;
            _rightPanel.TableTitle.Text = $"{tableTitle} — загрузка...";
            // Keep paging controls responsive while a slow filtered query is in flight.
            // A new click cancels the previous request through _dataLoadCts.
            UpdatePagination();

            try
            {
                bool filterMissingColumn = vm.Filter?.IsActive == true &&
                    (vm.Columns == null || !vm.Columns.Any(c => c.Name.Equals(vm.Filter.Column, StringComparison.OrdinalIgnoreCase)));

                if (filterMissingColumn)
                    ShowFilterNotAppliedNotice(vm.Filter.Column);

                // Первый показ: страница и COUNT запускаются параллельно.
                // Если сохранённый фильтр не относится к текущей таблице, он
                // остаётся настроенным, но к данным временно не применяется.
                var pageTask = filterMissingColumn ? vm.LoadPageIgnoringFilterAsync(ct) : vm.LoadPageAsync(ct);
                var countTask = filterMissingColumn ? vm.GetTotalCountIgnoringFilterAsync(ct) : vm.GetTotalCountAsync(ct);
                var pageData = await pageTask;
                if (_loadGeneration != generation || ct.IsCancellationRequested) return;

                _rightPanel.TableTitle.Text = tableTitle;
                UpdateFilterButtonState();
                SafeSetButtonEnabled(_rightPanel.BtnFilter, vm.Columns?.Count > 0);
                BindPageData(pageData, preferredRowIndex, preferredIdentity, resetScroll: preferredRowIndex < 0);

                SafeSetButtonEnabled(_rightPanel.BtnAdd, true);

                if (vm.Columns != null && vm.Columns.Count > 0)
                {
                    _fieldsBuilder.DisplayPrefix = "";
                    _fieldsBuilder.Generate(vm.Columns, vm.KeyColumn);
                }

                // The page is usable now. Complete COUNT independently so an expensive
                // filtered count cannot hold navigation hostage after rows are displayed.
                UpdatePagination();
                _ = CompleteCountUpdateAsync(countTask, vm, generation, ct, preferredRowIndex, preferredIdentity);
            }
            catch (FilterColumnMissingException ex)
            {
                ShowFilterNotAppliedNotice(ex.Message);
                await ReloadWithoutCurrentFilterAsync(vm, ct);
            }
            catch (OperationCanceledException)
            {
                // Пользователь сменил таблицу/страницу — старый запрос больше не нужен.
            }
            catch (Exception ex) when (vm.Filter?.IsActive == true && ex.Message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase))
            {
                ShowFilterNotAppliedNotice(vm.Filter.Column);
                await ReloadWithoutCurrentFilterAsync(vm, ct);
            }
            catch (SqlException ex) when (IsConnectionFailure(ex))
            {
                if (_loadGeneration != generation || ct.IsCancellationRequested) return;
                MarkDatabaseUnavailable(_activeDbName, ex);
            }
            catch (Exception ex)
            {
                if (_loadGeneration != generation || ct.IsCancellationRequested) return;
                _rightPanel.TableTitle.Text = vm.CurrentTable;
                System.Diagnostics.Debug.WriteLine(ex);
                Configurator.Infrastructure.ModernDialog.Show("Не удалось загрузить данные. Проверьте подключение и повторите попытку.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task CompleteCountUpdateAsync(
            Task<int> countTask,
            TableViewModel vm,
            int generation,
            CancellationToken ct,
            int preferredRowIndex,
            Dictionary<string, object> preferredIdentity)
        {
            try
            {
                await countTask;
                if (_loadGeneration != generation || ct.IsCancellationRequested || !ReferenceEquals(vm, _activeVm)) return;
                if (vm.CurrentPage > vm.TotalPages)
                {
                    vm.CurrentPage = vm.TotalPages;
                    await ReloadCurrentPageFastAsync(Math.Max(0, preferredRowIndex), preferredIdentity);
                    return;
                }
                UpdatePagination();
            }
            catch (OperationCanceledException) { }
            catch (SqlException ex) when (IsConnectionFailure(ex))
            {
                if (_loadGeneration == generation && !ct.IsCancellationRequested)
                    MarkDatabaseUnavailable(_activeDbName, ex);
            }
            catch (Exception ex)
            {
                if (_loadGeneration == generation && !ct.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine($"[CountUpdate] {ex.Message}");
                    if (StatusText != null) StatusText.Text = "Не удалось пересчитать страницы. Обновите таблицу.";
                    UpdatePagination();
                }
            }
        }

        private async Task ReloadCurrentPageFastAsync(
            int preferredRowIndex = -1,
            Dictionary<string, object> preferredIdentity = null)
        {
            if (_activeVm == null) return;

            var vm = _activeVm;
            int generation = ++_loadGeneration;

            _dataLoadCts?.Cancel();
            _dataLoadCts = new CancellationTokenSource();
            var ct = _dataLoadCts.Token;

            try
            {
                // Важное отличие от старой логики: после CRUD НЕ выполняем COUNT(*).
                // Загружается только одна текущая страница.
                var pageData = await vm.ReloadCurrentPageAsync(ct);
                if (_loadGeneration != generation || ct.IsCancellationRequested) return;

                BindPageData(pageData, preferredRowIndex, preferredIdentity, resetScroll: false);
                UpdatePagination();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_loadGeneration != generation || ct.IsCancellationRequested) return;
                System.Diagnostics.Debug.WriteLine(ex);
                Configurator.Infrastructure.ModernDialog.Show("Не удалось обновить таблицу. Проверьте подключение и повторите попытку.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BindPageData(
            DataTable pageData,
            int preferredRowIndex,
            Dictionary<string, object> preferredIdentity,
            bool resetScroll)
        {
            var grid = _rightPanel.DataTableGrid;
            if (grid == null) return;

            grid.ItemsSource = null;
            grid.ItemsSource = pageData?.DefaultView;
            // WPF can retain a recycled DataGridCell's displayed value when a
            // fresh DataView replaces the filtered page. Force item refresh so
            // every visible cell is rebound to the new row values.
            grid.Items.Refresh();

            if (resetScroll)
            {
                Dispatcher.BeginInvoke(new Action(ScrollDataGridToTop),
                    DispatcherPriority.Loaded);
            }

            if (pageData == null || pageData.Rows.Count == 0)
            {
                grid.SelectedIndex = -1;
                return;
            }

            int index = preferredRowIndex >= 0
                ? Math.Min(preferredRowIndex, pageData.Rows.Count - 1)
                : -1;

            if (preferredIdentity != null && preferredIdentity.Count > 0)
            {
                for (int i = 0; i < pageData.Rows.Count; i++)
                {
                    bool match = preferredIdentity.All(pair =>
                        pageData.Columns.Contains(pair.Key) &&
                        ValuesEqual(pageData.Rows[i][pair.Key], pair.Value));

                    if (match)
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index >= 0)
            {
                grid.SelectedIndex = index;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (grid.SelectedItem != null)
                        grid.ScrollIntoView(grid.SelectedItem);
                    QueueSelectionStateUpdate();
                }), DispatcherPriority.Loaded);
            }
            else
            {
                grid.SelectedIndex = -1;
            }
        }

        private static bool ValuesEqual(object a, object b)
        {
            if (a == null || a == DBNull.Value) return b == null || b == DBNull.Value;
            if (b == null || b == DBNull.Value) return false;
            return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private void SafeSetButtonEnabled(Button btn, bool enabled)
        {
            if (btn == null) return;
            if (_currentMode != 1 || !UserAuthorizationService.Has(Permission.Mode1View)) enabled = false;
            if (btn == _rightPanel?.BtnAdd || btn == _rightPanel?.BtnDuplicate) enabled &= UserAuthorizationService.Has(Permission.Mode1Add);
            else if (btn == _rightPanel?.BtnUpdate) enabled &= UserAuthorizationService.Has(Permission.Mode1Edit);
            else if (btn == _rightPanel?.BtnDelete) enabled &= UserAuthorizationService.Has(Permission.Mode1Delete);
            btn.IsEnabled = enabled;
        }

        private void ScrollDataGridToTop()
        {
            if (_rightPanel.DataTableGrid == null || _rightPanel.DataTableGrid.Items.Count == 0) return;

            _rightPanel.DataTableGrid.SelectedIndex = -1;

            if (_rightPanel.DataTableGrid.Items.Count > 0)
            {
                _rightPanel.DataTableGrid.ScrollIntoView(_rightPanel.DataTableGrid.Items[0]);
            }

            var scrollViewer = VisualTreeHelper.FindChild<System.Windows.Controls.ScrollViewer>(_rightPanel.DataTableGrid);
            scrollViewer?.ScrollToTop();
        }

        private void DataTableGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            QueueSelectionStateUpdate();
        }

        private void DataTableGrid_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            QueueSelectionStateUpdate();
        }

        private void DataTableGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is DataGrid grid && e.OriginalSource is DependencyObject source
                && source is not DataGridCell && FindVisualParent<DataGridCell>(source) == null
                && ItemsControl.ContainerFromElement(grid, source) is DataGridRow { Item: DataRowView item })
                grid.SelectedItem = item;
        }

        private void DataTableGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
            if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row || row.Item is not DataRowView) return;
            grid.SelectedItem = row.Item;
            UpdateSelectionState();
            if (_rightPanel.BtnUpdate.IsEnabled) BtnUpdate_Click(_rightPanel.BtnUpdate, new RoutedEventArgs());
        }

        private void QueueSelectionStateUpdate()
        {
            Dispatcher.BeginInvoke(new Action(UpdateSelectionState), DispatcherPriority.DataBind);
        }

        private void UpdateSelectionState()
        {
            if (_rightPanel.DataTableGrid == null) return;
            if (_isOfflineWorkspace)
            {
                SetOfflineEditorButtons();
                return;
            }
            if (_activeVm == null) return;

            int selectedCount = _rightPanel.DataTableGrid.SelectedItems.Count;

            if (selectedCount == 1 && _rightPanel.DataTableGrid.SelectedItem is DataRowView row)
            {
                _activeVm.SetCurrentRow(row);
                _fieldsBuilder.Fill(_activeVm.CurrentRowValues);
                _rightPanel.FieldsEmptyHint.Text = "";
                _rightPanel.FieldsEmptyHint.Visibility = Visibility.Collapsed;

                // Активируем CRUD только если строка действительно содержит все
                // значения идентификатора (PK либо уникального индекса).
                bool rowHasIdentity = _activeVm.HasValidKey
                    && (_activeVm.UsesValueIdentity
                        ? _activeVm.CurrentIdentityValues.Count > 0
                        : _activeVm.KeyColumns.All(key => _activeVm.CurrentKeyValues.ContainsKey(key)));

                SafeSetButtonEnabled(_rightPanel.BtnUpdate, rowHasIdentity);
                bool canDuplicate = rowHasIdentity && _activeVm.CanDuplicateCurrentRow;
                SafeSetButtonEnabled(_rightPanel.BtnDuplicate, canDuplicate);
                SafeSetButtonEnabled(_rightPanel.BtnDelete, rowHasIdentity);
                if (!rowHasIdentity)
                {
                    _rightPanel.BtnUpdate.ToolTip = "Не удалось определить строку";
                    _rightPanel.BtnDuplicate.ToolTip = "Не удалось определить строку";
                    _rightPanel.BtnDelete.ToolTip = "Не удалось определить строку";
                }
                else
                {
                    _rightPanel.BtnUpdate.ToolTip = _activeVm.UsesValueIdentity
                        ? "Изменить строку по полному снимку; сохранение разрешено при одном совпадении"
                        : "Изменить выбранную строку";
                    _rightPanel.BtnDuplicate.ToolTip = canDuplicate
                        ? "Создать копию выбранной строки"
                        : "Для безопасного дублирования нужен числовой Record или стабильный ключ";
                    _rightPanel.BtnDelete.ToolTip = _activeVm.UsesValueIdentity
                        ? "Удалить строку, только если полный снимок однозначно совпадает"
                        : "Удалить выбранные строки";
                }
                return;
            }

            _fieldsBuilder.ClearValues();
            _activeVm.ClearCurrentRow();
            _rightPanel.FieldsEmptyHint.Text = selectedCount > 1 ? $"Выбрано строк: {selectedCount}" : "Выберите одну строку в таблице";
            _rightPanel.FieldsEmptyHint.Visibility = Visibility.Visible;
            SafeSetButtonEnabled(_rightPanel.BtnUpdate, false);
            SafeSetButtonEnabled(_rightPanel.BtnDuplicate, false);
            bool selectedRowsHaveIdentity = selectedCount > 0 && _activeVm.HasValidKey;
            if (selectedRowsHaveIdentity && _activeVm.UsesValueIdentity)
                selectedRowsHaveIdentity = _rightPanel.DataTableGrid.SelectedItems.OfType<DataRowView>()
                    .All(row => _activeVm.BuildIdentityValues(row).Count > 0);
            SafeSetButtonEnabled(_rightPanel.BtnDelete, selectedRowsHaveIdentity);
        }

        private async Task<Dictionary<string, List<LookupOption>>> LoadLookupOptionsForTableAsync(string tableName)
        {
            var result = new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(_activeDbName)) return result;
            try
            {
                var db = new DatabaseService(_connection.ToConnectionString(_activeDbName));
                var lookupService = new LookupOptionsService(db);
                var columns = _activeVm?.Columns ?? new List<ColumnInfo>();
                foreach (var column in columns)
                {
                    var name = column.Name;
                    if (!new[] { "Type", "PLC", "Area", "PLCClassNumber", "PLCClasssNumber", "Class", "Unit" }
                        .Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    var options = await lookupService.TryGetAsync("", tableName, name);
                    if (options.Count > 0)
                        result[name] = options.Select(x => new LookupOption { Key = x.Key, Display = x.Display }).ToList();
                }
                // Preserve the class-specific Type mapping for legacy system tables.
                var definition = SystemLookupCatalog.All.FirstOrDefault(x => x.TargetTable.Equals(tableName ?? "", StringComparison.OrdinalIgnoreCase));
                if (definition != null && await db.TableExistsAsync(definition.LookupTable))
                    result[definition.TargetField] = await db.GetLookupOptionsAsync(definition.LookupTable);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LookupOptions] {tableName}: {ex.Message}"); }
            return result;
        }

        private async void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireMode1Permission(Permission.Mode1Add)) return;
            if (_isOfflineWorkspace) { AddOfflineRow(); return; }
            if (_activeVm == null || string.IsNullOrEmpty(_activeVm.CurrentTable)
                || _activeVm.Columns == null || _activeVm.Columns.Count == 0)
                return;

            int nextRecord = 0;
            bool hasPlc = _activeVm.Columns.Any(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase));
            bool hasRecord = _activeVm.Columns.Any(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));
            int? forcedClassNumber = ResolveSystemClassNumber(_activeVm.CurrentTable);
            string classNumberColumn = forcedClassNumber.HasValue ? ResolveClassNumberColumn(_activeVm.Columns) : null;

            // Для PLC + Record начальное значение рассчитывается после выбора PLC в окне.
            // Для таблиц без PLC сохраняем прежнюю глобальную автогенерацию Record.
            if (!hasPlc && hasRecord && !_activeVm.Columns.First(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase)).IsIdentity && !_activeVm.Columns.First(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase)).IsReadOnly)
            {
                try { nextRecord = await _activeVm.GetNextRecordValueAsync(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); Configurator.Infrastructure.ModernDialog.Show("Не удалось получить значение Record. Проверьте подключение к базе."); return; }
            }

            var addLookupOptions = await LoadLookupOptionsForTableAsync(_activeVm.CurrentTable);
            var addWindow = new AddRowWindow(
                _activeVm.CurrentTable,
                _activeVm.Columns,
                nextRecord,
                hasRecord ? _activeVm.Columns.First(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase)).Name : "",
                _activeDbName.Length > 0 ? _connection.ToConnectionString(_activeDbName) : null,
                hasPlc ? _activeVm.Columns.First(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase)).Name : null,
                forcedClassNumber,
                classNumberColumn,
                addLookupOptions)
            { Owner = this };

            addWindow.ShowDialog();

            if (!addWindow.Confirmed || addWindow.Values == null || addWindow.Values.Count == 0)
                return;

            try
            {
                await _activeVm.InsertAsync(addWindow.Values);
                var addTable = _activeVm.CurrentTable;
                var addDbName = _activeDbName;
                var addValues = new Dictionary<string, object>(addWindow.Values);
                var addIdentity = _activeVm.BuildIdentityValues(addValues);
                _activeVm.NotifyInserted(1);
                if (_activeVm.Filter?.IsActive == true)
                    await _activeVm.RefreshTotalCountAsync();

                // Добавленная строка логически находится в конце сортировки.
                // Переходим на последнюю страницу без повторного COUNT(*).
                _activeVm.CurrentPage = _activeVm.TotalPages;
                var preferred = new Dictionary<string, object>(addWindow.Values);

                string addDetails = string.Join("; ", addValues.Select(x => $"{x.Key}={FormatLogValue(x.Value)}"));
                LogAction($"Добавлена строка · {addTable}", async () =>
                {
                    var undoDb = new DatabaseService(_connection.ToConnectionString(addDbName));
                    await undoDb.DeleteRowsByValuesAsync(addTable, new List<Dictionary<string, object>> { addIdentity });
                }, category: "Добавление", source: addTable, details: addDetails,
                journalChanges: new[]
                {
                    CreateJournalChange(addTable, ChangeJournalOperationKind.Insert, null, addValues, null, addIdentity)
                }, operationKind: ChangeJournalOperationKind.Insert, databaseName: addDbName);
                await ReloadCurrentPageFastAsync(-1, preferred);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (ex is SqlException sqlEx && (sqlEx.Number == 2601 || sqlEx.Number == 2627))
                    Configurator.Infrastructure.ModernDialog.Show("Запись с таким Record уже существует. Выберите другое значение.", "Конфликт ключа", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    Configurator.Infrastructure.ModernDialog.Show("Не удалось добавить запись. Проверьте введённые значения и повторите попытку.");
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireMode1Permission(Permission.Mode1Edit)) return;
            if (_isOfflineWorkspace) { EditOfflineRow(); return; }
            if (_activeVm == null || !_activeVm.HasValidKey) return;
            if (_rightPanel.DataTableGrid.SelectedItem is not DataRowView) return;

            int selectedIndex = Math.Max(0, _rightPanel.DataTableGrid.SelectedIndex);
            var oldIdentity = _activeVm.UsesValueIdentity
                ? new Dictionary<string, object>(_activeVm.CurrentIdentityValues)
                : new Dictionary<string, object>(_activeVm.CurrentKeyValues);
            var oldValues = new Dictionary<string, object>(_activeVm.CurrentRawRowValues);
            var updateTable = _activeVm.CurrentTable;
            var updateDbName = _activeDbName;

            int? editClassNumber = ResolveSystemClassNumber(_activeVm.CurrentTable);
            string editClassNumberColumn = editClassNumber.HasValue ? ResolveClassNumberColumn(_activeVm.Columns) : null;
            var editLookupOptions = await LoadLookupOptionsForTableAsync(_activeVm.CurrentTable);
            var editWindow = new EditRowWindow(
                _activeVm.CurrentTable,
                _activeVm.Columns,
                new Dictionary<string, string>(_activeVm.CurrentRowValues),
                _activeVm.KeyColumn,
                editClassNumberColumn,
                editClassNumber,
                editLookupOptions)
            { Owner = this };

            editWindow.ShowDialog();

            if (!editWindow.Confirmed || editWindow.Values == null || editWindow.Values.Count == 0)
                return;

            var relatedChanges = editWindow.Values
                .Where(pair => oldValues.TryGetValue(pair.Key, out var oldValue) && !ValuesEqual(oldValue, pair.Value))
                .Where(pair => editLookupOptions.ContainsKey(pair.Key) || IsLikelyRelatedField(pair.Key))
                .Select(pair => $"{pair.Key}: {FormatLogValue(oldValues.TryGetValue(pair.Key, out var oldValue) ? oldValue : null)} → {FormatLogValue(pair.Value)}")
                .ToList();
            if (relatedChanges.Count > 0 && Configurator.Infrastructure.ModernDialog.Show(
                "Вы изменяете связанные поля. Это может изменить привязки объекта к справочникам или другим объектам:\n\n" + string.Join("\n", relatedChanges) + "\n\nПродолжить изменение?",
                "Предупреждение о смене связей", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                var preferredIdentity = new Dictionary<string, object>(oldIdentity);
                foreach (var pair in editWindow.Values)
                    if (preferredIdentity.ContainsKey(pair.Key))
                        preferredIdentity[pair.Key] = pair.Value;

                await _activeVm.UpdateAsync(editWindow.Values);
                if (_activeVm.Filter?.IsActive == true)
                    await _activeVm.RefreshTotalCountAsync();

                var afterValues = new Dictionary<string, object>(oldValues);
                foreach (var pair in editWindow.Values) afterValues[pair.Key] = pair.Value;
                var afterIdentity = _activeVm.BuildIdentityValues(afterValues);
                var changedDetails = string.Join("; ", editWindow.Values.Select(x => $"{x.Key}: {FormatLogValue(oldValues.TryGetValue(x.Key, out var ov) ? ov : null)} → {FormatLogValue(x.Value)}"));
                LogAction($"Изменена строка · {updateTable}", async () =>
                {
                    var undoDb = new DatabaseService(_connection.ToConnectionString(updateDbName));
                    await undoDb.UpdateRowByValuesAsync(updateTable, afterIdentity, oldValues);
                }, category: "Изменение", source: updateTable, details: changedDetails,
                journalChanges: new[]
                {
                    CreateJournalChange(updateTable, ChangeJournalOperationKind.Update, oldValues, afterValues, oldIdentity, afterIdentity)
                }, operationKind: ChangeJournalOperationKind.Update, databaseName: updateDbName);

                // Быстрое обновление: только текущая страница, без COUNT(*).
                await ReloadCurrentPageFastAsync(selectedIndex, preferredIdentity);
            }
            catch (Exception ex)
            {
                if (ex is SqlException sqlEx && (sqlEx.Number == 2601 || sqlEx.Number == 2627))
                    Configurator.Infrastructure.ModernDialog.Show("Нарушено ограничение уникальности. Такой Record или составная комбинация PLC + Class + Record уже существует. Выберите другое значение Record.", "Конфликт ключа", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                    Configurator.Infrastructure.ModernDialog.Show("Не удалось изменить запись. Проверьте введённые значения и повторите попытку.");
                }
            }
        }


        private static bool IsLikelyRelatedField(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Replace("_", "").Replace(" ", "").ToLowerInvariant();
            return n is "plc" or "area" or "type" or "class" or "number" or "recordplcclass" ||
                   n.EndsWith("type") || n.EndsWith("class") || n.EndsWith("plc") || n.EndsWith("area") || n.EndsWith("numberid");
        }

        private async void BtnDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireMode1Permission(Permission.Mode1Add)) return;
            if (_isOfflineWorkspace) { DuplicateOfflineRow(); return; }
            if (_activeVm == null || !_activeVm.HasValidKey) return;

            try
            {
                var values = await _activeVm.DuplicateCurrentRowAsync();
                var duplicateTable = _activeVm.CurrentTable;
                var duplicateDbName = _activeDbName;
                if (values.Count == 0)
                {
                    Configurator.Infrastructure.ModernDialog.Show("Нет данных для дублирования.");
                    return;
                }

                _activeVm.NotifyInserted(1);
                if (_activeVm.Filter?.IsActive == true)
                    await _activeVm.RefreshTotalCountAsync();
                // A duplicated row commonly receives the next Record and therefore belongs
                // on the last page under key ordering. Go there, but select by key identity,
                // not by every copied/display/computed column (e.g. computed Area).
                _activeVm.CurrentPage = _activeVm.TotalPages;

                string newRecordMsg = "";
                if (values.TryGetValue(_activeVm.RecordColumn, out var recordValue))
                    newRecordMsg = $" Новый {_activeVm.RecordColumn} = {recordValue}";

                var duplicateIdentity = _activeVm.BuildIdentityValues(values);
                string duplicateDetails = string.Join("; ", values.Select(x => $"{x.Key}={FormatLogValue(x.Value)}"));
                LogAction($"Дублирована строка · {duplicateTable}{newRecordMsg}", async () =>
                {
                    var undoDb = new DatabaseService(_connection.ToConnectionString(duplicateDbName));
                    await undoDb.DeleteRowsByValuesAsync(duplicateTable, new List<Dictionary<string, object>> { duplicateIdentity });
                }, category: "Дублирование", source: duplicateTable, details: duplicateDetails,
                journalChanges: new[]
                {
                    CreateJournalChange(duplicateTable, ChangeJournalOperationKind.Duplicate, null, values, null, duplicateIdentity)
                }, operationKind: ChangeJournalOperationKind.Duplicate, databaseName: duplicateDbName);

                // Ищем копию по тем значениям, которые реально были вставлены.
                // Это работает и для PLC + Record, и для keyless таблиц.
                await ReloadCurrentPageFastAsync(-1, duplicateIdentity);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                Configurator.Infrastructure.ModernDialog.Show("Не удалось дублировать запись. Проверьте исходную строку и подключение к базе.");
            }
        }


        private static string FormatLogValue(object value)
            => value == null || value == DBNull.Value ? "NULL" : Convert.ToString(value);

        private sealed class FilterColumnMissingException : Exception
        {
            public FilterColumnMissingException(string column) : base(column) { }
        }

    }
}
