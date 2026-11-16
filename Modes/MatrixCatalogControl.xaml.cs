#nullable enable
using System;
using System.Data;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Configurator;

public partial class MatrixCatalogControl : UserControl
{
    private string? _connectionString;
    private object? _plcValue;
    private object? _matrixClassValue;
    private ProgramSupportRepository? _repository;
    private ProgramSupportTableData? _tableData;
    private bool _busy;
    private bool _suppressSelectionEvents;
    private bool _canAdd;
    private bool _canDelete;
    private bool _canSave;
    private string? _appliedSectionId;
    private object? _appliedMatrixRecord;
    private int _contextVersion;
    private int _loadVersion;

    public MatrixCatalogControl()
    {
        InitializeComponent();
        Loaded += MatrixCatalogControl_Loaded;
        Unloaded += MatrixCatalogControl_Unloaded;
        _suppressSelectionEvents = true;
        SectionBox.ItemsSource = ProgramSupportCatalog.MatrixTables;
        SectionBox.SelectedValue = "program-matrix";
        _appliedSectionId = SectionBox.SelectedValue as string;
        _suppressSelectionEvents = false;
        SetActions(false, false, false);
    }

    public async void Configure(string connectionString, object plcValue, object matrixClassNumberValue)
    {
        if (!CommitGridEdits())
        {
            StatusText.Text = "Контекст Matrix не изменён: завершите редактирование текущей строки.";
            return;
        }
        if (HasPendingChanges())
        {
            StatusText.Text = "Контекст Matrix не изменён: сначала сохраните или отмените несохранённые правки.";
            return;
        }
        int contextVersion = ++_contextVersion;
        ++_loadVersion;
        _connectionString = connectionString;
        _plcValue = plcValue;
        _matrixClassValue = matrixClassNumberValue;
        _repository = string.IsNullOrWhiteSpace(connectionString) ? null : new ProgramSupportRepository(connectionString);
        _tableData = null;
        RowsGrid.ItemsSource = null;
        MatrixRecordBox.ItemsSource = null;
        _appliedMatrixRecord = null;
        _appliedSectionId = SectionBox.SelectedValue as string;
        SetBusy(false);
        SetActions(false, false, false);
        PlcInput.Text = Convert.ToString(plcValue, CultureInfo.InvariantCulture) ?? string.Empty;
        ContextText.Text = $"PLC={plcValue ?? "?"}  ·  Matrix class={matrixClassNumberValue ?? "?"}";
        if (await LoadMatrixRecordsAsync(contextVersion) && contextVersion == _contextVersion)
        {
            string? section = SectionBox.SelectedValue as string;
            if (await LoadSelectedAsync(section, _appliedMatrixRecord)) _appliedSectionId = section;
        }
    }

    public bool HasPendingChanges()
    {
        return _tableData?.Rows.Rows.Cast<DataRow>().Any(row =>
            row.RowState is DataRowState.Added or DataRowState.Modified or DataRowState.Deleted) == true;
    }

    public async System.Threading.Tasks.Task<bool> PrepareForContextChangeAsync()
    {
        if (!CommitGridEdits())
        {
            StatusText.Text = "Проверьте введённые значения перед переключением.";
            return false;
        }
        bool wasBusy = _busy;
        if (!wasBusy) SetBusy(true);
        try { return await PrepareForContextChangeCoreAsync(); }
        finally { if (!wasBusy) SetBusy(false); }
    }

