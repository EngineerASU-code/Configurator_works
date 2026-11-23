#nullable enable
using System;
using System.Data;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Configurator.Core.Projects;

namespace Configurator;

public partial class MatrixCatalogControl : UserControl
{
    private string? _connectionString;
    private object? _plcValue;
    private object? _matrixClassValue;
    private ProgramSupportRepository? _repository;
    public event Action<ChangeJournalEntry>? ChangeJournalRequested;
    private OfflineProjectDataSource? _offlineSource;
    private ProgramSupportTableData? _tableData;
    private IReadOnlyList<MatrixDeviceClassOption> _deviceClasses = Array.Empty<MatrixDeviceClassOption>();
    private DataTable? _commandMapView;
    private readonly Dictionary<string, int> _commandMapColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int Position, int Command)> _commandMapExistingCells = new();
    private IReadOnlyList<MatrixDevicePositionOption> _devicePositions = Array.Empty<MatrixDevicePositionOption>();
    private string? _deviceLookupStatus;
    private bool _busy;
    private bool _suppressSelectionEvents;
    private bool _canAdd;
    private bool _canDelete;
    private bool _canSave;
    private string? _appliedSectionId;
    private object? _appliedMatrixRecord;
    private object? _appliedDeviceGroup;
    private int _contextVersion;
    private int _loadVersion;

    public MatrixCatalogControl()
    {
        InitializeComponent();
        RowsGrid.PreparingCellForEdit += (_, _) => UpdateActionState();
        Loaded += MatrixCatalogControl_Loaded;
        Unloaded += MatrixCatalogControl_Unloaded;
        _suppressSelectionEvents = true;
        SectionBox.ItemsSource = ProgramSupportCatalog.MatrixTables;
        SectionBox.SelectedValue = "matrix-list";
        _appliedSectionId = SectionBox.SelectedValue as string;
        _suppressSelectionEvents = false;
        SetActions(false, false, false);
    }

    public async void Configure(string connectionString, object plcValue, object matrixClassNumberValue,
        IEnumerable<MatrixDeviceClassOption>? deviceClasses = null)
        => await ConfigureCoreAsync(connectionString, null, plcValue, matrixClassNumberValue, deviceClasses);

    public async void Configure(OfflineProjectDataSource source, object plcValue, object matrixClassNumberValue,
        IEnumerable<MatrixDeviceClassOption>? deviceClasses = null)
        => await ConfigureCoreAsync(null, source, plcValue, matrixClassNumberValue, deviceClasses);

    private bool _embedded;

    public Task OpenMatrixAsync(string? connection, OfflineProjectDataSource? offline, object plc, object cls,
        object record, string name, IEnumerable<MatrixDeviceClassOption> classes)
    {
        _embedded = true;
        return ConfigureCoreAsync(connection, offline, plc, cls, classes, record, name);
    }

    private async Task ConfigureCoreAsync(string? connectionString, OfflineProjectDataSource? offline, object plcValue,
        object matrixClassNumberValue, IEnumerable<MatrixDeviceClassOption>? deviceClasses, object? selectedRecord = null, string? matrixName = null)
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
        _offlineSource = offline;
        _plcValue = plcValue;
        _matrixClassValue = matrixClassNumberValue;
        if (deviceClasses != null) _deviceClasses = deviceClasses.ToArray();
        _repository = offline != null ? new ProgramSupportRepository(offline)
            : string.IsNullOrWhiteSpace(connectionString) ? null : new ProgramSupportRepository(connectionString);
        if (_repository != null) _repository.ChangeJournalRequested += entry => ChangeJournalRequested?.Invoke(entry);
        _tableData = null;
        _commandMapView = null;
        _commandMapColumns.Clear();
        _commandMapExistingCells.Clear();
        RowsGrid.ItemsSource = null;
        EmptyStateText.Text = string.Empty;
        EmptyStateText.Visibility = Visibility.Collapsed;
        MatrixRecordBox.ItemsSource = null;
        DeviceGroupBox.ItemsSource = null;
        PositionBox.ItemsSource = null;
        DevicePLCBox.ItemsSource = null;
        DeviceObjectBox.ItemsSource = null;
        _devicePositions = Array.Empty<MatrixDevicePositionOption>();
        _appliedMatrixRecord = null;
        _appliedDeviceGroup = null;
        _appliedSectionId = SectionBox.SelectedValue as string;
        SetBusy(false);
        SetActions(false, false, false);
        PlcInput.Text = Convert.ToString(plcValue, CultureInfo.InvariantCulture) ?? string.Empty;
        ContextText.Text = matrixName ?? $"PLC={plcValue ?? "?"}  ·  Matrix class={matrixClassNumberValue ?? "?"}";
        if (selectedRecord != null)
        {
            _suppressSelectionEvents = true;
            MatrixRecordBox.ItemsSource = new[] { new MatrixRecordOption { Record = Convert.ToInt32(selectedRecord), Caption = selectedRecord.ToString()! } };
            MatrixRecordBox.SelectedIndex = 0;
            _appliedMatrixRecord = MatrixRecordBox.SelectedValue;
            SectionBox.SelectedValue = "matrix-command-list";
            _appliedSectionId = "matrix-command-list";
            _suppressSelectionEvents = false;
            await LoadSelectedAsync(_appliedSectionId, _appliedMatrixRecord);
            return;
        }
        if (await LoadMatrixRecordsAsync(contextVersion) && contextVersion == _contextVersion)
        {
            string? section = SectionBox.SelectedValue as string;
            if (await LoadSelectedAsync(section, _appliedMatrixRecord)) _appliedSectionId = section;
        }
    }

    public bool HasPendingChanges()
    {
        return _tableData?.Rows.Rows.Cast<DataRow>().Any(row =>
                   row.RowState is DataRowState.Added or DataRowState.Modified or DataRowState.Deleted) == true
            || _commandMapView?.Rows.Cast<DataRow>().Any(row => row.RowState == DataRowState.Modified
                && _commandMapColumns.Keys.Any(column => !Equals(row[column, DataRowVersion.Original], row[column, DataRowVersion.Current]))) == true;
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
        var choice = Configurator.Infrastructure.ModernDialog.Show(Window.GetWindow(this),
            "В текущем разделе Matrix есть несохранённые изменения. Сохранить их перед переключением?",
            "Несохранённые изменения", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice is MessageBoxResult.Cancel or MessageBoxResult.None) return false;
        if (choice == MessageBoxResult.Yes) return await SavePendingChangesAsync();

        _tableData!.Rows.RejectChanges();
        _commandMapView?.RejectChanges();
        return await LoadSelectedAsync(_appliedSectionId, _appliedMatrixRecord);
    }

    private async System.Threading.Tasks.Task<bool> LoadMatrixRecordsAsync(int contextVersion)
    {
        if (_embedded) return contextVersion == _contextVersion;
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
                Caption = $"{row.Key.Record} · {row.Name_L1} / {row.Name_L2} · DB {row.DB_Number?.ToString(CultureInfo.InvariantCulture) ?? "—"}"
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
            StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex);
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

    private bool _syncingSectionTabs;

    private void MatrixTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSectionTabs || _busy || SectionBox == null ||
            MatrixTabs?.SelectedItem is not TabItem selected) return;
        if (selected.Content is TabControl children && children.SelectedItem is TabItem child)
            selected = child;
        if (selected.Tag is string section) SectionBox.SelectedValue = section;
    }

    private void SyncSectionTabs()
    {
        if (SectionBox.SelectedValue is not string section) return;
        _syncingSectionTabs = true;
        try
        {
            foreach (var parent in MatrixTabs.Items.OfType<TabItem>())
            {
                if (parent.Content is TabControl children)
                {
                    var child = children.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Tag, section));
                    if (child == null) continue;
                    MatrixTabs.SelectedItem = parent;
                    children.SelectedItem = child;
                    break;
                }
                if (!Equals(parent.Tag, section)) continue;
                MatrixTabs.SelectedItem = parent;
                break;
            }
        }
        finally { _syncingSectionTabs = false; }
    }

    private async void SectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSectionTabs();
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
        bool matrixCatalogChanged = _appliedSectionId == "matrix-list" && HasPendingChanges();
        SetBusy(true);
        if (!await PrepareForContextChangeCoreAsync())
        {
            RestoreSectionSelection();
            SetBusy(false);
            return;
        }
        if (contextVersion != _contextVersion) return;
        if (matrixCatalogChanged && !await LoadMatrixRecordsAsync(contextVersion))
        {
            RestoreSectionSelection();
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

    private async void DeviceGroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents || (SectionBox.SelectedValue as string) != "matrix-devices") return;
        object? requested = DeviceGroupBox.SelectedValue;
        if (Equals(requested, _appliedDeviceGroup)) return;
        if (_busy) { RestoreDeviceGroupSelection(); return; }
        if (!CommitGridEdits())
        {
            StatusText.Text = "Проверьте введённые значения перед переключением группы.";
            RestoreDeviceGroupSelection();
            return;
        }
        int contextVersion = _contextVersion;
        SetBusy(true);
        if (!await PrepareForContextChangeCoreAsync())
        {
            RestoreDeviceGroupSelection();
            SetBusy(false);
            return;
        }
        if (contextVersion != _contextVersion) return;
        _appliedDeviceGroup = requested;
        ApplyDeviceGroupFilter();
        try { await LoadDeviceObjectOptionsAsync(); }
        catch (Exception ex) { _deviceLookupStatus = ex.Message; }
        UpdateEmptyState();
        SetBusy(false);
    }

    private async void PositionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents || _busy || (SectionBox.SelectedValue as string) != "matrix-devices") return;
        SetBusy(true);
        try { await LoadDeviceObjectOptionsAsync(); }
        catch (Exception ex) { _deviceLookupStatus = Configurator.Infrastructure.UserErrorMessage.Format(ex); StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex); }
        finally { SetBusy(false); }
    }

    private async void DevicePLCBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents || _busy || (SectionBox.SelectedValue as string) != "matrix-devices") return;
        if (DevicePLCBox.SelectedValue is not int plc) return;
        SetBusy(true);
        try { await LoadDeviceObjectOptionsAsync(plc); }
        catch (Exception ex) { _deviceLookupStatus = Configurator.Infrastructure.UserErrorMessage.Format(ex); StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex); }
        finally { SetBusy(false); }
    }

    private void DeviceObjectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateActionState();

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

    private async void ExportAwl_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _repository == null || !UserAuthorizationService.Require(Permission.ExportData)) return;
        if (!CommitGridEdits())
        {
            StatusText.Text = "Завершите редактирование текущей строки перед экспортом.";
            return;
        }
        int? selectedRecord = GetSelectedMatrixRecord();
        if (selectedRecord == null)
        {
            StatusText.Text = "Выберите сохранённую матрицу для экспорта.";
            return;
        }
        int contextVersion = _contextVersion;
        SetBusy(true);
        string? temporaryFile = null;
        try
        {
            if (!await PrepareForContextChangeCoreAsync()) return;
            if (contextVersion != _contextVersion) return;
            if (selectedRecord == null) throw new InvalidOperationException("Матрица не выбрана.");
            string content = await BuildAwlAsync(selectedRecord.Value);
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт матрицы Siemens AWL",
                Filter = "Siemens AWL (*.AWL)|*.AWL|Все файлы (*.*)|*.*",
                DefaultExt = ".AWL",
                AddExtension = true,
                FileName = $"Matrix {selectedRecord.Value}.AWL"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                StatusText.Text = "Экспорт отменён.";
                return;
            }
            string directory = Path.GetDirectoryName(dialog.FileName) ?? throw new InvalidOperationException("Не удалось определить папку сохранения.");
            string name = Path.GetFileName(dialog.FileName);
            temporaryFile = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporaryFile, content, new UTF8Encoding(false));
            File.Move(temporaryFile, dialog.FileName, true);
            temporaryFile = null;
            StatusText.Text = $"AWL-файл сохранён: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Экспорт не выполнен. Файл не создан: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
        }
        finally
        {
            if (temporaryFile != null)
            {
                try { File.Delete(temporaryFile); } catch { }
            }
            if (contextVersion == _contextVersion) SetBusy(false);
        }
    }

    private async Task<string> BuildAwlAsync(int matrixRecord)
    {
        if (_repository == null || _plcValue == null || _matrixClassValue == null)
            throw new InvalidOperationException("Не выбраны проект, PLC или класс Matrix.");
        int plc = Convert.ToInt32(_plcValue, CultureInfo.InvariantCulture);
        int matrixClass = Convert.ToInt32(_matrixClassValue, CultureInfo.InvariantCulture);
        var matrices = await _repository.LoadMatrixAsync("matrix-list", plc, matrixClass);
        var matrix = matrices.Rows.Rows.Cast<DataRow>().FirstOrDefault(row =>
            row.RowState != DataRowState.Deleted && Convert.ToInt32(row["Record"], CultureInfo.InvariantCulture) == matrixRecord);
        if (matrix == null) throw new InvalidOperationException("Матрица больше не существует в выбранном PLC и классе.");

        var commandListTask = _repository.LoadMatrixAsync("matrix-command-list", plc, matrixClass, matrixRecord);
        var commandsTask = _repository.LoadMatrixAsync("matrix-commands", plc, matrixClass, matrixRecord);
        var groupListTask = _repository.LoadMatrixAsync("matrix-device-list", plc, matrixClass, matrixRecord);
        var positionTypesTask = _repository.LoadMatrixAsync("matrix-device-list-type", plc, matrixClass, matrixRecord);
        var assignmentsTask = _repository.LoadMatrixAsync("matrix-devices", plc, matrixClass, matrixRecord);
        await Task.WhenAll(commandListTask, commandsTask, groupListTask, positionTypesTask, assignmentsTask);
        var commandList = commandListTask.Result.Rows.Rows.Cast<DataRow>().ToArray();
        var commandCells = commandsTask.Result.Rows.Rows.Cast<DataRow>().ToArray();
        var groupRows = groupListTask.Result.Rows.Rows.Cast<DataRow>().ToArray();
        var positionRows = positionTypesTask.Result.Rows;
        var assignmentRows = assignmentsTask.Result.Rows;
        await _repository.ValidateMatrixDeviceReferencesAsync(groupListTask.Result.Rows, positionRows, assignmentRows, _deviceClasses);

        var commands = commandList.Select(row => new MatrixAwlCommand(
            Convert.ToInt32(row["CmdNumber"], CultureInfo.InvariantCulture),
            row["DelayON"], row["DelayOFF"], row["Condition"])).ToArray();
        var cells = commandCells.Select(row => (
            Position: Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture),
            CommandNumber: Convert.ToInt32(row["CmdNumber"], CultureInfo.InvariantCulture),
            Code: row["Cmd"] == DBNull.Value ? null : Convert.ToString(row["Cmd"], CultureInfo.InvariantCulture))).ToArray();
        var groups = new List<MatrixAwlDeviceGroup>();
        foreach (DataRow group in groupRows.OrderBy(row => Convert.ToInt32(row["DeviceList"], CultureInfo.InvariantCulture)))
        {
            int groupNumber = Convert.ToInt32(group["DeviceList"], CultureInfo.InvariantCulture);
            var devices = new Dictionary<int, long>();
            foreach (DataRow assignment in assignmentRows.Rows.Cast<DataRow>().Where(row =>
                         row.RowState != DataRowState.Deleted
                         && Convert.ToInt32(row["DeviceList"], CultureInfo.InvariantCulture) == groupNumber))
            {
                int position = Convert.ToInt32(assignment["Position"], CultureInfo.InvariantCulture);
                long devicePlc = Convert.ToInt64(assignment["Device_PLC"], CultureInfo.InvariantCulture);
                long deviceClass = Convert.ToInt64(assignment["Device_PLC_Class_Number"], CultureInfo.InvariantCulture);
                long deviceRecord = Convert.ToInt64(assignment["Device_Record"], CultureInfo.InvariantCulture);
                if (devicePlc < 0 || deviceClass < 0 || deviceRecord < 0)
                    throw new InvalidOperationException($"Ссылка устройства в группе {groupNumber}, позиции {position} содержит отрицательный ключ.");
                long value = checked(devicePlc * 100_000L + deviceClass * 1_000L + deviceRecord);
                if (!devices.TryAdd(position, value))
                    throw new InvalidOperationException($"В группе {groupNumber} несколько объектов назначены позиции {position}.");
            }
            groups.Add(new MatrixAwlDeviceGroup(groupNumber, devices));
        }

        int record = Convert.ToInt32(matrix["Record"], CultureInfo.InvariantCulture);
        if (matrix["DB_Number"] == DBNull.Value)
            throw new InvalidOperationException("У выбранной матрицы не задан DB_Number.");
        int dbNumber = Convert.ToInt32(matrix["DB_Number"], CultureInfo.InvariantCulture);
        int[] positions = positionRows.Rows.Cast<DataRow>()
            .Where(row => row.RowState != DataRowState.Deleted)
            .Select(row => Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture))
            .Concat(cells.Select(cell => cell.Position))
            .Distinct()
            .ToArray();
        return MatrixAwlExporter.Generate(new MatrixAwlSnapshot(record, dbNumber, positions, commands, cells, groups));
    }

    private int? GetSelectedMatrixRecord()
    {
        if (IsMatrixChildSelected())
            return MatrixRecordBox.SelectedValue == null || MatrixRecordBox.SelectedValue == DBNull.Value
                ? null
                : Convert.ToInt32(MatrixRecordBox.SelectedValue, CultureInfo.InvariantCulture);
        if ((SectionBox.SelectedValue as string) == "matrix-list" && RowsGrid.SelectedItem is DataRowView row
            && (row.Row.RowState is DataRowState.Unchanged or DataRowState.Modified) && row.Row["Record"] != DBNull.Value)
            return Convert.ToInt32(row.Row["Record"], CultureInfo.InvariantCulture);
        return null;
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
        if (_busy || _repository == null || (_connectionString == null && _offlineSource == null))
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
        if (_offlineSource != null) Configure(_offlineSource, plcValue, _matrixClassValue);
        else Configure(_connectionString!, plcValue, _matrixClassValue);
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
        if (childSelected && (matrixRecord == null || matrixRecord == DBNull.Value))
        {
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            _tableData = null;
            _commandMapView = null;
            _commandMapColumns.Clear();
            _commandMapExistingCells.Clear();
            RowsGrid.ItemsSource = null;
            DeviceGroupBox.ItemsSource = null;
            _appliedDeviceGroup = null;
            EmptyStateText.Text = "Сначала создайте или выберите матрицу в разделе «Matrix_List».";
            EmptyStateText.Visibility = Visibility.Visible;
            StatusText.Text = "Для дочерних разделов требуется выбранная матрица.";
            SetActions(false, false, false);
            UpdateMatrixRecordVisibility();
            SetBusy(false);
            return true;
        }
        _tableData = null;
        _commandMapView = null;
        _commandMapColumns.Clear();
        _commandMapExistingCells.Clear();
        RowsGrid.ItemsSource = null;
        EmptyStateText.Visibility = Visibility.Collapsed;
        DeviceGroupBox.ItemsSource = null;
        _appliedDeviceGroup = null;
        _deviceLookupStatus = null;
        PositionBox.ItemsSource = null;
        DevicePLCBox.ItemsSource = null;
        DeviceObjectBox.ItemsSource = null;
        _devicePositions = Array.Empty<MatrixDevicePositionOption>();
        SetBusy(true);
        SetActions(false, false, false);
        try
        {
            var tableData = await repository.LoadMatrixAsync(id, plcValue, matrixClassValue, matrixRecord);
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            _tableData = tableData;
            _commandMapView = null;
            _commandMapColumns.Clear();
            _commandMapExistingCells.Clear();
            RowsGrid.ItemsSource = _tableData.Rows.DefaultView;
            if (id == "matrix-commands")
            {
                await LoadCommandMapAsync(repository, Convert.ToInt32(matrixRecord, CultureInfo.InvariantCulture),
                    contextVersion, loadVersion);
                if (!IsCurrent(contextVersion, loadVersion)) return false;
            }
            await LoadDeviceGroupsAsync(id, matrixRecord, repository, contextVersion, loadVersion);
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            if (id == "matrix-devices")
            {
                await LoadDevicePositionsAsync(repository, matrixRecord, contextVersion, loadVersion);
                if (!IsCurrent(contextVersion, loadVersion)) return false;
            }
            ApplyDeviceGroupFilter();
            bool writable = _tableData.CanWrite;
            bool hasClassOptions = id != "matrix-device-list-type" || _deviceClasses.Count > 0;
            bool isCommandMap = id == "matrix-commands";
            SetActions(writable && hasClassOptions && !isCommandMap && _tableData.Definition.AllowInsert,
                writable && !isCommandMap && _tableData.Definition.AllowDelete, writable);
            StatusText.Text = string.Join(" ", new[]
            {
                _tableData.WriteBlockReason ?? $"{_tableData.Definition.Caption}: {_tableData.Rows.Rows.Count} строк.",
                _deviceLookupStatus,
                id == "matrix-device-list-type" && _deviceClasses.Count == 0
                    ? "В архитектуре проекта нет классов с основной таблицей; назначение типов устройств недоступно."
                    : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            UpdateEmptyState();
            return true;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(contextVersion, loadVersion)) return false;
            StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex);
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

    private async Task LoadDeviceGroupsAsync(string? sectionId, object? matrixRecord,
        ProgramSupportRepository repository, int contextVersion, int loadVersion)
    {
        if (sectionId != "matrix-devices")
        {
            _suppressSelectionEvents = true;
            DeviceGroupBox.ItemsSource = null;
            DeviceGroupBox.SelectedValue = null;
            PositionBox.ItemsSource = null;
            DevicePLCBox.ItemsSource = null;
            DeviceObjectBox.ItemsSource = null;
            _devicePositions = Array.Empty<MatrixDevicePositionOption>();
            _appliedDeviceGroup = null;
            _suppressSelectionEvents = false;
            return;
        }

        var data = await repository.LoadMatrixAsync("matrix-device-list", _plcValue!, _matrixClassValue!, matrixRecord);
        if (!IsCurrent(contextVersion, loadVersion)) return;
        var options = data.Rows.Rows.Cast<DataRow>().Select(row => new DeviceGroupOption
        {
            Number = Convert.ToInt32(row["DeviceList"], CultureInfo.InvariantCulture),
            Caption = $"{Convert.ToInt32(row["DeviceList"], CultureInfo.InvariantCulture)} · {Convert.ToString(row["Name_L1"], CultureInfo.CurrentCulture)}"
        }).ToList();
        _suppressSelectionEvents = true;
        DeviceGroupBox.ItemsSource = options;
        if (options.Count > 0 && !options.Any(option => Equals(option.Number, _appliedDeviceGroup)))
            DeviceGroupBox.SelectedValue = options[0].Number;
        else if (options.Count == 0)
            DeviceGroupBox.SelectedValue = null;
        _appliedDeviceGroup = DeviceGroupBox.SelectedValue;
        _suppressSelectionEvents = false;
    }

    private async Task LoadDevicePositionsAsync(ProgramSupportRepository repository, object? matrixRecord,
        int contextVersion, int loadVersion)
    {
        var data = await repository.LoadMatrixAsync("matrix-device-list-type", _plcValue!, _matrixClassValue!, matrixRecord);
        if (!IsCurrent(contextVersion, loadVersion)) return;
        _devicePositions = data.Rows.Rows.Cast<DataRow>().Select(row =>
        {
            int position = Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture);
            int classNumber = row["Device_Global_Class"] == DBNull.Value ? 0 : Convert.ToInt32(row["Device_Global_Class"], CultureInfo.InvariantCulture);
            string name = Convert.ToString(row["Name_L1"], CultureInfo.CurrentCulture) ?? string.Empty;
            string className = _deviceClasses.FirstOrDefault(option => option.Number == classNumber)?.Name ?? $"класс {classNumber}";
            return new MatrixDevicePositionOption(position, classNumber, $"{position} · {name} · {className}");
        }).OrderBy(option => option.Position).ToArray();

        int? selectedPosition = null;
        int? selectedGroup = _appliedDeviceGroup == null ? null : Convert.ToInt32(_appliedDeviceGroup, CultureInfo.InvariantCulture);
        var currentAssignment = FindAssignment(selectedGroup, null);
        if (currentAssignment != null && currentAssignment["Position"] != DBNull.Value)
            selectedPosition = Convert.ToInt32(currentAssignment["Position"], CultureInfo.InvariantCulture);
        _suppressSelectionEvents = true;
        PositionBox.ItemsSource = _devicePositions;
        if (_devicePositions.Count > 0)
            PositionBox.SelectedValue = selectedPosition.HasValue && _devicePositions.Any(item => item.Position == selectedPosition.Value)
                ? selectedPosition.Value : _devicePositions[0].Position;
        else PositionBox.SelectedValue = null;
        _suppressSelectionEvents = false;
        await LoadDeviceObjectOptionsAsync();
    }

    private async Task LoadDeviceObjectOptionsAsync(int? requestedPlc = null)
    {
        _deviceLookupStatus = null;
        var selectedPosition = PositionBox.SelectedItem as MatrixDevicePositionOption;
        if (_repository == null || _tableData?.Definition.Id != "matrix-devices" || selectedPosition == null)
        {
            SetDeviceObjectOptions(Array.Empty<MatrixDevicePlcOption>(), Array.Empty<MatrixDeviceObjectOption>(), null, null);
            return;
        }
        var deviceClass = _deviceClasses.FirstOrDefault(option => option.Number == selectedPosition.ClassNumber
            && !string.IsNullOrWhiteSpace(option.TableName));
        if (deviceClass == null)
        {
            SetDeviceObjectOptions(Array.Empty<MatrixDevicePlcOption>(), Array.Empty<MatrixDeviceObjectOption>(), null, null);
            _deviceLookupStatus = $"Для позиции {selectedPosition.Position} нет проверенной основной таблицы класса {selectedPosition.ClassNumber}; назначение недоступно.";
            StatusText.Text = _deviceLookupStatus;
            return;
        }

        var plcs = await _repository.LoadMatrixDevicePlcsAsync(deviceClass);
        var assignment = FindAssignment(_appliedDeviceGroup == null ? null : Convert.ToInt32(_appliedDeviceGroup, CultureInfo.InvariantCulture), selectedPosition.Position);
        int? oldPlc = assignment == null || assignment["Device_PLC"] == DBNull.Value
            ? null : Convert.ToInt32(assignment["Device_PLC"], CultureInfo.InvariantCulture);
        int? preferredPlc = requestedPlc ?? oldPlc ?? (int.TryParse(Convert.ToString(_plcValue, CultureInfo.InvariantCulture), out int matrixPlc) ? matrixPlc : null);
        int? selectedPlc = preferredPlc.HasValue && plcs.Any(item => item.Number == preferredPlc.Value)
            ? preferredPlc.Value : plcs.FirstOrDefault()?.Number;
        if (selectedPlc == null)
        {
            SetDeviceObjectOptions(plcs, Array.Empty<MatrixDeviceObjectOption>(), null, null);
            _deviceLookupStatus = $"В таблице класса «{deviceClass.Name}» нет объектов с PLC; назначение недоступно.";
            StatusText.Text = _deviceLookupStatus;
            return;
        }
        var objects = await _repository.LoadMatrixDeviceObjectsAsync(deviceClass, selectedPlc.Value);
        int? oldRecord = assignment == null || oldPlc != selectedPlc || assignment["Device_Record"] == DBNull.Value
            ? null : Convert.ToInt32(assignment["Device_Record"], CultureInfo.InvariantCulture);
        SetDeviceObjectOptions(plcs, objects, selectedPlc, oldRecord);
        if (objects.Count == 0)
        {
            _deviceLookupStatus = $"В классе «{deviceClass.Name}», PLC={selectedPlc} нет объектов для назначения.";
            StatusText.Text = _deviceLookupStatus;
        }
        if (assignment != null)
        {
            var viewRow = _tableData.Rows.DefaultView.Cast<DataRowView>().FirstOrDefault(item => ReferenceEquals(item.Row, assignment));
            if (viewRow != null) RowsGrid.SelectedItem = viewRow;
        }
        else RowsGrid.SelectedItem = null;
    }

    private void SetDeviceObjectOptions(IReadOnlyList<MatrixDevicePlcOption> plcs,
        IReadOnlyList<MatrixDeviceObjectOption> objects, int? selectedPlc, int? selectedRecord)
    {
        _suppressSelectionEvents = true;
        DevicePLCBox.ItemsSource = plcs;
        DevicePLCBox.SelectedValue = selectedPlc;
        DeviceObjectBox.ItemsSource = objects;
        DeviceObjectBox.SelectedItem = selectedRecord.HasValue
            ? objects.FirstOrDefault(option => option.Plc == selectedPlc && option.Record == selectedRecord.Value)
            : null;
        _suppressSelectionEvents = false;
        UpdateActionState();
    }

    private DataRow? FindAssignment(int? group, int? position)
    {
        if (_tableData?.Definition.Id != "matrix-devices") return null;
        return _tableData.Rows.Rows.Cast<DataRow>().FirstOrDefault(row => row.RowState != DataRowState.Deleted
            && (group == null || Convert.ToInt32(row["DeviceList"], CultureInfo.InvariantCulture) == group.Value)
            && (position == null || Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture) == position.Value));
    }

    private async Task LoadCommandMapAsync(ProgramSupportRepository repository, int matrixRecord,
        int contextVersion, int loadVersion)
    {
        var commandsTask = repository.LoadMatrixAsync("matrix-command-list", _plcValue!, _matrixClassValue!, matrixRecord);
        var positionsTask = repository.LoadMatrixAsync("matrix-device-list-type", _plcValue!, _matrixClassValue!, matrixRecord);
        await Task.WhenAll(commandsTask, positionsTask);
        if (!IsCurrent(contextVersion, loadVersion)) return;

        var commandNumbers = commandsTask.Result.Rows.Rows.Cast<DataRow>()
            .Select(row => Convert.ToInt32(row["CmdNumber"], CultureInfo.InvariantCulture))
            .Concat(_tableData!.Rows.Rows.Cast<DataRow>().Where(row => row.RowState != DataRowState.Deleted)
                .Select(row => Convert.ToInt32(row["CmdNumber"], CultureInfo.InvariantCulture)))
            .Distinct().OrderBy(value => value).ToArray();
        var positions = positionsTask.Result.Rows.Rows.Cast<DataRow>()
            .Select(row => Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture))
            .Concat(_tableData.Rows.Rows.Cast<DataRow>().Where(row => row.RowState != DataRowState.Deleted)
                .Select(row => Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture)))
            .Distinct().OrderBy(value => value).ToArray();

        var view = new DataTable("Matrix_CommandMap");
        view.Columns.Add("Position", typeof(int));
        _commandMapColumns.Clear();
        _commandMapExistingCells.Clear();
        foreach (int commandNumber in commandNumbers)
        {
            string column = $"Cmd_{commandNumber}";
            view.Columns.Add(column, typeof(string));
            _commandMapColumns[column] = commandNumber;
        }
        foreach (int position in positions)
        {
            var viewRow = view.NewRow();
            viewRow["Position"] = position;
            foreach (var pair in _commandMapColumns)
            {
                int commandNumber = pair.Value;
                var source = _tableData.Rows.Rows.Cast<DataRow>().FirstOrDefault(row =>
                    row.RowState != DataRowState.Deleted
                    && Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture) == position
                    && Convert.ToInt32(row["CmdNumber"], CultureInfo.InvariantCulture) == commandNumber);
                viewRow[pair.Key] = source == null || source["Cmd"] == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(source["Cmd"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (source != null) _commandMapExistingCells.Add((position, commandNumber));
            }
            view.Rows.Add(viewRow);
        }
        view.AcceptChanges();
        _commandMapView = view;
        RowsGrid.ItemsSource = view.DefaultView;
    }

    private void ApplyDeviceGroupFilter()
    {
        if (_tableData?.Definition.Id != "matrix-devices") return;
        var view = _tableData.Rows.DefaultView;
        view.RowFilter = _appliedDeviceGroup == null
            ? "1 = 0"
            : $"DeviceList = {Convert.ToInt32(_appliedDeviceGroup, CultureInfo.InvariantCulture)}";
    }

    private void UpdateEmptyState()
    {
        if (_tableData == null) return;
        bool empty = _tableData.Definition.Id is "matrix-devices" or "matrix-commands"
            ? RowsGrid.Items.Count == 0
            : _tableData.Rows.Rows.Count == 0;
        if (!empty)
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStateText.Text = _tableData.Definition.Id switch
        {
            "matrix-list" => "В выбранном PLC и классе Matrix пока нет матриц. Нажмите «Добавить», чтобы создать первую.",
            "matrix-commands" => "В этой матрице пока нет позиций или команд. Сначала добавьте их в соответствующих разделах.",
            "matrix-devices" when DeviceGroupBox.Items.Count == 0 => "Сначала создайте группу в разделе «Matrix_DeviceList».",
            "matrix-devices" => "Выберите позицию и проверенный объект выше, затем нажмите «Назначить объект».",
            _ => "В выбранной матрице пока нет записей в этом разделе."
        };
        EmptyStateText.Visibility = Visibility.Visible;
    }

    private bool IsMatrixChildSelected() => IsMatrixChild(SectionBox.SelectedValue as string);

    private bool IsMatrixChild(string? id) => id != null &&
        ProgramSupportCatalog.MatrixTables.FirstOrDefault(table => table.Id == id)?.ScopeColumns.Contains("Record", StringComparer.OrdinalIgnoreCase) == true;

    private void UpdateMatrixRecordVisibility()
    {
        var visibility = IsMatrixChildSelected() ? Visibility.Visible : Visibility.Collapsed;
        MatrixRecordLabel.Visibility = MatrixRecordBox.Visibility = _embedded ? Visibility.Collapsed : visibility;
        bool showGroup = (SectionBox.SelectedValue as string) == "matrix-devices";
        DeviceGroupLabel.Visibility = DeviceGroupBox.Visibility = showGroup ? Visibility.Visible : Visibility.Collapsed;
        PositionLabel.Visibility = DevicePLCLabel.Visibility = DeviceObjectLabel.Visibility = showGroup ? Visibility.Visible : Visibility.Collapsed;
        PositionBox.Visibility = DevicePLCBox.Visibility = DeviceObjectBox.Visibility = showGroup ? Visibility.Visible : Visibility.Collapsed;
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
        DeviceGroupBox.IsEnabled = !busy;
        PositionBox.IsEnabled = !busy;
        DevicePLCBox.IsEnabled = !busy;
        DeviceObjectBox.IsEnabled = !busy;
        PlcInput.IsEnabled = !busy;
        LoadPlcButton.IsEnabled = !busy && _repository != null;
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        bool canEdit = UserAuthorizationService.Has(Permission.EditData);
        bool canAdd = UserAuthorizationService.Has(Permission.AddData);
        RowsGrid.IsReadOnly = _tableData?.CanWrite != true || (!canEdit && !canAdd);
        bool assignmentEditor = _tableData?.Definition.Id == "matrix-devices";
        bool commandMapEditor = _tableData?.Definition.Id == "matrix-commands" && _commandMapView != null;
        AddButton.Content = assignmentEditor ? "Назначить объект" : "＋ Добавить";
        DeleteButton.Content = commandMapEditor ? "Удалить команду" : assignmentEditor ? "Удалить назначение" : "Удалить строку";
        bool canAssign = false;
        if (assignmentEditor && CanAssignDeviceSelection())
        {
            int group = Convert.ToInt32(_appliedDeviceGroup, CultureInfo.InvariantCulture);
            int position = Convert.ToInt32(PositionBox.SelectedValue, CultureInfo.InvariantCulture);
            canAssign = UserAuthorizationService.Has(FindAssignment(group, position) == null ? Permission.AddData : Permission.EditData);
        }
        AddButton.IsEnabled = !_busy && _canAdd && (assignmentEditor ? canAssign : canAdd);
        DeleteButton.IsEnabled = !_busy && CanDeleteSelected();
        bool editingRow = RowsGrid.CurrentItem is DataRowView { IsEdit: true } && (canEdit || canAdd);
        SaveButton.IsEnabled = !_busy && _canSave && (editingRow || CanSavePendingChanges(false));
        RefreshButton.IsEnabled = !_busy && _repository != null;
        ExportButton.IsEnabled = !_busy && _repository != null && HasSelectedMatrix() && UserAuthorizationService.Has(Permission.ExportData);
    }

    private bool CanAssignDeviceSelection()
    {
        if (_appliedDeviceGroup == null || PositionBox.SelectedItem is not MatrixDevicePositionOption position
            || DeviceObjectBox.SelectedItem is not MatrixDeviceObjectOption device) return false;
        return position.ClassNumber == device.ClassNumber
            && DevicePLCBox.SelectedValue is int plc && plc == device.Plc;
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
        if (_tableData?.Definition.Id == "matrix-commands" && _commandMapView != null)
        {
            if (!_tableData.Definition.AllowDelete) return false;
            if (!TryGetSelectedCommandMapCell(out var mapRow, out string column, out _, out _, out var source)) return false;
            if (source != null)
                return UserAuthorizationService.Has(source.RowState == DataRowState.Added ? Permission.AddData : Permission.DeleteData);
            return !string.IsNullOrWhiteSpace(Convert.ToString(mapRow[column], CultureInfo.InvariantCulture))
                && UserAuthorizationService.Has(Permission.AddData);
        }
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
            if (_tableData.Definition.Id == "matrix-device-list-type" && row.RowState != DataRowState.Deleted)
            {
                object value = row["Device_Global_Class"];
                if (!int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int classNumber)
                    || !_deviceClasses.Any(option => option.Number == classNumber && !string.IsNullOrWhiteSpace(option.TableName)))
                {
                    if (showAuthorizationMessage)
                        StatusText.Text = "Выберите класс устройств из проверенного списка текущего проекта.";
                    return false;
                }
            }
            if (!UserAuthorizationService.Has(required.Value))
            {
                if (showAuthorizationMessage) UserAuthorizationService.Require(required.Value);
                return false;
            }
        }
        if (_commandMapView != null && _tableData.Definition.Id == "matrix-commands")
        {
            foreach (var mapRow in _commandMapView.Rows.Cast<DataRow>().Where(item => item.RowState == DataRowState.Modified))
            {
                int position = Convert.ToInt32(mapRow["Position", DataRowVersion.Current], CultureInfo.InvariantCulture);
                foreach (var column in _commandMapColumns)
                {
                    object original = mapRow[column.Key, DataRowVersion.Original];
                    object current = mapRow[column.Key, DataRowVersion.Current];
                    if (Equals(original, current)) continue;
                    if (IsCommandCellDeleted(position, column.Value)) continue;
                    hasChanges = true;
                    Permission mapPermission = _commandMapExistingCells.Contains((position, column.Value))
                        ? Permission.EditData : Permission.AddData;
                    if (!UserAuthorizationService.Has(mapPermission))
                    {
                        if (showAuthorizationMessage) UserAuthorizationService.Require(mapPermission);
                        return false;
                    }
                }
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
        bool hasClassOptions = tableData.Definition.Id != "matrix-device-list-type" || _deviceClasses.Count > 0;
        SetActions(writable && hasClassOptions && tableData.Definition.AllowInsert,
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

    private void RestoreDeviceGroupSelection()
    {
        _suppressSelectionEvents = true;
        DeviceGroupBox.SelectedValue = _appliedDeviceGroup;
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
            if (_tableData.Definition.Id == "matrix-device-list-type")
                await ValidateMatrixClassChangesAsync();
            if (_tableData.Definition.Id == "matrix-devices")
            {
                var positionTypes = await _repository.LoadMatrixAsync("matrix-device-list-type", _plcValue!, _matrixClassValue!, _appliedMatrixRecord);
                var groups = await _repository.LoadMatrixAsync("matrix-device-list", _plcValue!, _matrixClassValue!, _appliedMatrixRecord);
                await _repository.ValidateMatrixDeviceReferencesAsync(groups.Rows, positionTypes.Rows, _tableData.Rows, _deviceClasses);
            }
            if (_tableData.Definition.Id == "matrix-commands") ApplyCommandMapChanges();
            await _repository.SaveAsync(_tableData, MatrixScope(_tableData.Definition, _appliedMatrixRecord));
            _tableData.Rows.AcceptChanges();
            _commandMapView?.AcceptChanges();
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

    private void ApplyCommandMapChanges()
    {
        if (_commandMapView == null || _tableData == null) return;
        var scope = MatrixScope(_tableData.Definition, _appliedMatrixRecord);
        foreach (DataRow mapRow in _commandMapView.Rows)
        {
            int position = Convert.ToInt32(mapRow["Position"], CultureInfo.InvariantCulture);
            foreach (var column in _commandMapColumns)
            {
                string value = Convert.ToString(mapRow[column.Key], CultureInfo.InvariantCulture) ?? string.Empty;
                var source = _tableData.Rows.Rows.Cast<DataRow>().FirstOrDefault(row =>
                    row.RowState != DataRowState.Deleted
                    && Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture) == position
                    && Convert.ToInt32(row["CmdNumber"], CultureInfo.InvariantCulture) == column.Value);
                if (source?.RowState == DataRowState.Added && string.IsNullOrWhiteSpace(value))
                {
                    _tableData.Rows.Rows.Remove(source);
                    continue;
                }
                if (source != null)
                {
                    if (!string.Equals(Convert.ToString(source["Cmd"], CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
                        source["Cmd"] = value;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(value)) continue;
                var added = _tableData.Rows.NewRow();
                foreach (var pair in scope) added[pair.Key] = pair.Value;
                added["Position"] = position;
                added["CmdNumber"] = column.Value;
                added["Cmd"] = value;
                _tableData.Rows.Rows.Add(added);
            }
        }
    }

    private async Task ValidateMatrixClassChangesAsync()
    {
        if (_repository == null || _tableData == null) return;
        var positions = new Dictionary<int, int>();
        foreach (DataRow row in _tableData.Rows.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            int position = Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture);
            if (!int.TryParse(Convert.ToString(row["Device_Global_Class"], CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int classNumber))
            {
                if (row.RowState == DataRowState.Unchanged) continue;
                throw new InvalidOperationException($"Для позиции {position} не выбран класс устройства.");
            }
            if (!positions.TryAdd(position, classNumber) && positions[position] != classNumber)
                throw new InvalidOperationException($"Для позиции {position} указано несколько классов устройств.");
        }
        var assignments = await _repository.LoadMatrixAsync("matrix-devices", _plcValue!, _matrixClassValue!, _appliedMatrixRecord);
        foreach (DataRow assignment in assignments.Rows.Rows)
        {
            int position = Convert.ToInt32(assignment["Position"], CultureInfo.InvariantCulture);
            if (!positions.TryGetValue(position, out int expectedClass)
                || assignment["Device_PLC_Class_Number"] == DBNull.Value
                || Convert.ToInt32(assignment["Device_PLC_Class_Number"], CultureInfo.InvariantCulture) != expectedClass)
                throw new InvalidOperationException($"Нельзя изменить тип позиции {position}: сначала удалите или переназначьте связанные объекты.");
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _tableData == null || !_tableData.CanWrite || !_tableData.Definition.AllowInsert) return;
        if (_tableData.Definition.Id == "matrix-devices")
        {
            AssignSelectedDevice();
            return;
        }
        if (!UserAuthorizationService.Require(Permission.AddData)) return;
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
        catch (Exception ex) { StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex); }
    }

    private void AssignSelectedDevice()
    {
        if (!CanAssignDeviceSelection() || _tableData == null) return;
        try
        {
            int group = Convert.ToInt32(_appliedDeviceGroup, CultureInfo.InvariantCulture);
            var position = (MatrixDevicePositionOption)PositionBox.SelectedItem;
            var device = (MatrixDeviceObjectOption)DeviceObjectBox.SelectedItem;
            var existing = FindAssignment(group, position.Position);
            Permission permission = existing == null ? Permission.AddData : Permission.EditData;
            if (!UserAuthorizationService.Require(permission)) return;
            if (existing == null)
            {
                existing = _tableData.Rows.NewRow();
                foreach (var pair in MatrixScope(_tableData.Definition, _appliedMatrixRecord)) existing[pair.Key] = pair.Value;
                existing["DeviceList"] = group;
                existing["Position"] = position.Position;
                _tableData.Rows.Rows.Add(existing);
            }
            existing["Device_PLC"] = device.Plc;
            existing["Device_PLC_Class_Number"] = device.ClassNumber;
            existing["Device_Record"] = device.Record;
            ApplyDeviceGroupFilter();
            var selected = _tableData.Rows.DefaultView.Cast<DataRowView>().FirstOrDefault(item => ReferenceEquals(item.Row, existing));
            if (selected != null)
            {
                RowsGrid.SelectedItem = selected;
                RowsGrid.ScrollIntoView(selected);
            }
            StatusText.Text = $"Объект назначен группе {group}, позиции {position.Position}. Сохраните изменения.";
            UpdateActionState();
        }
        catch (Exception ex) { StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _tableData == null || !_tableData.CanWrite) return;
        if (_tableData.Definition.Id == "matrix-commands" && _commandMapView != null)
        {
            if (!_tableData.Definition.AllowDelete) return;
            if (!TryGetSelectedCommandMapCell(out var mapRow, out string column, out _, out _, out var source)) return;
            string value = Convert.ToString(mapRow[column], CultureInfo.InvariantCulture) ?? string.Empty;
            if (source == null && string.IsNullOrWhiteSpace(value)) return;
            Permission commandPermission = source == null || source.RowState == DataRowState.Added
                ? Permission.AddData : Permission.DeleteData;
            if (!UserAuthorizationService.Require(commandPermission)) return;
            if (source?.RowState == DataRowState.Added) source.Table.Rows.Remove(source);
            else source?.Delete();
            mapRow[column] = string.Empty;
            StatusText.Text = "Команда удалена из выбранной позиции. Сохраните изменения.";
            UpdateEmptyState();
            UpdateActionState();
            return;
        }
        if (!_tableData.Definition.AllowDelete || RowsGrid.SelectedItem is not DataRowView selected) return;
        Permission required = selected.Row.RowState == DataRowState.Added ? Permission.AddData : Permission.DeleteData;
        if (!UserAuthorizationService.Require(required)) return;
        try
        {
            if (selected.Row.RowState == DataRowState.Added) selected.Row.Table.Rows.Remove(selected.Row);
            else selected.Row.Delete();
            StatusText.Text = "Строка помечена для удаления. Перед удалением корня проверяются его зависимости.";
            UpdateEmptyState();
            UpdateActionState();
        }
        catch (Exception ex) { StatusText.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex); }
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
        if (e.Column is DataGridTextColumn textColumn)
        {
            var textStyle = new Style(typeof(TextBlock), textColumn.ElementStyle);
            textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 2, 8, 2)));
            textColumn.ElementStyle = textStyle;
            if (name.Replace("_", "").StartsWith("NameL", StringComparison.OrdinalIgnoreCase))
                textColumn.MinWidth = 150;
        }
        if (_commandMapView != null && _tableData.Definition.Id == "matrix-commands")
        {
            if (name == "Position")
            {
                e.Column.Header = "Position";
                e.Column.IsReadOnly = true;
                e.Column.Width = new DataGridLength(110);
            }
            else if (_commandMapColumns.TryGetValue(name, out int commandNumber))
            {
                e.Column = new DataGridComboBoxColumn
                {
                    Header = $"Cmd {commandNumber}",
                    ItemsSource = MatrixAwlExporter.CommandOptions.Concat(_commandMapView.Rows.Cast<DataRow>()
                        .Select(row => Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? string.Empty)).Distinct().ToArray(),
                    SelectedItemBinding = new Binding($"[{name}]")
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    }
                };
                e.Column.IsReadOnly = !_tableData.CanWrite;
                e.Column.Width = new DataGridLength(110);
                e.Column.SortMemberPath = name;
            }
            else e.Column.IsReadOnly = true;
            return;
        }
        if (_tableData.Definition.Id == "matrix-device-list-type" && name.Equals("Device_Global_Class", StringComparison.OrdinalIgnoreCase))
        {
            e.Column = new DataGridComboBoxColumn
            {
                Header = "Класс устройства",
                SortMemberPath = name,
                ItemsSource = _deviceClasses,
                DisplayMemberPath = nameof(MatrixDeviceClassOption.Name),
                SelectedValuePath = nameof(MatrixDeviceClassOption.Number),
                SelectedValueBinding = new Binding(name) { Mode = BindingMode.TwoWay }
            };
        }
        if (_tableData.Definition.Id == "matrix-devices"
            && new[] { "Device_PLC", "Device_PLC_Class_Number", "Device_Record" }.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            e.Column.IsReadOnly = true;
            return;
        }
        if (name.Equals("PLC", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PLC_Class_Number", StringComparison.OrdinalIgnoreCase)
            || (name.Equals("Record", StringComparison.OrdinalIgnoreCase) && _tableData.Definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            e.Column.Visibility = Visibility.Collapsed;
            return;
        }
        bool inKey = _tableData.Definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool inScope = _tableData.Definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool eligible = _tableData.Definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
            || (inKey && !inScope && !name.Equals("Record", StringComparison.OrdinalIgnoreCase));
        e.Column.IsReadOnly = !_tableData.CanWrite || !eligible;
    }

    private bool HasSelectedMatrix()
    {
        if (IsMatrixChildSelected()) return MatrixRecordBox.SelectedValue != null && MatrixRecordBox.SelectedValue != DBNull.Value;
        return SectionBox.SelectedValue as string == "matrix-list"
            && RowsGrid.SelectedItem is DataRowView row
            && (row.Row.RowState is DataRowState.Unchanged or DataRowState.Modified)
            && row.Row.Table.Columns.Contains("Record");
    }

    private void RowsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_busy || _tableData == null || !_tableData.CanWrite || e.Row.Item is not DataRowView row) { e.Cancel = true; return; }
        string? name = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(name) && e.Column is DataGridBoundColumn bound && bound.Binding is Binding binding)
            name = binding.Path?.Path;
        if (string.IsNullOrWhiteSpace(name)) { e.Cancel = true; return; }
        if (_commandMapView != null && _tableData.Definition.Id == "matrix-commands")
        {
            if (!_commandMapColumns.TryGetValue(name, out int commandNumber)) { e.Cancel = true; return; }
            int position = Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture);
            if (IsCommandCellDeleted(position, commandNumber)) { e.Cancel = true; return; }
            Permission mapPermission = _commandMapExistingCells.Contains((position, commandNumber))
                ? Permission.EditData : Permission.AddData;
            e.Cancel = !UserAuthorizationService.Has(mapPermission);
            return;
        }
        Permission required = row.Row.RowState == DataRowState.Added ? Permission.AddData : Permission.EditData;
        if (!UserAuthorizationService.Has(required)) { e.Cancel = true; return; }
        bool isNew = row.Row.RowState == DataRowState.Added;
        bool isScope = _tableData.Definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool isKey = _tableData.Definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool protectedColumn = _tableData.Definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool editable = _tableData.Definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (_tableData.Definition.Id == "matrix-devices"
            && new[] { "Device_PLC", "Device_PLC_Class_Number", "Device_Record" }.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            return;
        }
        if (isScope || (isKey && !isNew) || (protectedColumn && !isNew) || (!editable && !(isNew && isKey))) e.Cancel = true;
    }

    private sealed class MatrixRecordOption
    {
        public object Record { get; init; } = DBNull.Value;
        public string Caption { get; init; } = "";
    }

    private sealed class DeviceGroupOption
    {
        public int Number { get; init; }
        public string Caption { get; init; } = "";
    }

    private sealed record MatrixDevicePositionOption(int Position, int ClassNumber, string Caption);

    private bool TryGetSelectedCommandMapCell(out DataRowView mapRow, out string column, out int position,
        out int commandNumber, out DataRow? source)
    {
        mapRow = null!;
        column = string.Empty;
        position = 0;
        commandNumber = 0;
        source = null;
        if (_tableData?.Definition.Id != "matrix-commands" || _commandMapView == null
            || RowsGrid.SelectedItem is not DataRowView selected || RowsGrid.CurrentCell.Column == null)
            return false;
        string? columnName = RowsGrid.CurrentCell.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(columnName) || !_commandMapColumns.TryGetValue(columnName, out commandNumber))
            return false;
        position = Convert.ToInt32(selected["Position"], CultureInfo.InvariantCulture);
        int selectedPosition = position;
        int selectedCommandNumber = commandNumber;
        source = _tableData.Rows.Rows.Cast<DataRow>().FirstOrDefault(row => row.RowState != DataRowState.Deleted
            && Convert.ToInt32(row["Position"], CultureInfo.InvariantCulture) == selectedPosition
            && Convert.ToInt32(row["CmdNumber"], CultureInfo.InvariantCulture) == selectedCommandNumber);
        mapRow = selected;
        column = columnName;
        return true;
    }

    private bool IsCommandCellDeleted(int position, int commandNumber)
        => _tableData?.Definition.Id == "matrix-commands"
            && _tableData.Rows.Rows.Cast<DataRow>().Any(row => row.RowState == DataRowState.Deleted
                && Convert.ToInt32(row["Position", DataRowVersion.Original], CultureInfo.InvariantCulture) == position
                && Convert.ToInt32(row["CmdNumber", DataRowVersion.Original], CultureInfo.InvariantCulture) == commandNumber);
}
