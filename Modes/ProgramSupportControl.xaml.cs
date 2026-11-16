#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Configurator;

public partial class ProgramSupportControl : UserControl
{
    private string? _connectionString;
    private ProgramContext? _context;
    private ProgramSupportRepository? _repository;
    private ProgramSupportTableData? _tableData;
    private bool _busy;
    private bool _suppressSectionSelection;
    private bool _canAdd;
    private bool _canDelete;
    private bool _canSave;
    private string? _appliedSectionId;
    private int _contextVersion;
    private int _loadVersion;

    public ProgramSupportControl()
    {
        InitializeComponent();
        Loaded += ProgramSupportControl_Loaded;
        Unloaded += ProgramSupportControl_Unloaded;
        _suppressSectionSelection = true;
        SectionBox.ItemsSource = ProgramSupportCatalog.ProgramTables;
        SectionBox.SelectedValue = "recipes";
        _appliedSectionId = SectionBox.SelectedValue as string;
        _suppressSectionSelection = false;
        SetActions(false, false, false);
    }

    public async void Configure(string connectionString, ProgramContext? context)
    {
        if (!CommitGridEdits())
        {
            StatusText.Text = "Контекст не изменён: завершите редактирование текущей строки.";
            return;
        }
        if (HasPendingChanges())
        {
            StatusText.Text = "Контекст не изменён: сначала сохраните или отмените несохранённые правки.";
            return;
        }
        ++_contextVersion;
        ++_loadVersion;
        _connectionString = connectionString;
        _repository = string.IsNullOrWhiteSpace(connectionString) ? null : new ProgramSupportRepository(connectionString);
        if (context == null)
        {
            _context = null;
            ClearContext();
            return;
        }
        context = CopyContext(context);
        _context = context;
        _tableData = null;
        RowsGrid.ItemsSource = null;
        _appliedSectionId = SectionBox.SelectedValue as string;
        string sequence = context?.SeqRecordValue == null || context.SeqRecordValue == DBNull.Value ? "последовательность не выбрана" : $"Seq={context.SeqRecordValue}";
        ContextText.Text = context == null ? "Выберите программу" : $"{context.ContextText}  ·  {sequence}";
        await LoadSelectedAsync();
    }

    public void ClearContext()
    {
        if (!CommitGridEdits())
        {
            StatusText.Text = "Контекст не очищен: завершите редактирование текущей строки.";
            return;
        }
        if (HasPendingChanges())
        {
            StatusText.Text = "Контекст не очищен: сначала сохраните или отмените несохранённые правки.";
            return;
        }
        _contextVersion++;
        _loadVersion++;
        _context = null;
        _tableData = null;
        RowsGrid.ItemsSource = null;
        ContextText.Text = "Выберите программу и последовательность";
        StatusText.Text = "Контекст программы очищен.";
        SetActions(false, false, false);
        SetBusy(false);
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
            "В текущем разделе есть несохранённые изменения. Сохранить их перед переключением?",
            "Несохранённые изменения", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return await SavePendingChangesAsync();

        _tableData!.Rows.RejectChanges();
        return await LoadSelectedAsync(_appliedSectionId);
    }