    private async System.Threading.Tasks.Task<bool> PrepareForContextChangeCoreAsync()
    {
        if (!HasPendingChanges()) return true;
        var choice = MessageBox.Show(Window.GetWindow(this),
            "В текущем разделе Matrix есть несохранённые изменения. Сохранить их перед переключением?",
            "Несохранённые изменения", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return await SavePendingChangesAsync();

        _tableData!.Rows.RejectChanges();
        return await LoadSelectedAsync(_appliedSectionId, _appliedMatrixRecord);
    }

    private async System.Threading.Tasks.Task<bool> LoadMatrixRecordsAsync(int contextVersion)
    {
        int loadVersion = ++_loadVersion;
        var repository = _repository;
        var plcValue = _plcValue;
        var matrixClassValue = _matrixClassValue;
        if (repository == null || plcValue == null || matrixClassValue == null || plcValue == DBNull.Value || matrixClassValue == DBNull.Value)
        {
            if (IsCurrent(contextVersion, loadVersion)) MatrixRecordBox.ItemsSource = null;
            return IsCurrent(contextVersion, loadVersion);
        }
        SetBusy(true);
        SetActions(false, false, false);
        try
        {
            var root = await repository.LoadMatrixAsync("matrix-list", plcValue, matrixClassValue);
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            var options = (root.MatrixListRows ?? Array.Empty<MatrixListRow>()).Select(row => new MatrixRecordOption
            {
                Record = row.Key.Record,
                Caption = $"{row.Key.Record} · {row.Name_L1}"
            }).ToList();
            object? previousRecord = _appliedMatrixRecord;
            _suppressSelectionEvents = true;
            MatrixRecordBox.ItemsSource = options;
            if (options.Count > 0 && !options.Any(option => Equals(option.Record, MatrixRecordBox.SelectedValue)))
                MatrixRecordBox.SelectedValue = options[0].Record;
            else if (options.Count == 0)
                MatrixRecordBox.SelectedValue = null;
            object? selectedRecord = MatrixRecordBox.SelectedValue;
            _appliedMatrixRecord = MatrixRecordBox.SelectedValue;
            _suppressSelectionEvents = false;
            if (!Equals(previousRecord, selectedRecord) && IsMatrixChild(_appliedSectionId))
            {
                _tableData = null;
                RowsGrid.ItemsSource = null;
                SetActions(false, false, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            _suppressSelectionEvents = true;
            MatrixRecordBox.ItemsSource = null;
            MatrixRecordBox.SelectedValue = null;
            _appliedMatrixRecord = null;
            _suppressSelectionEvents = false;
            _tableData = null;
            RowsGrid.ItemsSource = null;
            StatusText.Text = ex.Message;
            SetActions(false, false, false);
            return false;
        }
        finally
        {
            if (IsCurrent(contextVersion, loadVersion))
            {
                UpdateMatrixRecordVisibility();
                SetBusy(false);
            }
        }
    }

    private async void SectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents) return;
        string? requested = SectionBox.SelectedValue as string;
        if (requested == _appliedSectionId) return;
        if (_busy) { RestoreSectionSelection(); return; }
        if (!CommitGridEdits())
        {
            StatusText.Text = "Проверьте введённые значения перед переключением.";
            RestoreSectionSelection();
            return;
        }
        int contextVersion = _contextVersion;
        SetBusy(true);
        if (!await PrepareForContextChangeCoreAsync())
        {
            RestoreSectionSelection();
            SetBusy(false);
            return;
        }
        if (contextVersion != _contextVersion) return;
        UpdateMatrixRecordVisibility();
        bool loaded = await LoadSelectedAsync(requested, _appliedMatrixRecord);
        if (contextVersion != _contextVersion) return;
        if (loaded) _appliedSectionId = requested;
        else RestoreSectionSelection();
    }

    private async void MatrixRecordBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents || !IsMatrixChildSelected()) return;
        object? requested = MatrixRecordBox.SelectedValue;
        if (Equals(requested, _appliedMatrixRecord)) return;
        if (_busy) { RestoreMatrixRecordSelection(); return; }
        if (!CommitGridEdits())
        {
            StatusText.Text = "Проверьте введённые значения перед переключением.";
            RestoreMatrixRecordSelection();
            return;
        }
        int contextVersion = _contextVersion;
        SetBusy(true);
        if (!await PrepareForContextChangeCoreAsync())
        {
            RestoreMatrixRecordSelection();
            SetBusy(false);
            return;
        }
        if (contextVersion != _contextVersion) return;
        bool loaded = await LoadSelectedAsync(_appliedSectionId, requested);
        if (contextVersion != _contextVersion) return;
        if (loaded) _appliedMatrixRecord = requested;
        else RestoreMatrixRecordSelection();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!CommitGridEdits())
        {
            StatusText.Text = "Проверьте введённые значения перед обновлением.";
            return;
        }
        int contextVersion = _contextVersion;
        SetBusy(true);
        if (!await PrepareForContextChangeCoreAsync()) { SetBusy(false); return; }
        if (contextVersion != _contextVersion) return;
        if (await LoadMatrixRecordsAsync(contextVersion) && contextVersion == _contextVersion)
        {
            string? section = _appliedSectionId;
            object? record = _appliedMatrixRecord;
            if (await LoadSelectedAsync(section, record))
            {
                _appliedSectionId = section;
                _appliedMatrixRecord = MatrixRecordBox.SelectedValue;
            }
        }
    }

    private void LoadPlc_Click(object sender, RoutedEventArgs e) => TryLoadPlc();

    private void PlcInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        TryLoadPlc();
    }

    private async void TryLoadPlc()
    {
        if (_busy || _repository == null || _connectionString == null)
        {
            StatusText.Text = "Сначала загрузите подключение и класс Matrix.";
            return;
        }
        if (!CommitGridEdits())
        {
            StatusText.Text = "Завершите редактирование текущей строки перед сменой PLC.";
            return;
        }
        if (!int.TryParse(PlcInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plcValue))
        {
            StatusText.Text = "Введите целочисленный номер PLC.";
            return;
        }
        if (_matrixClassValue == null || _matrixClassValue == DBNull.Value)
        {
            StatusText.Text = "Не задан класс Matrix для загрузки.";
            return;
        }
        int contextVersion = _contextVersion;
        SetBusy(true);
        if (!await PrepareForContextChangeCoreAsync())
        {
            PlcInput.Text = Convert.ToString(_plcValue, CultureInfo.InvariantCulture) ?? string.Empty;
            SetBusy(false);
            return;
        }
        if (contextVersion != _contextVersion) return;
        Configure(_connectionString, plcValue, _matrixClassValue);
    }

    private async System.Threading.Tasks.Task<bool> LoadSelectedAsync(string? requestedId = null, object? requestedMatrixRecord = null)
    {
        int contextVersion = _contextVersion;
        int loadVersion = ++_loadVersion;
        var repository = _repository;
        string? id = requestedId ?? SectionBox.SelectedValue as string;
        object? plcValue = _plcValue;
        object? matrixClassValue = _matrixClassValue;
        bool childSelected = IsMatrixChild(id);
        object? matrixRecord = childSelected ? requestedMatrixRecord ?? MatrixRecordBox.SelectedValue : null;
        if (repository == null || plcValue == null || matrixClassValue == null || plcValue == DBNull.Value || matrixClassValue == DBNull.Value || id == null)
        {
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            StatusText.Text = "Выберите подключение и контекст класса Matrix.";
            SetActions(false, false, false);
            SetBusy(false);
            return false;
        }
        SetBusy(true);
        SetActions(false, false, false);
        try
        {
            var tableData = await repository.LoadMatrixAsync(id, plcValue, matrixClassValue, matrixRecord);
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            _tableData = tableData;
            RowsGrid.ItemsSource = _tableData.Rows.DefaultView;
            bool writable = _tableData.CanWrite;
            SetActions(writable && _tableData.Definition.AllowInsert, writable && _tableData.Definition.AllowDelete, writable);
            StatusText.Text = _tableData.WriteBlockReason ?? $"{_tableData.Definition.Caption}: {_tableData.Rows.Rows.Count} строк. Таблица и unique key сверены с backup-схемой.";
            return true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            StatusText.Text = ex.Message;
            SetActionsFromCurrentTable();
            return false;
        }
        finally
        {
            if (IsCurrent(contextVersion, loadVersion))
            {
                UpdateMatrixRecordVisibility();
                SetBusy(false);
            }
        }
    }

    private bool IsCurrent(int contextVersion, int loadVersion) =>
        contextVersion == _contextVersion && loadVersion == _loadVersion;

    private bool IsMatrixChildSelected() => IsMatrixChild(SectionBox.SelectedValue as string);

    private bool IsMatrixChild(string? id) => id != null &&
        ProgramSupportCatalog.MatrixTables.FirstOrDefault(table => table.Id == id)?.ScopeColumns.Contains("Record", StringComparer.OrdinalIgnoreCase) == true;

    private void UpdateMatrixRecordVisibility()
    {
        var visibility = IsMatrixChildSelected() ? Visibility.Visible : Visibility.Collapsed;
        MatrixRecordLabel.Visibility = MatrixRecordBox.Visibility = visibility;
    }

    private void SetActions(bool add, bool delete, bool save)
    {
        _canAdd = add;
        _canDelete = delete;
        _canSave = save;
        UpdateActionState();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RowsGrid.IsEnabled = !busy;
        SectionBox.IsEnabled = !busy;
        MatrixRecordBox.IsEnabled = !busy;
        PlcInput.IsEnabled = !busy;
        LoadPlcButton.IsEnabled = !busy && _repository != null;
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        bool canEdit = UserAuthorizationService.Has(Permission.EditData);
        bool canAdd = UserAuthorizationService.Has(Permission.AddData);
        RowsGrid.IsReadOnly = _tableData?.CanWrite != true || (!canEdit && !canAdd);
        AddButton.IsEnabled = !_busy && _canAdd && canAdd;
        DeleteButton.IsEnabled = !_busy && CanDeleteSelected();
        SaveButton.IsEnabled = !_busy && _canSave && CanSavePendingChanges(false);
        RefreshButton.IsEnabled = !_busy && _repository != null;
    }

    private void MatrixCatalogControl_Loaded(object sender, RoutedEventArgs e)
    {
        UserAuthorizationService.Changed += UserAuthorization_Changed;
        UpdateActionState();
    }

    private void MatrixCatalogControl_Unloaded(object sender, RoutedEventArgs e)
        => UserAuthorizationService.Changed -= UserAuthorization_Changed;

    private void UserAuthorization_Changed(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) UpdateActionState();
        else Dispatcher.Invoke(UpdateActionState);
    }

    private bool CanDeleteSelected()
    {
        if (!_canDelete || RowsGrid.SelectedItem is not DataRowView selected) return false;
        return selected.Row.RowState == DataRowState.Added
            ? UserAuthorizationService.Has(Permission.AddData)
            : UserAuthorizationService.Has(Permission.DeleteData);
    }

    private bool CanSavePendingChanges(bool showAuthorizationMessage)
    {
        if (_tableData == null || !_tableData.CanWrite) return false;
        bool hasChanges = false;
        foreach (DataRow row in _tableData.Rows.Rows)
        {
            Permission? required = row.RowState switch
            {
                DataRowState.Added => Permission.AddData,
                DataRowState.Modified => Permission.EditData,
                DataRowState.Deleted => Permission.DeleteData,
                _ => null
            };
            if (required == null) continue;
            hasChanges = true;
            if (!UserAuthorizationService.Has(required.Value))
            {
                if (showAuthorizationMessage) UserAuthorizationService.Require(required.Value);
                return false;
            }
        }
        return hasChanges;
    }

    private void RowsGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateActionState));

    private void RowsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateActionState();

    private void SetActionsFromCurrentTable()
    {
        var tableData = _tableData;
        if (tableData == null) { SetActions(false, false, false); return; }
        bool writable = tableData.CanWrite;
        SetActions(writable && tableData.Definition.AllowInsert,
            writable && tableData.Definition.AllowDelete, writable);
    }

    private void RestoreSectionSelection()
    {
        _suppressSelectionEvents = true;
        SectionBox.SelectedValue = _appliedSectionId;
        _suppressSelectionEvents = false;
        UpdateMatrixRecordVisibility();
    }

    private void RestoreMatrixRecordSelection()
    {
        _suppressSelectionEvents = true;
        MatrixRecordBox.SelectedValue = _appliedMatrixRecord;
        _suppressSelectionEvents = false;
    }

    private bool CommitGridEdits()
    {
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        return RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private async System.Threading.Tasks.Task<bool> SavePendingChangesAsync()
    {
        if (_repository == null || _tableData == null) return false;
        if (!CanSavePendingChanges(true)) return false;
        try
        {
            await _repository.SaveAsync(_tableData, MatrixScope(_tableData.Definition, _appliedMatrixRecord));
            _tableData.Rows.AcceptChanges();
            StatusText.Text = "Изменения сохранены.";
            UpdateActionState();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Изменения не сохранены. Транзакция отменена: {ex.Message}";
            return false;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !UserAuthorizationService.Require(Permission.AddData)
            || _tableData == null || !_tableData.CanWrite || !_tableData.Definition.AllowInsert) return;
        try
        {
            var row = _tableData.Rows.NewRow();
            var scope = MatrixScope(_tableData.Definition, _appliedMatrixRecord);
            foreach (var pair in scope) row[pair.Key] = pair.Value;
            foreach (DataColumn column in _tableData.Rows.Columns)
            {
                if (row[column] != DBNull.Value || column.AutoIncrement) continue;
                if (_tableData.Definition.KeyColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase)
                    && column.ColumnName.Equals("Record", StringComparison.OrdinalIgnoreCase)
                    && !_tableData.Definition.ScopeColumns.Contains("Record", StringComparer.OrdinalIgnoreCase))
                {
                    long max = _tableData.Rows.Rows.Cast<DataRow>()
                        .Where(existing => existing.RowState != DataRowState.Deleted && existing[column] != DBNull.Value)
                        .Select(existing => long.TryParse(Convert.ToString(existing[column]), out var value) ? value : 0)
                        .DefaultIfEmpty(0).Max();
                    row[column] = Convert.ChangeType(max + 1, column.DataType, System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (!_tableData.DefaultColumns.Contains(column.ColumnName) && !column.AllowDBNull && column.DataType == typeof(string))
                    row[column] = string.Empty;
                else if (!_tableData.DefaultColumns.Contains(column.ColumnName) && !column.AllowDBNull && column.DataType.IsValueType)
                    row[column] = Activator.CreateInstance(column.DataType);
            }
            _tableData.Rows.Rows.Add(row);
            var item = RowsGrid.Items[RowsGrid.Items.Count - 1];
            RowsGrid.SelectedItem = item;
            RowsGrid.ScrollIntoView(item);
            StatusText.Text = "Заполните ключевые поля новой строки и сохраните. Связи, заданные внешними ключами, проверит SQL в транзакции.";
            UpdateActionState();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _tableData == null || !_tableData.CanWrite || !_tableData.Definition.AllowDelete
            || RowsGrid.SelectedItem is not DataRowView selected) return;
        Permission required = selected.Row.RowState == DataRowState.Added ? Permission.AddData : Permission.DeleteData;
        if (!UserAuthorizationService.Require(required)) return;
        try
        {
            if (selected.Row.RowState == DataRowState.Added) selected.Row.Table.Rows.Remove(selected.Row);
            else selected.Row.Delete();
            StatusText.Text = "Строка помечена для удаления. Перед удалением корня проверяются его зависимости.";
            UpdateActionState();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _repository == null || _tableData == null || !CommitGridEdits()) return;
        if (!CanSavePendingChanges(true)) return;
        int contextVersion = _contextVersion;
        SetBusy(true);
        if (!await SavePendingChangesAsync()) { SetBusy(false); return; }
        if (contextVersion != _contextVersion) return;
        if (await LoadMatrixRecordsAsync(contextVersion) && contextVersion == _contextVersion)
            await LoadSelectedAsync(_appliedSectionId, _appliedMatrixRecord);
    }

    private System.Collections.Generic.IReadOnlyDictionary<string, object> MatrixScope(ProgramSupportTableDefinition definition, object? matrixRecord = null)
    {
        var scope = new System.Collections.Generic.Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = _plcValue ?? throw new InvalidOperationException("Не выбран PLC."),
            ["PLC_Class_Number"] = _matrixClassValue ?? throw new InvalidOperationException("Не выбран класс Matrix.")
        };
        if (definition.ScopeColumns.Contains("Record", StringComparer.OrdinalIgnoreCase))
            scope["Record"] = matrixRecord ?? _appliedMatrixRecord ?? throw new InvalidOperationException("Сначала выберите строку Matrix_List.");
        return scope;
    }

    private void RowsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (_tableData == null) { e.Column.IsReadOnly = true; return; }
        string name = e.PropertyName;
        bool inKey = _tableData.Definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool inScope = _tableData.Definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool eligible = _tableData.Definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
            || (inKey && !inScope && !name.Equals("Record", StringComparison.OrdinalIgnoreCase));
        e.Column.IsReadOnly = !_tableData.CanWrite || !eligible;
    }

    private void RowsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_busy || _tableData == null || !_tableData.CanWrite || e.Row.Item is not DataRowView row) { e.Cancel = true; return; }
        Permission required = row.Row.RowState == DataRowState.Added ? Permission.AddData : Permission.EditData;
        if (!UserAuthorizationService.Has(required)) { e.Cancel = true; return; }
        string? name = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(name) && e.Column is DataGridBoundColumn bound && bound.Binding is Binding binding)
            name = binding.Path?.Path;
        if (string.IsNullOrWhiteSpace(name)) { e.Cancel = true; return; }
        bool isNew = row.Row.RowState == DataRowState.Added;
        bool isScope = _tableData.Definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool isKey = _tableData.Definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool protectedColumn = _tableData.Definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool editable = _tableData.Definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (isScope || (isKey && !isNew) || (protectedColumn && !isNew) || (!editable && !(isNew && isKey))) e.Cancel = true;
    }

    private sealed class MatrixRecordOption
    {
        public object Record { get; init; } = DBNull.Value;
        public string Caption { get; init; } = "";
    }
}
