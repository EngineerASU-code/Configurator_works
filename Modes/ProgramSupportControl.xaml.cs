#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace Configurator;

public partial class ProgramSupportControl : UserControl
{
    private Configurator.Core.Projects.OfflineProjectDataSource? _offlineSource;
    private ProgramSupportControl? _recipeDetails;
    private ProgramSupportControl? _recipeOwner;
    private bool _selectingRecipe;
    private int? _recipeRecord;
    private (string Column, int Record)? _detailFilter;
    private DataView? _queueChoices;
    private DataTable? _baseRecipeValues;
    public event Action<ChangeJournalEntry>? ChangeJournalRequested;
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

    internal bool LoadWhenVisible { get; set; }
    private bool _deferredLoad;

    public ProgramSupportControl()
    {
        InitializeComponent();
        RowsGrid.PreviewKeyDown += (_, e) => Mode3DeleteShortcut.Handle(e, DeleteButton);
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible || !_deferredLoad) return;
            _deferredLoad = false;
            await LoadSelectedAsync();
        };
        Loaded += ProgramSupportControl_Loaded;
        Unloaded += ProgramSupportControl_Unloaded;
        _suppressSectionSelection = true;
        SectionBox.ItemsSource = ProgramSupportCatalog.ProgramTables;
        SectionBox.SelectedValue = "recipes";
        _appliedSectionId = SectionBox.SelectedValue as string;
        _suppressSectionSelection = false;
        SetActions(false, false, false);
    }

    public void SetSections(params string[] ids)
    {
        _suppressSectionSelection = true;
        SectionBox.ItemsSource = ProgramSupportCatalog.ProgramTables.Where(section => ids.Contains(section.Id)).ToArray();
        SectionBox.SelectedIndex = 0;
        SectionLabel.Visibility = SectionBox.Visibility = ids.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        _appliedSectionId = SectionBox.SelectedValue as string;
        _suppressSectionSelection = false;
    }

    public async void Configure(string connectionString, ProgramContext? context)
        => await ConfigureCoreAsync(connectionString, null, context);

    public async void Configure(Configurator.Core.Projects.OfflineProjectDataSource source, ProgramContext? context)
        => await ConfigureCoreAsync(null, source, context);

    private async Task ConfigureCoreAsync(string? connectionString, Configurator.Core.Projects.OfflineProjectDataSource? offline, ProgramContext? context)
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
        _offlineSource = offline;
        _repository = offline != null ? new ProgramSupportRepository(offline)
            : string.IsNullOrWhiteSpace(connectionString) ? null : new ProgramSupportRepository(connectionString);
        if (_repository != null) _repository.ChangeJournalRequested += entry => ChangeJournalRequested?.Invoke(entry);
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
        RecipeDetailsGroup.Content = null;
        _recipeDetails = null;
        _selectingRecipe = true;
        RecipePicker.ItemsSource = null;
        _selectingRecipe = false;
        SetActions(false, false, false);
        SetBusy(false);
        _deferredLoad = LoadWhenVisible && !IsVisible;
        if (!_deferredLoad) await LoadSelectedAsync();
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
        _deferredLoad = false;
        _tableData = null;
        RowsGrid.ItemsSource = null;
        _selectingRecipe = true;
        RecipePicker.ItemsSource = null;
        RecipeDetailsGroup.Content = null;
        _recipeDetails = null;
        _recipeRecord = null;
        _selectingRecipe = false;
        ContextText.Text = "Выберите программу и последовательность";
        StatusText.Text = "Контекст программы очищен.";
        SetActions(false, false, false);
        SetBusy(false);
    }

    public bool HasPendingChanges()
    {
        return _recipeDetails?.HasPendingChanges() == true || _tableData?.Rows.Rows.Cast<DataRow>().Any(row =>
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
        if (_recipeDetails != null && !_recipeDetails.CommitGridEdits()) return false;
        if (!HasPendingChanges()) return true;

        var choice = Configurator.Infrastructure.ModernDialog.Show(Window.GetWindow(this),
            "В текущем разделе есть несохранённые изменения. Сохранить их перед переключением?",
            "Несохранённые изменения", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice is MessageBoxResult.Cancel or MessageBoxResult.None) return false;
        if (choice == MessageBoxResult.Yes) return await SavePendingChangesAsync();

        _recipeDetails?._tableData?.Rows.RejectChanges();
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
        if (ProgramSupportCatalog.ProgramTables.First(section => section.Id == id).ScopeColumns.Contains("Seq_Record") &&
            context.SeqRecordValue is null or DBNull)
        {
            _tableData = null;
            RowsGrid.ItemsSource = null;
            StatusText.Text = "Выберите последовательность.";
            SetActions(false, false, false);
            SetBusy(false);
            return true;
        }
        SetBusy(true);
        SetActions(false, false, false);
        try
        {
            var tableData = id == "recipe-values" && _detailFilter is { Column: "Recipe_Record" } recipe
                ? await repository.LoadRecipeValuesAsync(context, recipe.Record)
                : await repository.LoadProgramAsync(id, context);
            var baseRecipe = id == "recipe-values" && _detailFilter is { Column: "Recipe_Record", Record: > 0 }
                ? await repository.LoadRecipeValuesAsync(context, 0) : null;
            var queues = id == "queue-selections" ? await repository.LoadProgramAsync("queues", context) : null;
            if (contextVersion != _contextVersion || loadVersion != _loadVersion) return false;
            _tableData = tableData;
            _queueChoices = queues?.Rows.DefaultView;
            _baseRecipeValues = baseRecipe?.Rows;
            var view = _tableData.Rows.DefaultView;
            if (_detailFilter is { } filter) view.RowFilter = $"[{filter.Column}] = {filter.Record}";
            RowsGrid.ItemsSource = view;
            ToggleRecipeButton.Visibility = id == "recipe-values" ? Visibility.Visible : Visibility.Collapsed;
            if (id == "recipes")
            {
                RecipePicker.Visibility = Visibility.Visible;
                RecipeDetailsGroup.Visibility = Visibility.Visible;
                RecipeDetailsHeight.Height = new GridLength(1, GridUnitType.Star);
                RecordsHeight.Height = GridLength.Auto;
                RowsGrid.MaxHeight = 125;
                _selectingRecipe = true;
                RecipePicker.ItemsSource = view;
                RecipePicker.SelectedItem = view.Cast<DataRowView>().FirstOrDefault(row =>
                    Convert.ToString(row["Record"]) == _recipeRecord?.ToString()) ?? view.Cast<DataRowView>().FirstOrDefault();
                _selectingRecipe = false;
                await SelectRecipeAsync();
            }
            if (contextVersion != _contextVersion || loadVersion != _loadVersion) return false;
            bool writable = _tableData.CanWrite;
            SetActions(writable && _tableData.Definition.AllowInsert, writable && _tableData.Definition.AllowDelete, writable);
            StatusText.Text = _tableData.WriteBlockReason ?? "";
            return true;
        }
        catch (Exception ex)
        {
            if (contextVersion == _contextVersion && loadVersion == _loadVersion) StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex);
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
        RecipeDetailsGroup.IsEnabled = !busy;
        RecipePicker.IsEnabled = !busy && !_selectingRecipe;
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        bool canEdit = UserAuthorizationService.Has(Permission.EditData);
        bool canAdd = UserAuthorizationService.Has(Permission.AddData);
        RowsGrid.IsReadOnly = _tableData?.CanWrite != true || (!canEdit && !canAdd);
        AddButton.IsEnabled = !_busy && _canAdd && canAdd;
        DeleteButton.IsEnabled = !_busy && CanDeleteSelected();
        SaveButton.IsEnabled = !_busy && ((_canSave && CanSavePendingChanges(false)) || _recipeDetails?.SaveButton.IsEnabled == true);
        _recipeOwner?.UpdateActionState();
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
        if (ProgramSupportRepository.HasStagedDeletionReplacements(_tableData) && !UserAuthorizationService.Has(Permission.EditData))
        {
            if (showAuthorizationMessage) UserAuthorizationService.Require(Permission.EditData);
            return false;
        }
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
        if (_recipeOwner != null) return await _recipeOwner.SavePendingChangesAsync();
        if (_repository == null || _tableData == null) return false;
        var tables = new List<ProgramSupportTableData>();
        foreach (var editor in new[] { _recipeDetails, this })
        {
            if (editor?._tableData == null || !editor._tableData.Rows.Rows.Cast<DataRow>().Any(row => row.RowState != DataRowState.Unchanged)) continue;
            if (!editor.CanSavePendingChanges(true)) return false;
            tables.Add(editor._tableData);
        }
        if (tables.Count == 0) return false;
        try
        {
            var context = _context ?? throw new InvalidOperationException("Контекст программы не выбран.");
            await _repository.SaveTogetherAsync(ProgramSupportRepository.GetProgramScope(_tableData.Definition, context), tables.ToArray());
            foreach (var table in tables) table.Rows.AcceptChanges();
            _recipeDetails?.UpdateActionState();
            StatusText.Text = "Изменения сохранены.";
            UpdateActionState();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Изменения не сохранены. Транзакция отменена: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
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

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !UserAuthorizationService.Require(Permission.AddData) || _context == null
            || _tableData == null || !_tableData.CanWrite || !_tableData.Definition.AllowInsert) return;
        try
        {
            if (_recipeDetails != null && !await _recipeDetails.PrepareForContextChangeAsync()) return;
            var row = _tableData.Rows.NewRow();
            var scope = ProgramSupportRepository.GetProgramScope(_tableData.Definition, _context);
            foreach (var pair in scope) row[pair.Key] = pair.Value;
            if (_appliedSectionId == "queue-selections")
            {
                var queue = _detailFilter?.Record;
                if (queue == null && _queueChoices is { Count: 1 }) queue = Convert.ToInt32(_queueChoices[0]["Record"]);
                if (queue == null) throw new InvalidOperationException("Откройте нужную очередь двойным кликом.");
                row["Queue_Record"] = queue.Value;
            }
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
            if (_appliedSectionId == "recipes")
            {
                _selectingRecipe = true;
                RecipePicker.SelectedItem = RecipePicker.Items.OfType<DataRowView>().First(view => ReferenceEquals(view.Row, row));
                _selectingRecipe = false;
                await SelectRecipeAsync();
            }
            var item = RowsGrid.Items.OfType<DataRowView>().FirstOrDefault(view => ReferenceEquals(view.Row, row));
            if (item == null) return;
            RowsGrid.SelectedItem = item;
            RowsGrid.ScrollIntoView(item);
            StatusText.Text = "Заполните поля новой строки и сохраните. Ключевые поля существующих строк менять нельзя.";
            UpdateActionState();
        }
        catch (Exception ex) { StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _repository == null || _context == null || _tableData == null || !_tableData.CanWrite ||
            !_tableData.Definition.AllowDelete || !CommitGridEdits() || RowsGrid.SelectedItem is not DataRowView selected) return;
        var tableData = _tableData;
        var repository = _repository;
        var context = _context;
        var row = selected.Row;
        int contextVersion = _contextVersion;
        bool IsCurrent() => contextVersion == _contextVersion && ReferenceEquals(tableData, _tableData);

        SetBusy(true);
        try
        {
            if (_recipeDetails?.HasPendingChanges() == true && !await _recipeDetails.PrepareForContextChangeAsync()) return;
            if (!IsCurrent()) return;
            var scope = ProgramSupportRepository.GetProgramScope(tableData.Definition, context);
            bool added = row.RowState == DataRowState.Added;
            if (await ProgramSupportDeletionInteraction.StageAsync(Window.GetWindow(this), repository, tableData, row, scope, IsCurrent))
                StatusText.Text = added ? "Новая строка удалена." : "Строка помечена для удаления. Нажмите «Сохранить», чтобы применить изменение.";
        }
        catch (Exception ex)
        {
            if (IsCurrent()) StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex);
        }
        finally
        {
            if (IsCurrent()) SetBusy(false);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveCurrentAsync();

    public async Task SaveCurrentAsync()
    {
        if (_busy || _repository == null || _tableData == null || !CommitGridEdits()) return;
        if (_recipeOwner != null) { await _recipeOwner.SaveCurrentAsync(); return; }
        if (_recipeDetails != null && !_recipeDetails.CommitGridEdits()) return;
        if (!HasPendingChanges()) return;
        SetBusy(true);
        if (await SavePendingChangesAsync()) await LoadSelectedAsync(_appliedSectionId);
        else SetBusy(false);
    }

    private async void RecipePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_selectingRecipe && !_busy) await SelectRecipeAsync();
    }

    private async Task SelectRecipeAsync()
    {
        _selectingRecipe = true;
        RecipePicker.IsEnabled = false;
        try
        {
            if (_recipeDetails != null && !await _recipeDetails.PrepareForContextChangeAsync())
            {
                RecipePicker.SelectedItem = RecipePicker.Items.OfType<DataRowView>().FirstOrDefault(row =>
                    Convert.ToString(row["Record"]) == _recipeRecord?.ToString());
                return;
            }
            _recipeDetails = null;
            RecipeDetailsGroup.Content = null;
            if (RecipePicker.SelectedItem is not DataRowView selected || _tableData == null)
            {
                if (_tableData != null) RowsGrid.ItemsSource = new DataView(_tableData.Rows) { RowFilter = "1 = 0" };
                _recipeRecord = null;
                return;
            }
            int record = Convert.ToInt32(selected["Record"]);
            _recipeRecord = record;
            RowsGrid.ItemsSource = new DataView(_tableData.Rows) { RowFilter = $"[Record] = {record}" };
            RowsGrid.SelectedIndex = 0;
            if (selected.Row.RowState == DataRowState.Added) return;
            var editor = CreateDetails("recipe-values", "Recipe_Record", record);
            editor._recipeOwner = this;
            _recipeDetails = editor;
            RecipeDetailsGroup.Content = editor;
            await editor.ConfigureCoreAsync(_connectionString, _offlineSource, _context);
        }
        finally
        {
            _selectingRecipe = false;
            RecipePicker.IsEnabled = true;
        }
    }

    private ProgramSupportControl CreateDetails(string section, string column, int record)
    {
        var editor = new ProgramSupportControl { _detailFilter = (column, record) };
        editor.SetSections(section);
        editor.ContextText.Visibility = Visibility.Collapsed;
        editor.ChangeJournalRequested += entry => ChangeJournalRequested?.Invoke(entry);
        return editor;
    }

    private async void RowsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _tableData == null || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(RowsGrid, source) is not DataGridRow { Item: DataRowView row }) return;
        if (!int.TryParse(Convert.ToString(row["Record"]), out int record)) return;
        if (_recipeOwner != null && _recipeOwner.RecipePicker.SelectedItem is DataRowView recipe)
        {
            e.Handled = true;
            _recipeOwner.EditRecipe(recipe, row.Row);
            return;
        }
        if (_appliedSectionId == "queues" && row.Row.RowState != DataRowState.Added)
        {
            e.Handled = true;
            if (!await PrepareForContextChangeAsync()) return;
            var details = CreateDetails("queue-selections", "Queue_Record", record);
            await details.ConfigureCoreAsync(_connectionString, _offlineSource, _context);
            var dialog = new Window
            {
                Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Title = $"Очередь № {record}", Content = details,
                Width = Math.Min(1280, SystemParameters.WorkArea.Width),
                Height = Math.Min(820, SystemParameters.WorkArea.Height)
            };
            dialog.Closing += (_, args) =>
            {
                args.Cancel = details._busy || (details.HasPendingChanges() &&
                    Configurator.Infrastructure.ModernDialog.Show(dialog, "Закрыть окно и отменить несохранённые правки?",
                        "Несохранённые изменения", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes);
            };
            dialog.ShowDialog();
            return;
        }
        if (_appliedSectionId is not ("recipes" or "recipe-values" or "queue-selections" or "softkey" or "messages")) return;
        e.Handled = true;
        if (_appliedSectionId == "recipes")
        {
            EditRecipe(row, null);
            return;
        }
        var permission = row.Row.RowState == DataRowState.Added ? Permission.AddData : Permission.EditData;
        if (!_canSave || !UserAuthorizationService.Require(permission) || !CommitGridEdits()) return;
        EditFields(row, record);
    }

    private void EditRecipe(DataRowView recipe, DataRow? selectedParameter)
    {
        var permission = recipe.Row.RowState == DataRowState.Added ? Permission.AddData : Permission.EditData;
        if (_busy || _selectingRecipe || !_canSave || !UserAuthorizationService.Require(permission) || !CommitGridEdits()) return;
        EditFields(recipe, Convert.ToInt32(recipe["Record"]), selectedParameter);
    }

    internal void EditFields(DataRowView row, int record, DataRow? selectedParameter = null)
    {
        var fields = new UniformGrid { Columns = 2, Margin = new Thickness(12) };
        var editors = new Dictionary<DataColumn, Control>();
        bool added = row.Row.RowState == DataRowState.Added;
        foreach (DataColumn column in row.Row.Table.Columns)
        {
            string name = column.ColumnName;
            if (column.ReadOnly || (!_tableData!.Definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                !(added && _tableData.Definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase)))) continue;
            if (name == "UseRecipes" && !IsDefaultRecipe(row)) continue;
            Control editor = name == "UseRecipes" || column.DataType == typeof(bool)
                ? new CheckBox { IsChecked = row[name] != DBNull.Value && Convert.ToBoolean(row[name]) }
                : new TextBox { Text = Convert.ToString(row[name]) ?? "" };
            editor.IsEnabled = !_tableData!.ComputedColumns.Contains(name) && !_tableData.IdentityColumns.Contains(name) &&
                (added || !_tableData.Definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase));
            editors.Add(column, editor);
            var box = new StackPanel { Margin = new Thickness(6) };
            box.Children.Add(new Label { Content = name == "UseRecipes" ? "Использовать в рецепте" : name, Target = editor });
            box.Children.Add(editor);
            fields.Children.Add(box);
        }
        if (editors.Count == 0) return;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var save = new Button { Content = "Применить", IsDefault = true, Margin = new Thickness(6) };
        save.SetResourceReference(StyleProperty, "BtnSaveObject");
        actions.Children.Add(new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(6) });
        actions.Children.Add(save);
        var panel = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        var parameterTable = _recipeDetails?._tableData;
        DataTable? draft = null;
        DataGrid? grid = null;
        if (parameterTable != null)
        {
            draft = parameterTable.Rows.Copy();
            draft.AcceptChanges();
            if (_recipeDetails?._baseRecipeValues is { } defaults)
            {
                var overridden = draft.Rows.Cast<DataRow>().Select(ParameterKey).ToHashSet();
                foreach (DataRow inherited in defaults.Rows)
                    if (!overridden.Contains(ParameterKey(inherited))) draft.ImportRow(inherited);
            }
            grid = new DataGrid { ItemsSource = draft.DefaultView, AutoGenerateColumns = false,
                CanUserAddRows = false, CanUserDeleteRows = false, IsReadOnly = !parameterTable.CanWrite,
                MinHeight = 100, MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height * 0.38) };
            if (_recipeDetails?._baseRecipeValues != null)
            {
                grid.Columns.Add(new DataGridTextColumn { Header = "Рецепт-источник", IsReadOnly = true,
                    Binding = new Binding("[Recipe_Record]") });
                grid.BeginningEdit += (_, e) =>
                {
                    if (e.Row.Item is DataRowView value && IsDefaultRecipe(value))
                        e.Cancel = !ParticipatesInRecipes(value.Row) || !UserAuthorizationService.Has(Permission.AddData);
                };
            }
            foreach (DataColumn column in draft.Columns)
            {
                string name = column.ColumnName;
                if (parameterTable.Definition.ScopeColumns.Contains(name) || name == "Recipe_Record" ||
                    Configurator.MainWindow.HideLanguageField(name)) continue;
                bool editable = parameterTable.Definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                    !column.ReadOnly && !parameterTable.ComputedColumns.Contains(name) && !parameterTable.IdentityColumns.Contains(name) &&
                    (name != "UseRecipes" || record == 0);
                var binding = new Binding($"[{name}]") { ValidatesOnExceptions = true };
                if (name == "UseRecipes")
                {
                    var choices = new[] { false, true }.Select(enabled => new
                    {
                        Value = Convert.ChangeType(enabled, column.DataType), Caption = enabled ? "Да" : "Нет"
                    }).ToArray();
                    grid.Columns.Add(new DataGridComboBoxColumn { Header = "Использовать в рецепте", IsReadOnly = !editable,
                        ItemsSource = choices, DisplayMemberPath = "Caption", SelectedValuePath = "Value", SelectedValueBinding = binding });
                }
                else grid.Columns.Add(new DataGridTextColumn { Header = name, IsReadOnly = !editable, Binding = binding });
            }
            if (selectedParameter != null && parameterTable.CanWrite)
            {
                var selected = draft.Rows.Find(parameterTable.Rows.PrimaryKey.Select(key => selectedParameter[key]).ToArray());
                grid.SelectedItem = draft.DefaultView.Cast<DataRowView>().FirstOrDefault(item => item.Row == selected);
                if (grid.SelectedItem != null) grid.ScrollIntoView(grid.SelectedItem);
            }
        }
        var content = new StackPanel();
        content.Children.Add(fields);
        if (grid != null) content.Children.Add(new GroupBox { Header = "Параметры рецепта", Content = grid, Margin = new Thickness(12, 0, 12, 8) });
        panel.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window { Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = $"{_tableData!.Definition.Caption} · № {record}", Width = Math.Min(grid == null ? 700 : 1000, SystemParameters.WorkArea.Width),
            MaxHeight = SystemParameters.WorkArea.Height * 0.9, SizeToContent = SizeToContent.Height, Content = panel };
        save.Click += (_, _) =>
        {
            if (grid != null && (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true))) return;
            var addedParameters = new List<DataRow>();
            var editedParameters = new List<(DataRow Row, DataRowState State, Dictionary<string, object> Before)>();
            row.BeginEdit();
            try
            {
                foreach (var (column, editor) in editors)
                {
                    if (!editor.IsEnabled) continue;
                    object value = editor is CheckBox option
                        ? Convert.ChangeType(option.IsChecked == true, column.DataType)
                        : ((TextBox)editor).Text;
                    if (value is string { Length: 0 } && row[column.ColumnName] == DBNull.Value) continue;
                    if (value is string text && column.DataType != typeof(string))
                        value = text.Length == 0 && column.AllowDBNull ? DBNull.Value : Convert.ChangeType(text, column.DataType);
                    if (!Equals(row[column.ColumnName], value)) row[column.ColumnName] = value;
                }
                if (draft != null && parameterTable?.CanWrite == true)
                {
                    foreach (DataRow modified in draft.Select(null, null, DataViewRowState.ModifiedCurrent))
                    {
                        if (record > 0 && Convert.ToInt32(modified["Recipe_Record"]) == 0)
                        {
                            if (!ParticipatesInRecipes(modified) || !parameterTable.Definition.EditableColumns.Any(name =>
                                draft.Columns.Contains(name) && !Equals(modified[name], modified[name, DataRowVersion.Original]))) continue;
                            var own = parameterTable.Rows.NewRow();
                            foreach (DataColumn column in parameterTable.Rows.Columns)
                                if (!column.ReadOnly) own[column] = modified[column.ColumnName];
                            own["Recipe_Record"] = record;
                            own["UseRecipes"] = Convert.ChangeType(false, draft.Columns["UseRecipes"]!.DataType);
                            parameterTable.Rows.Rows.Add(own);
                            addedParameters.Add(own);
                            continue;
                        }
                        var original = parameterTable.Rows.Rows.Find(draft.PrimaryKey.Select(key => modified[key]).ToArray())!;
                        var before = parameterTable.Definition.EditableColumns
                            .Where(name => draft.Columns.Contains(name) && !Equals(original[name], modified[name]))
                            .ToDictionary(name => name, name => original[name]);
                        if (before.Count == 0) continue;
                        editedParameters.Add((original, original.RowState, before));
                        foreach (string name in before.Keys) original[name] = modified[name];
                    }
                }
                row.EndEdit();
                _recipeDetails?.UpdateActionState();
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or DataException)
            {
                row.CancelEdit();
                foreach (var addedParameter in addedParameters) parameterTable!.Rows.Rows.Remove(addedParameter);
                foreach (var (parameter, state, before) in editedParameters)
                    if (state == DataRowState.Unchanged) parameter.RejectChanges();
                    else foreach (var (name, value) in before) parameter[name] = value;
                Configurator.Infrastructure.ModernDialog.Show(dialog, "Проверьте тип и диапазон введённых значений.",
                    "Редактирование", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        dialog.ShowDialog();
        UpdateActionState();
    }

    private static bool ParticipatesInRecipes(DataRow row) =>
        row.Table.Columns.Contains("UseRecipes") && row["UseRecipes"] != DBNull.Value && Convert.ToBoolean(row["UseRecipes"]);

    private static (object Step, object Type, object Record) ParameterKey(DataRow row) =>
        (row["Step_Number"], row["Type"], row["Record"]);

    private static bool IsDefaultRecipe(DataRowView row) =>
        row.Row.Table.Columns.Contains("Recipe_Record") && Convert.ToString(row["Recipe_Record"]) == "0";

    private void ToggleRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!_canSave || RowsGrid.SelectedItem is not DataRowView row || !IsDefaultRecipe(row) ||
            !UserAuthorizationService.Require(Permission.EditData) ||
            row.Row.Table.Columns["UseRecipes"] is not DataColumn { ReadOnly: false } column) return;
        bool value = row[column.ColumnName] != DBNull.Value && Convert.ToBoolean(row[column.ColumnName]);
        row[column.ColumnName] = Convert.ChangeType(!value, column.DataType);
        row.EndEdit();
        UpdateActionState();
    }

    private void RowsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (Configurator.MainWindow.HideLanguageField(e.PropertyName)) { e.Cancel = true; return; }
        if (_tableData == null) { e.Column.IsReadOnly = true; return; }
        string name = e.PropertyName;
        if (e.Column is DataGridTextColumn text)
        {
            var style = new Style(typeof(TextBlock), text.ElementStyle);
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 3, 8, 3)));
            text.ElementStyle = style;
        }
        if (name == "UseRecipes")
        {
            e.Column.Header = "Использовать в рецепте";
            var style = new Style(typeof(DataGridCell), RowsGrid.CellStyle);
            foreach (string enabled in new[] { "1", "True" })
            {
                var trigger = new MultiDataTrigger();
                trigger.Conditions.Add(new Condition(new Binding("[Recipe_Record]"), "0"));
                trigger.Conditions.Add(new Condition(new Binding("[UseRecipes]"), enabled));
                trigger.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.PaleGreen));
                trigger.Setters.Add(new Setter(Control.ForegroundProperty, System.Windows.Media.Brushes.Black));
                style.Triggers.Add(trigger);
            }
            e.Column.CellStyle = style;
        }
        if (_appliedSectionId == "queue-selections" && name == "Queue_Record" && _queueChoices != null)
        {
            e.Column = new DataGridComboBoxColumn
            {
                Header = "Очередь", SortMemberPath = name, ItemsSource = _queueChoices,
                DisplayMemberPath = _queueChoices.Table?.Columns.Contains("Name_L1") == true ? "[Name_L1]" : "[Record]",
                SelectedValuePath = "[Record]", SelectedValueBinding = new Binding($"[{name}]"), IsReadOnly = true
            };
            return;
        }
        bool inKey = _tableData.Definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool inScope = _tableData.Definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool eligible = _tableData.Definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
            || (inKey && !inScope && !name.Equals("Record", StringComparison.OrdinalIgnoreCase));
        e.Column.IsReadOnly = !_tableData.CanWrite || !eligible;
    }

    private void RowsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_busy || _tableData == null || !_tableData.CanWrite || e.Row.Item is not DataRowView row) { e.Cancel = true; return; }
        if (_appliedSectionId is "recipes" or "recipe-values" or "queue-selections" ||
            (_appliedSectionId == "queues" && row.Row.RowState != DataRowState.Added)) { e.Cancel = true; return; }
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