    private async void SectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSectionSelection) return;
        string? requested = SectionBox.SelectedValue as string;
        if (requested == _appliedSectionId) return;
        if (_busy)
        {
            RestoreSectionSelection();
            return;
        }
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
        if (await LoadSelectedAsync(requested)) _appliedSectionId = requested;
        else RestoreSectionSelection();
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
        await LoadSelectedAsync(_appliedSectionId);
    }

    private async System.Threading.Tasks.Task<bool> LoadSelectedAsync(string? requestedId = null)
    {
        int contextVersion = _contextVersion;
        int loadVersion = ++_loadVersion;
        var repository = _repository;
        var context = _context;
        string? id = requestedId ?? SectionBox.SelectedValue as string;
        if (repository == null || context == null || id == null)
        {
            StatusText.Text = "Выберите подключение и контекст программы.";
            SetActions(false, false, false);
            SetBusy(false);
            return false;
        }
        SetBusy(true);
        SetActions(false, false, false);
        try
        {
            var tableData = await repository.LoadProgramAsync(id, context);
            if (contextVersion != _contextVersion || loadVersion != _loadVersion) return false;
            _tableData = tableData;
            RowsGrid.ItemsSource = _tableData.Rows.DefaultView;
            bool writable = _tableData.CanWrite;
            SetActions(writable && _tableData.Definition.AllowInsert, writable && _tableData.Definition.AllowDelete, writable);
            StatusText.Text = _tableData.WriteBlockReason ?? $"{_tableData.Definition.Caption}: {_tableData.Rows.Rows.Count} строк. Ключ и область записи сверены со схемой; изменения сохраняются транзакционно.";
            return true;
        }
        catch (Exception ex)
        {
            if (contextVersion == _contextVersion && loadVersion == _loadVersion) StatusText.Text = ex.Message;
            return false;
        }
        finally
        {
            if (contextVersion == _contextVersion && loadVersion == _loadVersion)
            {
                SetActionsFromCurrentTable();
                SetBusy(false);
            }
        }
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

    private void ProgramSupportControl_Loaded(object sender, RoutedEventArgs e)
    {
        UserAuthorizationService.Changed += UserAuthorization_Changed;
        UpdateActionState();
    }

    private void ProgramSupportControl_Unloaded(object sender, RoutedEventArgs e)
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
        _suppressSectionSelection = true;
        SectionBox.SelectedValue = _appliedSectionId;
        _suppressSectionSelection = false;
    }

    private async System.Threading.Tasks.Task<bool> SavePendingChangesAsync()
    {
        if (_repository == null || _tableData == null) return false;
        if (!CanSavePendingChanges(true)) return false;
        try
        {
            var context = _context ?? throw new InvalidOperationException("Контекст программы не выбран.");
            await _repository.SaveAsync(_tableData, ProgramSupportRepository.GetProgramScope(_tableData.Definition, context));
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

    private bool CommitGridEdits()
    {
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        return RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static ProgramContext CopyContext(ProgramContext context)
    {
        var copy = new ProgramContext
        {
            PLC = context.PLC,
            PlcValue = context.PlcValue,
            ClassNumberValue = context.ClassNumberValue,
            RecordValue = context.RecordValue,
            AreaValue = context.AreaValue,
            SeqRecordValue = context.SeqRecordValue
        };
        foreach (var pair in context.Values) copy.Values[pair.Key] = pair.Value;
        return copy;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !UserAuthorizationService.Require(Permission.AddData) || _context == null
            || _tableData == null || !_tableData.CanWrite || !_tableData.Definition.AllowInsert) return;
        try
        {
            var row = _tableData.Rows.NewRow();
            var scope = ProgramSupportRepository.GetProgramScope(_tableData.Definition, _context);
            foreach (var pair in scope) row[pair.Key] = pair.Value;
            foreach (DataColumn column in _tableData.Rows.Columns)
            {
                if (row[column] != DBNull.Value || column.AutoIncrement) continue;
                if (_tableData.Definition.KeyColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase)
                    && column.ColumnName.Equals("Record", StringComparison.OrdinalIgnoreCase))
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
            StatusText.Text = "Заполните поля новой строки и сохраните. Ключевые поля существующих строк менять нельзя.";
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
            StatusText.Text = "Строка помечена для удаления. Нажмите «Сохранить», чтобы применить изменение.";
            UpdateActionState();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _repository == null || _tableData == null) return;
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        if (!RowsGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        if (!CanSavePendingChanges(true)) return;
        SetBusy(true);
        if (await SavePendingChangesAsync()) await LoadSelectedAsync(_appliedSectionId);
        else SetBusy(false);
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
}
