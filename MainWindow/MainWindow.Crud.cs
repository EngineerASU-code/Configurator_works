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
            _loadGeneration++;
            _dataLoadCts?.Cancel();
            _activeVm = null;
            _activeDbName = "";

            if (_rightPanel.DataTableGrid != null) _rightPanel.DataTableGrid.ItemsSource = null;
            if (_rightPanel.TableTitle != null) _rightPanel.TableTitle.Text = "Выберите таблицу слева";

            _fieldsBuilder?.Clear();

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
                var pageTask = vm.LoadPageAsync(ct);
                var countTask = vm.GetTotalCountAsync(ct);
                var pageData = await pageTask;
                if (_loadGeneration != generation || ct.IsCancellationRequested) return;

                _rightPanel.TableTitle.Text = tableTitle;
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
            catch (OperationCanceledException)
            {
                // Пользователь сменил таблицу/страницу — старый запрос больше не нужен.
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
                SessionTestLog.Trace("TABLE-LOAD-ERROR", $"table='{vm.CurrentTable}'; {ex}");
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
            Dictionary<string, object> preferredIdentity = null, string copyRecordMessage = null)
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
                bool hidden = false;
                if (copyRecordMessage != null)
                {
                    var page = await vm.FindRowPageAsync(preferredIdentity, ct);
                    if (_loadGeneration != generation || ct.IsCancellationRequested || _activeVm != vm) return;
                    if (page.HasValue) vm.CurrentPage = page.Value;
                    else hidden = true;
                }
                var pageData = await vm.ReloadCurrentPageAsync(ct);
                if (_loadGeneration != generation || ct.IsCancellationRequested) return;

                BindPageData(pageData, preferredRowIndex, preferredIdentity, resetScroll: false);
                UpdatePagination();
                if (hidden && vm.ColumnView.HasFilters)
                {
                    LastActionText.Text = $"Копия создана, но скрыта фильтром.{copyRecordMessage}";
                    StatusText.Text = LastActionText.Text;
                }
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
            _rightPanel.HasActiveColumnView = CurrentColumnView?.IsActive == true;
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
            enabled &= Configurator.Core.Projects.ProjectWriteGuard.IsDataTableWritable(_isOfflineWorkspace ? _offlineCurrentTableName : _activeVm?.CurrentTable);
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
                static string NormalizeTable(string name) => string.Join('.', (name ?? string.Empty)
                    .Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim().Trim('[', ']')));
                var classDefinition = new Configurator.Infrastructure.Architecture.TemplateLibraryStore().Load()
                    .FirstOrDefault(c => string.Equals(NormalizeTable(c.PrimaryStorage?.TableName), NormalizeTable(tableName), StringComparison.OrdinalIgnoreCase));
                foreach (var column in columns)
                {
                    var name = column.Name;
                    var field = classDefinition?.Fields?.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var options = await lookupService.TryGetAsync(classDefinition?.Name ?? "", tableName, name, default,
                        field?.LookupTable, field?.LookupKeyField, field?.LookupDisplayField);
                    if (options.Count > 0)
                        result[name] = options.Select(x => new LookupOption { Key = x.Key, Display = x.Display }).ToList();
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LookupOptions] {tableName}: {ex.Message}"); }
            return result;
        }

        private Dictionary<string, object> InitialPlcForNewRow()
        {
            var plc = CurrentColumnView?.Filters.FirstOrDefault(filter =>
                filter.Column.Equals("PLC", StringComparison.OrdinalIgnoreCase) && filter.Operator == "Equals")?.Value;
            if (string.IsNullOrWhiteSpace(plc) && _rightPanel.DataTableGrid.SelectedItem is DataRowView row && row.Row.Table.Columns.Contains("PLC"))
                plc = Convert.ToString(row["PLC"], System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(plc) ? null : new Dictionary<string, object> { ["PLC"] = plc };
        }

        private async void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireMode1Permission(Permission.Mode1Add)) return;
            if (_isOfflineWorkspace) { await AddOfflineRow(); return; }
            if (_activeVm == null || string.IsNullOrEmpty(_activeVm.CurrentTable)
                || _activeVm.Columns == null || _activeVm.Columns.Count == 0)
                return;

            var addVm = _activeVm;
            var addTable = addVm.CurrentTable;
            var addDbName = _activeDbName;
            bool CanAdd() => !_isOfflineWorkspace && _activeVm == addVm && addVm.CurrentTable == addTable && _activeDbName == addDbName;
            int nextRecord = 0;
            var plcColumn = _activeVm.Columns.FirstOrDefault(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase));
            var recordColumn = _activeVm.Columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));
            bool suggestScopedRecord = plcColumn != null && recordColumn is { IsIdentity: false, IsReadOnly: false };
            int? forcedClassNumber = ResolveSystemClassNumber(_activeVm.CurrentTable);
            string classNumberColumn = forcedClassNumber.HasValue ? ResolveClassNumberColumn(_activeVm.Columns) : null;

            // Для PLC + Record начальное значение рассчитывается после выбора PLC в окне.
            // Для таблиц без PLC сохраняем прежнюю глобальную автогенерацию Record.
            if (plcColumn == null && recordColumn is { IsIdentity: false, IsReadOnly: false })
            {
                try { nextRecord = await _activeVm.GetNextRecordValueAsync(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); Configurator.Infrastructure.ModernDialog.Show("Не удалось получить значение Record. Проверьте подключение к базе."); return; }
            }

            var addLookupOptions = await LoadLookupOptionsForTableAsync(_activeVm.CurrentTable);
            if (!CanAdd()) return;
            var source = new Configurator.Core.Projects.OnlineProjectDataSource(_connection.ToConnectionString(_activeDbName), _activeDbName);
            using var addressWorkspace = new Configurator.Core.Projects.ProjectWorkspace(source,
                architecture: new Configurator.Infrastructure.Projects.ProjectArchitectureDataSource(source.Server, source.Database));
            var tableName = _activeVm.CurrentTable;
            var addWindow = new AddRowWindow(
                _activeVm.CurrentTable,
                _activeVm.Columns,
                nextRecord,
                recordColumn?.Name,
                _activeDbName.Length > 0 ? _connection.ToConnectionString(_activeDbName) : null,
                plcColumn?.Name,
                forcedClassNumber,
                classNumberColumn,
                addLookupOptions, initialValues: InitialPlcForNewRow(),
                preferFreeScopedRecord: suggestScopedRecord,
                recordScopeClassColumn: ProjectClassCatalog.FindRecordClassColumn(_activeVm.KeyColumns),
                freeAddressProvider: (plc, cls, ct) => PlcAddressCatalog.SuggestAddressesAsync(addressWorkspace, tableName, plc, cls, ct))
            { Owner = this };

            addWindow.CanApply = CanAdd;
            await addWindow.ShowRowEditorAsync();

            if (!addWindow.Confirmed || addWindow.Values == null || addWindow.Values.Count == 0)
                return;

            try
            {
                var keys = addVm.KeyColumns.ToList();
                var addValues = await addVm.InsertAndGetSnapshotAsync(addWindow.Values);
                var addIdentity = keys.Count > 0 ? keys.ToDictionary(key => key, key => addValues[key]) : addVm.BuildIdentityValues(addValues);
                string addDetails = string.Join("; ", addValues.Select(x => $"{x.Key}={FormatLogValue(x.Value)}"));
                LogAction($"Добавлена строка · {addTable}", category: "Добавление", source: addTable, details: addDetails,
                journalChanges: new[]
                {
                    CreateJournalChange(addTable, ChangeJournalOperationKind.Insert, null, addValues, null, addIdentity)
                }, operationKind: ChangeJournalOperationKind.Insert, databaseName: addDbName);
                if (_activeVm != addVm || addVm.CurrentTable != addTable || _activeDbName != addDbName) return;
                addVm.NotifyInserted(1);
                if (addVm.ColumnView.HasFilters) await addVm.RefreshTotalCountAsync();
                if (!suggestScopedRecord) addVm.CurrentPage = addVm.TotalPages;
                await ReloadCurrentPageFastAsync(-1, addIdentity);
                if (suggestScopedRecord && addValues.TryGetValue(recordColumn.Name, out var record))
                    StatusText.Text = $"Строка добавлена · Record = {FormatLogValue(record)}";
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
            if (_isOfflineWorkspace) { await EditOfflineRow(); return; }
            if (_activeVm == null || !_activeVm.HasValidKey) return;
            if (_rightPanel.DataTableGrid.SelectedItem is not DataRowView selectedRow) return;

            var editVm = _activeVm;
            var usesValueIdentity = editVm.UsesValueIdentity;
            var keyColumns = editVm.KeyColumns.ToList();
            var editColumns = editVm.Columns.ToList();
            var displayValues = new Dictionary<string, string>(editVm.CurrentRowValues);
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
            if (!ReferenceEquals(_activeVm, editVm) || editVm.CurrentTable != updateTable || _activeDbName != updateDbName ||
                !ReferenceEquals(_rightPanel.DataTableGrid.SelectedItem, selectedRow)) return;
            var plcColumn = editColumns.FirstOrDefault(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase));
            var recordColumn = editColumns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));
            Func<string, CancellationToken, Task<int>> suggestRecord = null;
            if (plcColumn is { IsReadOnly: false } && recordColumn is { IsIdentity: false, IsReadOnly: false })
            {
                var db = new DatabaseService(_connection.ToConnectionString(updateDbName));
                suggestRecord = (plc, ct) => db.GetFreeScopedRecordValueAsync(updateTable, recordColumn.Name, plcColumn.Name,
                    plc == null ? DBNull.Value : EditRowWindow.ConvertValue(plc, plcColumn), ct);
            }
            var source = new Configurator.Core.Projects.OnlineProjectDataSource(_connection.ToConnectionString(updateDbName), updateDbName);
            using var addressWorkspace = new Configurator.Core.Projects.ProjectWorkspace(source,
                architecture: new Configurator.Infrastructure.Projects.ProjectArchitectureDataSource(source.Server, source.Database));
            var editWindow = new EditRowWindow(
                updateTable,
                editColumns,
                displayValues,
                keyColumns.FirstOrDefault() ?? "",
                editClassNumberColumn,
                editClassNumber,
                editLookupOptions,
                suggestRecord, plc => PlcAddressCatalog.SuggestAddressesAsync(addressWorkspace, updateTable, plc, editClassNumber?.ToString(), default))
            { Owner = this };

            editWindow.CanApply = () => !_isOfflineWorkspace && _activeVm == editVm && _activeDbName == updateDbName;
            editWindow.SaveChangesAsync = async () =>
            {
                var relatedChanges = editWindow.Values
                    .Where(pair => oldValues.TryGetValue(pair.Key, out var oldValue) && !ValuesEqual(oldValue, pair.Value))
                    .Where(pair => editLookupOptions.ContainsKey(pair.Key) || IsLikelyRelatedField(pair.Key))
                    .Select(pair => $"{pair.Key}: {FormatLogValue(oldValues.TryGetValue(pair.Key, out var oldValue) ? oldValue : null)} → {FormatLogValue(pair.Value)}")
                    .ToList();
                if (relatedChanges.Count > 0 && Configurator.Infrastructure.ModernDialog.Show(editWindow,
                    "Вы изменяете связанные поля. Это может изменить привязки объекта к справочникам или другим объектам:\n\n" + string.Join("\n", relatedChanges) + "\n\nПродолжить изменение?",
                    "Предупреждение о смене связей", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;
                try
                {
                    await editVm.UpdateSnapshotAsync(updateTable, usesValueIdentity, keyColumns, oldIdentity, oldValues, editWindow.Values);
                    return true;
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    Configurator.Infrastructure.ModernDialog.Show(editWindow, "Такая комбинация ключевых полей уже существует. Измените значение и повторите сохранение.", "Конфликт ключа", MessageBoxButton.OK, MessageBoxImage.Warning);
                    foreach (var key in keyColumns.Where(editWindow.Values.ContainsKey))
                        editWindow.MarkConflict(key);
                    return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                    Configurator.Infrastructure.ModernDialog.Show(editWindow, "Проверьте введённые значения и повторите сохранение.", "Не удалось изменить запись", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            };
            await editWindow.ShowRowEditorAsync();

            if (!editWindow.Confirmed || editWindow.Values == null || editWindow.Values.Count == 0)
                return;

            try
            {
                var preferredIdentity = new Dictionary<string, object>(oldIdentity);
                foreach (var pair in editWindow.Values)
                    if (preferredIdentity.ContainsKey(pair.Key))
                        preferredIdentity[pair.Key] = pair.Value;

                var afterValues = new Dictionary<string, object>(oldValues);
                foreach (var pair in editWindow.Values) afterValues[pair.Key] = pair.Value;
                var afterIdentity = usesValueIdentity ? new Dictionary<string, object>(afterValues) :
                    keyColumns.ToDictionary(key => key, key => afterValues[key]);
                var changedDetails = string.Join("; ", editWindow.Values.Select(x => $"{x.Key}: {FormatLogValue(oldValues.TryGetValue(x.Key, out var ov) ? ov : null)} → {FormatLogValue(x.Value)}"));
                LogAction($"Изменена строка · {updateTable}", category: "Изменение", source: updateTable, details: changedDetails,
                journalChanges: new[]
                {
                    CreateJournalChange(updateTable, ChangeJournalOperationKind.Update, oldValues, afterValues, oldIdentity, afterIdentity)
                }, operationKind: ChangeJournalOperationKind.Update, databaseName: updateDbName);

                // Быстрое обновление: только текущая страница, без COUNT(*).
                if (ReferenceEquals(_activeVm, editVm) && editVm.CurrentTable == updateTable && _activeDbName == updateDbName &&
                    ReferenceEquals(_rightPanel.DataTableGrid.SelectedItem, selectedRow))
                {
                    if (editVm.ColumnView.HasFilters) await editVm.RefreshTotalCountAsync();
                    if (ReferenceEquals(_activeVm, editVm) && editVm.CurrentTable == updateTable && _activeDbName == updateDbName &&
                        ReferenceEquals(_rightPanel.DataTableGrid.SelectedItem, selectedRow))
                        await ReloadCurrentPageFastAsync(selectedIndex, preferredIdentity);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                Configurator.Infrastructure.ModernDialog.Show("Запись сохранена. Не удалось обновить её отображение или журнал.");
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
                var duplicateVm = _activeVm;
                var duplicateTable = duplicateVm.CurrentTable;
                var duplicateDbName = _activeDbName;
                var keys = duplicateVm.KeyColumns.ToList();
                var sourceIdentity = new Dictionary<string, object>(duplicateVm.UsesValueIdentity
                    ? duplicateVm.CurrentIdentityValues : duplicateVm.CurrentKeyValues);
                var values = await duplicateVm.DuplicateCurrentRowAsync(returnSnapshot: true);
                if (values.Count == 0)
                {
                    Configurator.Infrastructure.ModernDialog.Show("Нет данных для дублирования.");
                    return;
                }

                string newRecordMsg = "";
                if (duplicateVm.RecordColumn is string recordName && values.TryGetValue(recordName, out var recordValue))
                    newRecordMsg = $" Новый {recordName} = {recordValue}";

                var duplicateIdentity = keys.Count > 0 ? keys.ToDictionary(key => key, key => values[key]) : duplicateVm.BuildIdentityValues(values);
                string duplicateDetails = string.Join("; ", values.Select(x => $"{x.Key}={FormatLogValue(x.Value)}"));
                LogAction($"Дублирована строка · {duplicateTable}{newRecordMsg}", category: "Дублирование", source: duplicateTable, details: duplicateDetails,
                journalChanges: new[]
                {
                    CreateJournalChange(duplicateTable, ChangeJournalOperationKind.Duplicate, null, values, sourceIdentity, duplicateIdentity)
                }, operationKind: ChangeJournalOperationKind.Duplicate, databaseName: duplicateDbName);

                if (_activeVm != duplicateVm || duplicateVm.CurrentTable != duplicateTable || _activeDbName != duplicateDbName) return;
                duplicateVm.NotifyInserted(1);
                if (duplicateVm.ColumnView.HasFilters)
                    await duplicateVm.RefreshTotalCountAsync();
                if (_activeVm != duplicateVm || duplicateVm.CurrentTable != duplicateTable || _activeDbName != duplicateDbName) return;
                StatusText.Text = $"Копия строки добавлена.{newRecordMsg}";
                await ReloadCurrentPageFastAsync(-1, duplicateIdentity, copyRecordMessage: newRecordMsg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                Configurator.Infrastructure.ModernDialog.Show("Не удалось дублировать запись. Проверьте исходную строку и подключение к базе.");
            }
        }


        private static string FormatLogValue(object value)
            => value == null || value == DBNull.Value ? "NULL" : Convert.ToString(value);


    }
}
