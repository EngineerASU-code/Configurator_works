#nullable enable
using System;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Configurator.Core.Projects;
using Configurator.Core.Workspace;
using Configurator.Infrastructure.Projects;

namespace Configurator;

public partial class MainWindow
{
    private readonly ProjectPackageService _projectPackageService = new();
    private readonly Configurator.Core.Projects.ProjectDataSourceContext _projectDataSource = new();
    private ConfiguratorProject? _openedLocalProject;
    private string? _openedLocalProjectPath;
    // Keep multiple Offline projects loaded in this session, keyed by their canonical file path.
    private readonly Dictionary<string, ConfiguratorProject> _offlineProjects = new(StringComparer.OrdinalIgnoreCase);
    // Hold a Windows file handle for every opened package. Omitting FileShare.Delete prevents
    // Explorer/other processes from deleting or renaming an in-use project. Writes are routed
    // through this handle by ProjectPackageService so atomic replacement cannot bypass the lock.
    private readonly Dictionary<string, FileStream> _offlineProjectLocks = new(StringComparer.OrdinalIgnoreCase);
    private bool _isOfflineWorkspace;
    private string? _offlineCurrentTableName;
    private DataTable? _offlineCurrentDataTable;
    private List<ColumnInfo> _offlineCurrentColumns = new();
    private readonly Dictionary<DataRow, string> _offlineRowRecordKeys = new();
    private readonly Dictionary<string, ProjectRecord> _offlineCurrentRecordIndex = new(StringComparer.Ordinal);
    // Snapshot of the currently displayed table before grid edits, used to record accurate before/after values.
    private readonly Dictionary<string, Dictionary<string, object>> _offlineBaselineRows = new(StringComparer.Ordinal);
    private bool _offlineLoadingTable;
    private bool _offlineGridDirty;
    private bool _offlineProjectDirty;
    private long _offlineModeSwitchCount;
    private long _offlineTableOpenCount;
    private readonly DispatcherTimer _offlineAutoSaveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _offlineAutoSaveInProgress;
    private readonly SemaphoreSlim _offlineSaveGate = new(1, 1);

    private async void ChooseOfflineProject_Click(object sender, RoutedEventArgs e)
    {
        // Do not change workspace state before the user has picked a file. Cancel is a no-op:
        // the active project, current mode and mode buttons must remain usable.
        bool wasOffline = _isOfflineWorkspace;
        bool opened = await OpenLocalProjectAsync();
        if (!opened && wasOffline && _openedLocalProject != null)
        {
            _isOfflineWorkspace = true;
            _offlineSelectionRequired = false;
            UpdateWorkspaceModeUi();
        }
    }

    private async void ReturnOnlineFromSelection_Click(object sender, RoutedEventArgs e)
    {
        await SwitchToOnlineWorkspaceAsync();
    }

    private async void MenuCloseOfflineProject_Click(object sender, RoutedEventArgs e)
    {
        if (!_isOfflineWorkspace || _openedLocalProject == null || string.IsNullOrWhiteSpace(_openedLocalProjectPath))
            return;

        _offlineAutoSaveTimer.Stop();
        while (_offlineAutoSaveInProgress)
            await Task.Delay(25);
        _rightPanel?.DataTableGrid?.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        _rightPanel?.DataTableGrid?.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        if (_offlineProjectDirty || _offlineGridDirty)
        {
            var choice = MessageBox.Show(this,
                "В Offline-проекте есть несохранённые изменения. Сохранить их перед закрытием?\n\nДа — сохранить; Нет — закрыть без сохранения последних изменений; Отмена — вернуться к проекту.",
                "Закрытие Offline-проекта", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel)
            {
                _offlineAutoSaveTimer.Start();
                return;
            }
            if (choice == MessageBoxResult.Yes)
            {
                try { await SaveOfflineProjectAsync(); }
                catch (Exception ex)
                {
                    Configurator.Infrastructure.ModernDialog.Show(this,
                        "Проект не закрыт, потому что сохранить изменения не удалось.\n\n" + ex.Message,
                        "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
                    _offlineAutoSaveTimer.Start();
                    return;
                }
            }
        }

        var path = Path.GetFullPath(_openedLocalProjectPath);
        _projectEditor?.ClearWorkspace();
        _templateEditor?.ClearWorkspace();
        _projectDataSource.Set(null);
        _offlineProjects.Remove(path);
        if (_offlineProjectLocks.Remove(path, out var projectLock))
        {
            try { await projectLock.DisposeAsync(); }
            catch { try { projectLock.Dispose(); } catch { } }
        }

        _openedLocalProject = null;
        _openedLocalProjectPath = null;
        _isOfflineWorkspace = false;
        _workspaceModeChosen = false;
        _offlineSelectionRequired = true;
        _offlineProjectDirty = false;
        _offlineGridDirty = false;
        _offlineCurrentDataTable = null;
        _offlineCurrentTableName = null;
        _offlineCurrentColumns.Clear();
        _offlineRowRecordKeys.Clear();
        _offlineCurrentRecordIndex.Clear();
        if (_rightPanel?.DataTableGrid != null) _rightPanel.DataTableGrid.ItemsSource = null;
        if (_rightPanel?.TableTitle != null) _rightPanel.TableTitle.Text = "Проект не выбран";
        _activeVm = null;
        _activeDbName = string.Empty;
        _fieldsBuilder?.Clear();
        SetFieldsPanelExpanded(false, false);
        _currentMode = 1;
        EditorModeView.Visibility = Visibility.Collapsed;
        Mode2View.Visibility = Visibility.Collapsed;
        Mode3View.Visibility = Visibility.Collapsed;
        Mode4View.Visibility = Visibility.Collapsed;
        UpdateWorkspaceModeUi();
        CurrentProjectText.Content = "не выбран";
        StatusText.Text = "Проект закрыт. Выберите локальный проект или перейдите в Online.";
        SessionTestLog.Trace("OFFLINE-PROJECT-CLOSE", $"closed='{path}'; dirtyChoiceHandled=true; sqlUsed=false");
    }

    private async void MenuFileSaveOfflineProject_Click(object sender, RoutedEventArgs e)
    {
        if (!_isOfflineWorkspace) return;
        try
        {
            await SaveOfflineProjectAsync();
            Configurator.Infrastructure.ModernDialog.Show(this,
                "Изменения сохранены в тот же открытый файл .cfgproj. Новый файл не создавался; Online-БД не изменялась.",
                "Offline-проект сохранён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show(this, "Не удалось сохранить Offline-проект: " + Configurator.Infrastructure.UserErrorMessage.Format(ex),
                "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MenuFileSaveLocalProject_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Require(Permission.ExportData)) return;
        if (_isOfflineWorkspace)
        {
            Configurator.Infrastructure.ModernDialog.Show(this,
                "Создание локальной копии из Online доступно только в режиме Online. Для сохранения текущего файла используйте «Сохранить Offline-проект в открытый файл».",
                "Команда недоступна в Offline", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var database = !string.IsNullOrWhiteSpace(_selectedDbName) ? _selectedDbName : _connection.ConnectedDatabases.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(database))
        {
            Configurator.Infrastructure.ModernDialog.Show("Сначала подключите и выберите базу данных. Локальный проект будет содержать снимок её таблиц.", "Сохранить локальный проект", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить локальный проект Configurator",
            Filter = "Локальный проект Configurator (*.cfgproj)|*.cfgproj",
            DefaultExt = ".cfgproj", AddExtension = true,
            FileName = $"{database}_{DateTime.Now:yyyyMMdd_HHmm}.cfgproj"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            StatusText.Text = $"Чтение таблиц базы {database}...";
            var db = new DatabaseService(_connection.ToConnectionString(database));
            var tableList = await db.GetTableListAsync();
            var project = new ConfiguratorProject();
            project.Manifest.Name = database;
            project.Settings.DisplayName = database;
            project.Settings.DatabaseLabel = database;
            project.Settings.PreferredMode = "Online"; // сохраняем текущий рабочий режим; переключение будет отдельным этапом
            project.Settings.AllowOnlineWrites = false;
            project.Architecture["snapshotKind"] = "database-table-snapshot";
            project.Architecture["sourceDatabase"] = database;
            project.Architecture["sourceServer"] = _connection.Server ?? string.Empty;
            project.Architecture["capturedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            try
            {
                var projectSettings = await new ProjectCompareSettingsStore(_connection.Server ?? string.Empty, database).LoadAsync();
                project.TextLists = projectSettings.TextLists.Select(list => JsonSerializer.Deserialize<ProjectTextList>(JsonSerializer.Serialize(list))!).ToList();
                project.ThemePresets = projectSettings.ThemePresets.Select(theme => theme.Clone()).ToList();
            }
            catch (Exception settingsError)
            {
                throw new InvalidOperationException("Не удалось перенести списки и темы проекта в локальную копию: " + settingsError.Message, settingsError);
            }

            // Capture project logic alongside the physical database snapshot. These are
            // project data, not credentials; the local package must travel independently.
            try
            {
                var relationDocument = new GraphRelationStore(_connection.Server ?? string.Empty, database).Load();
                project.GraphRelations = JsonSerializer.SerializeToNode(relationDocument) as JsonObject ?? new JsonObject();
                project.Architecture["graphRelations"] = JsonSerializer.SerializeToNode(relationDocument);
            }
            catch (Exception relationError)
            {
                throw new InvalidOperationException("Не удалось сохранить логические связи графа в локальный проект: " + relationError.Message, relationError);
            }
            try
            {
                // Snapshot SQL foreign-key relations and schema metadata as portable project data.
                // Offline readers use this captured model; they must never re-query SQL Server.
                var physicalSchema = await new DatabaseArchitectureAnalyzer(_connection.ToConnectionString(database)).AnalyzeAsync();
                // Preserve the complete analyzer result, not only the relation list:
                // tables/columns/keys, physical relations, inferred relations and rule metadata
                // must travel with the snapshot. Offline consumes this saved model and does not
                // regenerate it by contacting SQL Server.
                project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(physicalSchema);
                project.Architecture["physicalRelations"] = JsonSerializer.SerializeToNode(physicalSchema.Relations);
                project.Architecture["physicalSchemaCapturedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            }
            catch (Exception relationError)
            {
                throw new InvalidOperationException("Не удалось сохранить физические связи SQL Server в локальный проект: " + relationError.Message, relationError);
            }
            try
            {
                var classLibrary = new Configurator.Infrastructure.Architecture.TemplateLibraryStore().Load();
                project.Architecture["classDefinitions"] = JsonSerializer.SerializeToNode(classLibrary);
            }
            catch (Exception classError)
            {
                throw new InvalidOperationException("Не удалось сохранить библиотеку классов в локальный проект: " + classError.Message, classError);
            }
            try
            {
                var functionalBlocks = new FunctionalBlockStore(_connection.Server ?? string.Empty, database).Load();
                new FunctionalBlockStore(project).Save(functionalBlocks);
            }
            catch (Exception blockError)
            {
                throw new InvalidOperationException("Не удалось сохранить функциональные блоки в локальный проект: " + blockError.Message, blockError);
            }

            int tableCount = 0;
            int rowCount = 0;
            foreach (DataRow tableRow in tableList.Rows)
            {
                var schema = Convert.ToString(tableRow["TABLE_SCHEMA"], CultureInfo.InvariantCulture) ?? "dbo";
                var tableName = Convert.ToString(tableRow["TABLE_NAME"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(tableName)) continue;
                var qualifiedName = $"{schema}.{tableName}";
                StatusText.Text = $"Локальный снимок: {qualifiedName}...";
                var data = await db.GetAllTableRowsAsync(qualifiedName);
                var keyColumns = await db.GetPrimaryKeyColumnsAsync(qualifiedName);
                var columns = await db.GetTableColumnsAsync(qualifiedName);
                var hasStableKey = keyColumns.Count > 0 && keyColumns.All(name => data.Columns.Contains(name));
                // A synthetic snapshot key belongs to ProjectRecord.Key, not to the
                // captured SQL table schema. ProjectMapping.KeyColumn must always refer
                // to a real column because package validation checks it against TableSchemas.
                // For tables without a declared primary key, use an existing column only
                // as the mapping anchor; HasStableKey remains false and writes stay disabled.
                var mappingKeyColumn = hasStableKey
                    ? keyColumns[0]
                    : columns.FirstOrDefault()?.Name;
                if (string.IsNullOrWhiteSpace(mappingKeyColumn))
                    throw new InvalidDataException($"Не удалось создать сопоставление таблицы '{qualifiedName}': схема не содержит колонок.");

                var mapping = new ProjectMapping
                {
                    EntityName = qualifiedName, TableName = qualifiedName,
                    KeyColumn = mappingKeyColumn,
                    KeyColumns = hasStableKey ? keyColumns : new List<string>(),
                    HasStableKey = hasStableKey, ReadEnabled = true, WriteEnabled = false
                };
                project.Mappings.Add(mapping);
                project.TableSchemas.Add(new ProjectTableSchema
                {
                    TableName = qualifiedName, KeyColumns = hasStableKey ? keyColumns : new List<string>(), HasStableKey = hasStableKey,
                    Columns = columns.Select(column => new ProjectColumnSchema
                    {
                        Name = column.Name, DataType = column.DataType, IsNullable = column.IsNullable,
                        IsIdentity = column.IsIdentity, IsComputed = column.IsComputed, MaxLength = column.MaxLength,
                        Precision = column.Precision, Scale = column.Scale, HasDefault = column.HasDefault, IsReadOnly = column.IsReadOnly
                    }).ToList()
                });
                int rowIndex = 0;
                foreach (DataRow row in data.Rows)
                {
                    var values = new JsonObject();
                    foreach (DataColumn column in data.Columns)
                    {
                        object value = row[column] == DBNull.Value ? null! : row[column];
                        JsonNode? node;
                        try { node = value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType()); }
                        catch { node = JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)); }
                        values[column.ColumnName] = node;
                    }
                    var recordKey = hasStableKey
                        ? BuildStableRecordKey(values, keyColumns)
                        : "snapshot:" + (rowIndex++).ToString(CultureInfo.InvariantCulture);
                    project.LocalRecords.Add(new ProjectRecord { TableName = qualifiedName, Key = recordKey, Values = values });
                }
                rowCount += data.Rows.Count;
                tableCount++;
            }

            project.BaselineRecords = project.LocalRecords.Select(CloneProjectRecord).ToList();
            await _projectPackageService.SaveAsync(dialog.FileName, project);
            StatusText.Text = "Локальный проект сохранён";
            Mouse.OverrideCursor = null;
            Configurator.Infrastructure.ModernDialog.Show($"Локальный проект сохранён успешно.\n\nБаза: {database}\nТаблиц: {tableCount}\nСтрок: {rowCount}\nФайл: {dialog.FileName}\n\nЭто независимая копия данных в формате .cfgproj. Исходная Online-БД не изменялась.", "Локальный проект сохранён", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка локального сохранения";
            Configurator.Infrastructure.ModernDialog.Show($"Не удалось создать локальный проект. Исходная Online-БД не изменялась.\n\n{Configurator.Infrastructure.UserErrorMessage.Format(ex)}", "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private async void MenuFileOpenLocalProject_Click(object sender, RoutedEventArgs e)
    {
        if (_isOfflineWorkspace)
        {
            Configurator.Infrastructure.ModernDialog.Show(this,
                "Открытие/смена локальной копии — команда Online. Сначала явно перейдите в Online.",
                "Команда недоступна в Offline", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await OpenLocalProjectAsync();
    }

    private async Task<bool> OpenLocalProjectAsync(string? requestedPath = null)
    {
        // Switching between projects already present in the left tree preserves the user's
        // current mode. A newly selected/imported project starts in Mode 1 intentionally.
        bool switchingExistingProject = !string.IsNullOrWhiteSpace(requestedPath);
        string selectedFile;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            var dialog = new OpenFileDialog
            {
                Title = "Добавить / открыть локальный проект Configurator",
                Filter = "Локальный проект Configurator (*.cfgproj)|*.cfgproj",
                CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return false;
            selectedFile = dialog.FileName;
        }
        else selectedFile = requestedPath;
        if (_currentMode == 4 && !FunctionalBlockEditorControl.ConfirmPendingEdits()) return false;
        bool resumeAutoSave = _offlineAutoSaveTimer.IsEnabled;
        FileStream? candidateLock = null;
        bool candidateLoaded = false;
        bool candidateRegistered = false;
        try
        {
            string selectedPath = Path.GetFullPath(selectedFile);
            if (!_offlineProjects.TryGetValue(selectedPath, out var project))
            {
                project = await _projectPackageService.OpenAsync(selectedPath);
                candidateLock = new FileStream(selectedPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.Read, 81920, FileOptions.Asynchronous);
                candidateLoaded = true;
            }

            // Stop the autosave timer before changing the active file. An already-running
            // autosave must finish against the old project before we switch references.
            _offlineAutoSaveTimer.Stop();
            while (_offlineAutoSaveInProgress)
                await Task.Delay(25);

            // Save the currently active project while its project/path/grid references still match.
            if (_isOfflineWorkspace && _openedLocalProject != null)
                await SaveOfflineProjectAsync();
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            StatusText.Text = "Открытие локального проекта...";
            if (candidateLoaded)
            {
                _offlineProjectLocks[selectedPath] = candidateLock!;
                _offlineProjects[selectedPath] = project;
                candidateRegistered = true;
            }
            // The Mode 1 grid is a mutable working copy. Never carry its DataTable or row-key
            // map into another project: the next autosave would otherwise write the previous
            // project's visible rows into the newly selected package.
            _offlineCurrentDataTable = null;
            _offlineCurrentTableName = null;
            _offlineCurrentColumns = new List<ColumnInfo>();
            _offlineRowRecordKeys.Clear();
            if (_rightPanel?.DataTableGrid != null) _rightPanel.DataTableGrid.ItemsSource = null;
            if (_rightPanel?.TableTitle != null) _rightPanel.TableTitle.Text = string.Empty;

            _openedLocalProject = project;
            _openedLocalProjectPath = selectedPath;
            _offlineProjectDirty = false;
            _offlineGridDirty = false;
            _offlineCurrentRecordIndex.Clear();
            _projectDataSource.Set(new Configurator.Core.Projects.OfflineProjectDataSource(project));
            OfflineModeButton.IsEnabled = true;
            UpdateWorkspaceModeUi();
            StatusText.Text = "Локальный проект проверен";
            // Selecting/importing a new project is the explicit exception: start it at
            // the main database screen. Switching between already-open tree projects
            // preserves Mode 1/2/3.
            if (!switchingExistingProject) _currentMode = 1;
            SwitchToOffline_Click(this, new RoutedEventArgs());
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка открытия локального проекта";
            Configurator.Infrastructure.ModernDialog.Show($"Не удалось открыть локальный проект.\n\n{Configurator.Infrastructure.UserErrorMessage.Format(ex)}", "Ошибка открытия", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            if (candidateLoaded && !candidateRegistered && candidateLock is not null)
            {
                try { await candidateLock.DisposeAsync(); }
                catch { try { candidateLock.Dispose(); } catch { } }
            }
            Mouse.OverrideCursor = null;
            if (resumeAutoSave && _isOfflineWorkspace && _openedLocalProject != null && !_offlineAutoSaveTimer.IsEnabled)
                _offlineAutoSaveTimer.Start();
        }
    }

private static string BuildStableRecordKey(JsonObject values, IReadOnlyList<string> keyColumns)
    {
        var key = new JsonArray();
        foreach (var column in keyColumns)
        {
            if (!values.TryGetPropertyValue(column, out var node) || node is null)
                throw new InvalidDataException($"Невозможно сформировать ключ записи: поле '{column}' отсутствует или содержит NULL.");
            key.Add(node.DeepClone());
        }
        return key.ToJsonString();
    }

    private static ProjectRecord CloneProjectRecord(ProjectRecord source) => new ProjectRecord
        {
            TableName = source.TableName,
            Key = source.Key,
            RowVersion = source.RowVersion,
            Values = (JsonObject)source.Values.DeepClone()
        };

        private void SwitchToOffline_Click(object sender, RoutedEventArgs e)
        {
            _databaseRefreshCancellation?.Cancel();
            var switchTimer = Stopwatch.StartNew();
            if (_openedLocalProject is null || string.IsNullOrWhiteSpace(_openedLocalProjectPath))
            {
                // Entering Offline with no loaded project only presents the choice screen.
                // The file picker is opened exclusively by the explicit "Choose local project" button.
                _isOfflineWorkspace = false;
                _offlineSelectionRequired = true;
                UpdateWorkspaceModeUi();
                StatusText.Text = "Выберите локальный проект или перейдите в Online";
                SessionTestLog.Trace("WORKSPACE-PERF", $"switch-to-offline-choice-ms={switchTimer.ElapsedMilliseconds}; projectLoaded=false");
                return;
            }

            if (!File.Exists(_openedLocalProjectPath))
            {
                _offlineSelectionRequired = true;
                _isOfflineWorkspace = false;
                UpdateWorkspaceModeUi();
                Configurator.Infrastructure.ModernDialog.Show(this,
                    "Файл Offline-проекта больше не существует по сохранённому пути. Он не будет создан автоматически. Выберите существующий .cfgproj.",
                    "Offline-файл не найден", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _offlineSelectionRequired = false;
            _workspaceModeChosen = true;
            _isOfflineWorkspace = true;
            _projectDataSource.Set(new OfflineProjectDataSource(_openedLocalProject));
            _offlineCurrentDataTable = null;
            _offlineCurrentTableName = null;
            _offlineCurrentColumns = new List<ColumnInfo>();
            _offlineRowRecordKeys.Clear();
            if (_rightPanel?.DataTableGrid != null) _rightPanel.DataTableGrid.ItemsSource = null;
            if (_rightPanel?.TableTitle != null) _rightPanel.TableTitle.Text = string.Empty;
            _activeVm = null;
            if (!_offlineAutoSaveTimer.IsEnabled) _offlineAutoSaveTimer.Start();
            UpdateWorkspaceModeUi();
            WorkspaceModeHint.Text = "";
            int modeToRestore = _currentMode is >= 1 and <= 4 &&
                (_currentMode != 4 || PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 4))
                ? _currentMode : 1;
            EditorModeView.Visibility = modeToRestore == 1 ? Visibility.Visible : Visibility.Collapsed;
            Mode2View.Visibility = modeToRestore == 2 ? Visibility.Visible : Visibility.Collapsed;
            Mode3View.Visibility = modeToRestore == 3 ? Visibility.Visible : Visibility.Collapsed;
            Mode4View.Visibility = modeToRestore == 4 ? Visibility.Visible : Visibility.Collapsed;
            _currentMode = modeToRestore;
            _offlineModeSwitchCount++;
            System.Diagnostics.Debug.WriteLine($"[OfflinePerf] mode-switch #{_offlineModeSwitchCount}: mode={modeToRestore}, records={_openedLocalProject.LocalRecords.Count}, schemas={_openedLocalProject.TableSchemas.Count}");
            UpdateModeNavigation(modeToRestore);
            NavDatabase.IsEnabled = !_offlineSelectionRequired;
            NavTemplates.IsEnabled = !_offlineSelectionRequired;
            NavProject.IsEnabled = !_offlineSelectionRequired;
            NavFbEditor.IsEnabled = !_offlineSelectionRequired;
            // The active editor is bound below, after the workspace UI state is stable.
            // Do not configure both editors: that would duplicate metadata/object loads.
            MenuDatabase.IsEnabled = false;
            CurrentProjectText.IsEnabled = true;
            CurrentProjectText.Content = _openedLocalProject.Manifest.Name + " · Offline";
            _activeVm = null;
            _activeDbName = _openedLocalProject.Manifest.Name;
            _fieldsBuilder?.Clear();
            SetFieldsPanelExpanded(false, false);
            BuildOfflineProjectTree();
            UpdateWorkspaceModeUi();
            UpdateCurrentProjectText();
            StatusText.Text = "Offline · изменения сохраняются только в локальный проект";
            SetOfflineEditorButtons();
            ApplyAuthorizationUi();
            // Critical: switching the workspace directly does not invoke SetMode. Bind the
            // active Mode 2/3 editor here so it cannot retain its previous SQL connection.
            ConfigureActiveProjectEditor(modeToRestore, string.Empty);
            switchTimer.Stop();
            SessionTestLog.Trace("WORKSPACE-PERF", $"switch-to-offline-setup-ms={switchTimer.ElapsedMilliseconds}; mode={modeToRestore}; localRecords={_openedLocalProject.LocalRecords.Count}; sqlUsed=false");
        }

        private void BuildOfflineProjectTree()
        {
            if (_openedLocalProject is null) return;
            _leftPanel.ServerTree.Items.Clear();
            _loadedTreeNodes.Clear();

            foreach (var entry in _offlineProjects.OrderBy(x => x.Value.Manifest.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string projectPath = entry.Key;
                var project = entry.Value;
                bool isActive = string.Equals(projectPath, _openedLocalProjectPath, StringComparison.OrdinalIgnoreCase);
                string projectName = string.IsNullOrWhiteSpace(project.Manifest.Name)
                    ? Path.GetFileNameWithoutExtension(projectPath) : project.Manifest.Name;
                var projectNode = new System.Windows.Controls.TreeViewItem
                {
                    Header = CreateTreeHeader((isActive ? "✓  " : "▣  ") + "OFFLINE · " + projectName,
                        isActive ? "АКТИВНЫЙ ЛОКАЛЬНЫЙ ПРОЕКТ" : "ОТКРЫТЫЙ ЛОКАЛЬНЫЙ ПРОЕКТ", true),
                    Tag = projectPath,
                    IsExpanded = isActive,
                    Padding = new Thickness(7, 6, 7, 6),
                    Margin = new Thickness(1, 3, 1, 3),
                    ToolTip = projectPath
                };
                projectNode.Selected += async (sender, args) =>
                {
                    args.Handled = true;
                    if (!string.Equals(projectPath, _openedLocalProjectPath, StringComparison.OrdinalIgnoreCase))
                        await OpenLocalProjectAsync(projectPath);
                };

                if (isActive)
                {
                    var tableNames = project.Mappings.Select(x => x.TableName)
                        .Concat(project.TableSchemas.Select(x => x.TableName))
                        .Concat(project.LocalRecords.Select(x => x.TableName))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
                    foreach (var fullName in tableNames)
                    {
                        var parts = fullName.Split('.', 2);
                        var schema = parts.Length == 2 ? parts[0] : "dbo";
                        var tableName = parts.Length == 2 ? parts[1] : parts[0];
                        var item = new System.Windows.Controls.TreeViewItem
                        {
                            Header = CreateTreeHeader(fullName, "ТАБЛИЦА", false),
                            Tag = new TableNodeInfo { DatabaseName = project.Manifest.Name, Schema = schema, TableName = tableName },
                            Padding = new Thickness(5, 4, 5, 4),
                            ToolTip = fullName
                        };
                        item.Selected += (sender, args) =>
                        {
                            args.Handled = true;
                            OpenOfflineTable(fullName);
                        };
                        projectNode.Items.Add(item);
                    }
                }
                else
                {
                    // A small placeholder makes it clear the project is loaded, while its
                    // table list is rebuilt after activating it so table actions always target
                    // the correct project's isolated working copy.
                    projectNode.Items.Add(new System.Windows.Controls.TreeViewItem
                    {
                        Header = "Выбрать проект для просмотра таблиц",
                        IsEnabled = false
                    });
                }
                _leftPanel.ServerTree.Items.Add(projectNode);
            }

            _leftPanel.SetLoadingStatus($"Offline · открыто проектов: {_offlineProjects.Count}", false);
            _leftPanel.HidePlaceholder();
            var activeNode = _leftPanel.ServerTree.Items.OfType<System.Windows.Controls.TreeViewItem>()
                .FirstOrDefault(x => string.Equals(x.Tag as string, _openedLocalProjectPath, StringComparison.OrdinalIgnoreCase));
            if (activeNode != null)
            {
                activeNode.IsExpanded = true;
                // Opening a project must not implicitly open its first table. The user
                // explicitly selects a table from the tree when ready to work with it.
                ClearActiveTable();
                _offlineCurrentTableName = null;
                _offlineCurrentDataTable = null;
                _offlineCurrentColumns.Clear();
                _rightPanel.TableTitle.Text = "Выберите таблицу слева";
                _rightPanel.BtnAdd.IsEnabled = false;
                _rightPanel.BtnUpdate.IsEnabled = false;
                _rightPanel.BtnDuplicate.IsEnabled = false;
                _rightPanel.BtnDelete.IsEnabled = false;
            }
        }

        private void OpenOfflineTable(string tableName)
        {
            if (_openedLocalProject is null) return;
            var timer = Stopwatch.StartNew();
            // Commit only if the visible grid actually changed. Merely browsing must not
            // rewrite every record in the table or scan the entire project.
            CommitOfflineGridToProject();
            _offlineCurrentTableName = tableName;
            _offlineRowRecordKeys.Clear();
            _offlineCurrentRecordIndex.Clear();
            var records = _openedLocalProject.LocalRecords.Where(x => x.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)).ToList();
            _offlineBaselineRows.Clear();
            foreach (var record in records)
            {
                _offlineCurrentRecordIndex[record.Key] = record;
                _offlineBaselineRows[record.Key] = OfflineRecordValues(record);
            }
            var schema = _openedLocalProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
            var columnNames = (schema?.Columns.Select(x => x.Name) ?? Enumerable.Empty<string>())
                .Concat(records.SelectMany(r => r.Values.Select(kv => kv.Key)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var table = new DataTable(tableName);
            foreach (var name in columnNames) table.Columns.Add(name, typeof(string));
            _offlineLoadingTable = true;
            try
            {
                foreach (var record in records)
                {
                    var row = table.NewRow();
                    foreach (var name in columnNames)
                        row[name] = record.Values.TryGetPropertyValue(name, out var node) && node is not null ? node.ToString() : DBNull.Value;
                    table.Rows.Add(row);
                    _offlineRowRecordKeys[row] = record.Key;
                }
            }
            finally { _offlineLoadingTable = false; }
            table.ColumnChanged += OfflineGrid_Changed;
            table.RowChanged += OfflineGrid_RowChanged;
            table.RowDeleted += OfflineGrid_RowDeleted;
            _offlineCurrentDataTable = table;
            _offlineGridDirty = false;
            _offlineCurrentColumns = (schema?.Columns ?? new List<ProjectColumnSchema>()).Select(c => new ColumnInfo
            {
                Name = c.Name, DataType = c.DataType, IsIdentity = c.IsIdentity, IsNullable = c.IsNullable,
                IsComputed = c.IsComputed, MaxLength = c.MaxLength, Precision = c.Precision, Scale = c.Scale, HasDefault = c.HasDefault
            }).ToList();
            foreach (var name in columnNames)
                if (!_offlineCurrentColumns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    _offlineCurrentColumns.Add(new ColumnInfo { Name = name, DataType = "nvarchar", IsNullable = true });

            _rightPanel.DataTableGrid.ItemsSource = null;
            _fieldsBuilder.DisplayPrefix = "";
            _fieldsBuilder.Generate(_offlineCurrentColumns, schema?.KeyColumns.FirstOrDefault() ?? string.Empty);
            _rightPanel.TableTitle.Text =  $"{_openedLocalProject.Manifest.Name} → {tableName}";
            BindOfflineColumnView(table);
            _rightPanel.BtnFirst.IsEnabled = _rightPanel.BtnPrev.IsEnabled = _rightPanel.BtnNext.IsEnabled = _rightPanel.BtnLast.IsEnabled = false;
            _rightPanel.BtnAdd.IsEnabled = ProjectWriteGuard.IsDataTableWritable(tableName) && UserAuthorizationService.Has(Permission.Mode1Add);
            _rightPanel.BtnUpdate.IsEnabled = false;
            _rightPanel.BtnDuplicate.IsEnabled = ProjectWriteGuard.IsDataTableWritable(tableName) && UserAuthorizationService.Has(Permission.Mode1Add) && _rightPanel.DataTableGrid.SelectedItem != null;
            _rightPanel.BtnDelete.IsEnabled = false;
            _offlineTableOpenCount++;
            System.Diagnostics.Debug.WriteLine($"[OfflinePerf] table-open #{_offlineTableOpenCount}: {tableName}, rows={table.Rows.Count}, columns={table.Columns.Count}, ms={timer.ElapsedMilliseconds}");
            StatusText.Text = $"Offline · {tableName}: {table.Rows.Count:N0} строк. Сохранение — в .cfgproj; SQL Server не затрагивается.";
        }

        private void OfflineGrid_Changed(object? sender, DataColumnChangeEventArgs e)
        {
            if (_offlineLoadingTable) return;
            _offlineGridDirty = true;
            _offlineProjectDirty = true;
        }

        private void OfflineGrid_RowChanged(object? sender, DataRowChangeEventArgs e)
        {
            if (_offlineLoadingTable || e.Action == DataRowAction.Commit) return;
            _offlineGridDirty = true;
            _offlineProjectDirty = true;
        }

        private void OfflineGrid_RowDeleted(object? sender, DataRowChangeEventArgs e)
        {
            if (_offlineLoadingTable) return;
            _offlineGridDirty = true;
            _offlineProjectDirty = true;
        }

        private void CommitOfflineGridToProject()
        {
            if (!_isOfflineWorkspace || _openedLocalProject is null || _offlineCurrentDataTable is null || _offlineCurrentTableName is null || !_offlineGridDirty) return;
            ProjectWriteGuard.EnsureDataTableWritable(_offlineCurrentTableName);
            var timer = Stopwatch.StartNew();
            _rightPanel.DataTableGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            _rightPanel.DataTableGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            var schema = _openedLocalProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(_offlineCurrentTableName, StringComparison.OrdinalIgnoreCase));
            var currentKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (DataRow row in _offlineCurrentDataTable.Rows)
            {
                if (!_offlineRowRecordKeys.TryGetValue(row, out var key))
                {
                    key = "offline:" + Guid.NewGuid().ToString("N");
                    _offlineRowRecordKeys[row] = key;
                }
                currentKeys.Add(key);
                if (!_offlineCurrentRecordIndex.TryGetValue(key, out var record))
                {
                    record = new ProjectRecord { TableName = _offlineCurrentTableName, Key = key };
                    _openedLocalProject.LocalRecords.Add(record);
                    _offlineCurrentRecordIndex[key] = record;
                }
                var values = new JsonObject();
                foreach (DataColumn column in _offlineCurrentDataTable.Columns)
                {
                    var raw = row[column] == DBNull.Value ? null : Convert.ToString(row[column], CultureInfo.InvariantCulture);
                    var columnSchema = schema?.Columns.FirstOrDefault(x => x.Name.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));
                    if (raw is null) values[column.ColumnName] = null;
                    else if (columnSchema is not null && IsNumericOfflineType(columnSchema.DataType) && decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)) values[column.ColumnName] = JsonValue.Create(number);
                    else if (columnSchema is not null && columnSchema.DataType.Equals("bit", StringComparison.OrdinalIgnoreCase) && bool.TryParse(raw, out var flag)) values[column.ColumnName] = JsonValue.Create(flag);
                    else values[column.ColumnName] = JsonValue.Create(raw);
                }
                record.Values = values;
            }
            var removedKeys = _offlineCurrentRecordIndex.Keys.Where(key => !currentKeys.Contains(key)).ToList();
            if (removedKeys.Count > 0)
            {
                var removed = new HashSet<string>(removedKeys, StringComparer.Ordinal);
                _openedLocalProject.LocalRecords.RemoveAll(x => x.TableName.Equals(_offlineCurrentTableName, StringComparison.OrdinalIgnoreCase) && removed.Contains(x.Key));
                foreach (var key in removedKeys) _offlineCurrentRecordIndex.Remove(key);
            }
            _offlineGridDirty = false;
            _offlineProjectDirty = true;
            RecordOfflineGridChanges(schema);
            System.Diagnostics.Debug.WriteLine($"[OfflinePerf] grid-commit: {_offlineCurrentTableName}, rows={_offlineCurrentDataTable.Rows.Count}, ms={timer.ElapsedMilliseconds}");
        }

        private static Dictionary<string, object> OfflineRecordValues(ProjectRecord record)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (record?.Values != null)
                foreach (var pair in record.Values)
                    values[pair.Key] = pair.Value == null ? DBNull.Value : pair.Value.ToString();
            return values;
        }

        private void RecordOfflineGridChanges(ProjectTableSchema? schema)
        {
            if (!_isOfflineWorkspace || _openedLocalProject == null || string.IsNullOrWhiteSpace(_offlineCurrentTableName)) return;
            var current = _openedLocalProject.LocalRecords
                .Where(x => x.TableName.Equals(_offlineCurrentTableName, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, OfflineRecordValues, StringComparer.Ordinal);
            var changes = new List<ChangeJournalRowChange>();
            var identityColumns = schema?.KeyColumns ?? new List<string>();
            Dictionary<string, object> Identity(Dictionary<string, object> values, string recordKey)
            {
                var identity = identityColumns.Where(k => values.TryGetValue(k, out var value) &&
                        value != null && value != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                    .ToDictionary(k => k, k => values[k], StringComparer.OrdinalIgnoreCase);
                if (identity.Count == 0) identity["__ProjectRecordKey"] = recordKey;
                return identity;
            }

            foreach (var pair in current)
            {
                if (!_offlineBaselineRows.TryGetValue(pair.Key, out var before))
                {
                    changes.Add(CreateJournalChange(_offlineCurrentTableName, ChangeJournalOperationKind.Insert,
                        null, pair.Value, null, Identity(pair.Value, pair.Key)));
                    continue;
                }
                var changedBefore = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var changedAfter = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in before.Keys.Union(pair.Value.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    before.TryGetValue(name, out var oldValue);
                    pair.Value.TryGetValue(name, out var newValue);
                    if (string.Equals(Convert.ToString(oldValue, CultureInfo.InvariantCulture) ?? "", Convert.ToString(newValue, CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal)) continue;
                    changedBefore[name] = oldValue ?? DBNull.Value;
                    changedAfter[name] = newValue ?? DBNull.Value;
                }
                if (changedBefore.Count > 0)
                    changes.Add(CreateJournalChange(_offlineCurrentTableName, ChangeJournalOperationKind.Update,
                        before, pair.Value, Identity(before, pair.Key), Identity(pair.Value, pair.Key)));
            }
            foreach (var pair in _offlineBaselineRows)
                if (!current.ContainsKey(pair.Key))
                    changes.Add(CreateJournalChange(_offlineCurrentTableName, ChangeJournalOperationKind.Delete,
                        pair.Value, null, Identity(pair.Value, pair.Key), null));

            _offlineBaselineRows.Clear();
            foreach (var pair in current) _offlineBaselineRows[pair.Key] = new Dictionary<string, object>(pair.Value, StringComparer.OrdinalIgnoreCase);
            if (changes.Count == 0) return;
            var kind = changes.Select(x => x.Kind).Distinct().Count() == 1 ? changes[0].Kind : ChangeJournalOperationKind.Composite;
            LogAction($"Offline: изменена таблица {_offlineCurrentTableName} · записей: {changes.Count}",
                category: "Изменение данных", source: _offlineCurrentTableName,
                details: $"Локальный проект: {_openedLocalProject.Manifest.Name}",
                journalChanges: changes, operationKind: kind, databaseName: _openedLocalProject.Manifest.Name);
        }

        private static bool IsNumericOfflineType(string type) => new[] { "int", "bigint", "smallint", "tinyint", "decimal", "numeric", "float", "real", "money", "smallmoney" }.Contains(type, StringComparer.OrdinalIgnoreCase);


        private void DuplicateOfflineRow()
        {
            if (!ProjectWriteGuard.IsDataTableWritable(_offlineCurrentTableName)) return;
            if (!UserAuthorizationService.Require(Permission.Mode1Add)) return;
            if (_offlineCurrentDataTable is null || _openedLocalProject is null || _rightPanel.DataTableGrid.SelectedItem is not DataRowView selected) return;
            var schema = _openedLocalProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(_offlineCurrentTableName, StringComparison.OrdinalIgnoreCase));
            var row = _offlineCurrentDataTable.NewRow();
            foreach (DataColumn column in _offlineCurrentDataTable.Columns)
            {
                var columnSchema = schema?.Columns.FirstOrDefault(x => x.Name.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (columnSchema?.IsIdentity == true || columnSchema?.IsComputed == true) { row[column] = DBNull.Value; continue; }
                row[column] = selected.Row[column] == DBNull.Value ? DBNull.Value : Convert.ToString(selected.Row[column], CultureInfo.InvariantCulture) ?? string.Empty;
            }
            var suggestRecord = GetOfflineScopedRecordSuggestion(_offlineCurrentDataTable);
            if (suggestRecord != null)
            {
                try
                {
                    var plc = row["PLC"] as string;
                    int record = suggestRecord(plc, CancellationToken.None);
                    row["Record"] = record.ToString(CultureInfo.InvariantCulture);
                }
                catch (InvalidOperationException ex)
                {
                    Configurator.Infrastructure.ModernDialog.Show(ex.Message);
                    return;
                }
            }
            _offlineCurrentDataTable.Rows.Add(row);
            _offlineRowRecordKeys[row] = "offline:" + Guid.NewGuid().ToString("N");
            CommitOfflineGridToProject();
            _rightPanel.DataTableGrid.SelectedItem = row;
            _rightPanel.DataTableGrid.ScrollIntoView(row);
            UpdateOfflineTableCount();
            SetOfflineEditorButtons();
            StatusText.Text = "Копия строки добавлена в локальный проект. SQL Server не изменялся.";
        }

        private void AddOfflineRow()
        {
            if (!ProjectWriteGuard.IsDataTableWritable(_offlineCurrentTableName)) return;
            if (!UserAuthorizationService.Require(Permission.Mode1Add)) return;
            if (_offlineCurrentDataTable is null || _offlineCurrentTableName is null || _openedLocalProject is null) return;
            var classNumber = ResolveSystemClassNumber(_offlineCurrentTableName);
            var classNumberColumn = classNumber.HasValue ? ResolveClassNumberColumn(_offlineCurrentColumns) : null;
            var recordColumn = _offlineCurrentColumns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
            var lookupOptions = OfflineLookupOptionsService.Build(_openedLocalProject, _offlineCurrentTableName, _offlineCurrentColumns);
            var suggestRecord = GetOfflineScopedRecordSuggestion(_offlineCurrentDataTable);
            var window = new AddRowWindow(_offlineCurrentTableName, _offlineCurrentColumns, 0, recordColumn, null, null,
                classNumber, classNumberColumn, lookupOptions,
                scopedRecordProvider: suggestRecord == null ? null : (plc, ct) => Task.FromResult(suggestRecord(plc, ct)),
                recordExistsProvider: (plc, record, ct) => Task.FromResult(
                    _offlineCurrentDataTable.Columns.Contains("PLC") && _offlineCurrentDataTable.Columns.Contains("Record") &&
                    _offlineCurrentDataTable.AsEnumerable().Any(row =>
                        OfflineKeyEquals(Convert.ToString(row["PLC"], CultureInfo.InvariantCulture), plc) &&
                        OfflineKeyEquals(Convert.ToString(row["Record"], CultureInfo.InvariantCulture), record))),
                preferFreeScopedRecord: suggestRecord != null) { Owner = this };
            window.ShowDialog();
            if (!window.Confirmed || window.Values is null || window.Values.Count == 0) return;
            var row = _offlineCurrentDataTable.NewRow();
            foreach (var pair in window.Values)
                if (_offlineCurrentDataTable.Columns.Contains(pair.Key)) row[pair.Key] = pair.Value is null ? DBNull.Value : Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            _offlineCurrentDataTable.Rows.Add(row);
            _offlineRowRecordKeys[row] = "offline:" + Guid.NewGuid().ToString("N");
            CommitOfflineGridToProject();
            if (_rightPanel.DataTableGrid.ItemsSource is DataView view)
                _rightPanel.DataTableGrid.SelectedItem = view.Cast<DataRowView>().FirstOrDefault(item => item.Row == row);
            UpdateOfflineTableCount();
            SetOfflineEditorButtons();
            StatusText.Text = "Строка добавлена в локальный проект. Изменения будут автоматически сохранены в открытый файл .cfgproj.";
        }

        private static bool OfflineKeyEquals(string? left, string? right)
        {
            if (decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture, out var first) &&
                decimal.TryParse(right, NumberStyles.Number, CultureInfo.InvariantCulture, out var second)) return first == second;
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private Func<string?, CancellationToken, int>? GetOfflineScopedRecordSuggestion(DataTable table)
        {
            if (!table.Columns.Contains("PLC") || !table.Columns.Contains("Record") ||
                !_offlineCurrentColumns.Any(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase) && !c.IsIdentity && !c.IsReadOnly)) return null;

            return (plc, ct) => SuggestOfflineScopedRecord(table, plc, ct);
        }

        internal static int SuggestOfflineScopedRecord(DataTable table, string? plc, CancellationToken ct = default)
        {
            var suggestion = new ScopedRecordSuggestion();
            ct.ThrowIfCancellationRequested();
            foreach (DataRow row in table.Rows)
            {
                ct.ThrowIfCancellationRequested();
                if (row.RowState != DataRowState.Deleted && OfflineKeyEquals(row["PLC"] as string, plc))
                    suggestion.Add(row["Record"]);
            }
            return suggestion.GetValue();
        }

        private void EditOfflineRow()
        {
            if (!ProjectWriteGuard.IsDataTableWritable(_offlineCurrentTableName)) return;
            if (!UserAuthorizationService.Require(Permission.Mode1Edit)) return;
            if (_offlineCurrentDataTable is null || _offlineCurrentTableName is null || _rightPanel.DataTableGrid.SelectedItem is not DataRowView selected) return;
            var oldValues = selected.Row.Table.Columns.Cast<DataColumn>().ToDictionary(c => c.ColumnName, c => selected[c.ColumnName] == DBNull.Value ? "" : Convert.ToString(selected[c.ColumnName], CultureInfo.InvariantCulture) ?? "", StringComparer.OrdinalIgnoreCase);
            var schema = _openedLocalProject?.TableSchemas.FirstOrDefault(x => x.TableName.Equals(_offlineCurrentTableName, StringComparison.OrdinalIgnoreCase));
            var keyColumn = schema?.KeyColumns.FirstOrDefault() ?? "";
            var lookupOptions = OfflineLookupOptionsService.Build(_openedLocalProject, _offlineCurrentTableName, _offlineCurrentColumns);
            var classNumber = ResolveSystemClassNumber(_offlineCurrentTableName);
            var classNumberColumn = classNumber.HasValue ? ResolveClassNumberColumn(_offlineCurrentColumns) : null;
            var suggestRecord = GetOfflineScopedRecordSuggestion(_offlineCurrentDataTable);
            var window = new EditRowWindow(_offlineCurrentTableName, _offlineCurrentColumns, oldValues, keyColumn,
                classNumberColumn, classNumber, lookupOptions,
                suggestRecord == null ? null : (plc, ct) => Task.FromResult(suggestRecord(plc, ct))) { Owner = this };
            window.ShowDialog();
            if (!window.Confirmed || window.Values is null) return;
            foreach (var pair in window.Values)
                if (_offlineCurrentDataTable.Columns.Contains(pair.Key)) selected.Row[pair.Key] = pair.Value is null ? DBNull.Value : Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            CommitOfflineGridToProject();
            _rightPanel.DataTableGrid.Items.Refresh();
            StatusText.Text = "Изменения применены к локальной копии. SQL Server не изменялся.";
            SetOfflineEditorButtons();
        }

        private async void DeleteOfflineRows()
        {
            if (!ProjectWriteGuard.IsDataTableWritable(_offlineCurrentTableName)) return;
            if (!UserAuthorizationService.Require(Permission.Mode1Delete)) return;
            if (_offlineCurrentDataTable is null || _openedLocalProject is null) return;
            var rows = _rightPanel.DataTableGrid.SelectedItems.OfType<DataRowView>().Select(x => x.Row).ToList();
            if (rows.Count == 0) return;
            if (Configurator.Infrastructure.ModernDialog.Show(this, rows.Count == 1 ? "Удалить выбранную строку из локальной копии?" : $"Удалить {rows.Count} строк из локальной копии?", "Offline · подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            try
            {
                CommitOfflineGridToProject();
                var source = _projectDataSource.Current as Configurator.Core.Projects.OfflineProjectDataSource
                    ?? throw new InvalidOperationException("Не удалось проверить связи локального проекта.");
                var schema = _openedLocalProject.TableSchemas.FirstOrDefault(x =>
                    x.TableName.Equals(_offlineCurrentTableName, StringComparison.OrdinalIgnoreCase));
                var targetRecords = rows.Select(row =>
                {
                    if (!_offlineRowRecordKeys.TryGetValue(row, out var key) ||
                        !_offlineCurrentRecordIndex.TryGetValue(key, out var record))
                        throw new InvalidOperationException("Не удалось определить запись локального проекта для проверки связей.");
                    return record;
                }).ToList();
                var targets = targetRecords.Select(record => record.Values.ToDictionary(pair => pair.Key,
                    pair => (object?)(pair.Value?.DeepClone()), StringComparer.OrdinalIgnoreCase)).ToList();
                var replacementOptions = OfflineObjectDeletionGuard.BuildOptions(source, _offlineCurrentTableName!, targets);
                IReadOnlyList<ReferenceReplacement> replacements = Array.Empty<ReferenceReplacement>();
                if (replacementOptions.Count > 0)
                {
                    var dialog = new DependencyReplacementWindow(replacementOptions, requireExistingCandidates: true) { Owner = this };
                    if (dialog.ShowDialog() != true) return;
                    replacements = dialog.Replacements;
                }

                var recordsToRemove = new HashSet<Configurator.Core.Projects.ProjectRecord>();
                var journalChanges = new List<ChangeJournalRowChange>(rows.Count);
                foreach (var row in rows)
                {
                    if (!_offlineRowRecordKeys.TryGetValue(row, out var key) ||
                        !_offlineCurrentRecordIndex.TryGetValue(key, out var record))
                        throw new InvalidOperationException("Не удалось определить запись локального проекта для удаления.");

                    var values = OfflineRecordValues(record);
                    var identity = schema?.KeyColumns.Where(column => values.TryGetValue(column, out var value) &&
                            value != null && value != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                        .ToDictionary(column => column, column => values[column], StringComparer.OrdinalIgnoreCase)
                        ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    if (identity.Count == 0) identity["__ProjectRecordKey"] = key;
                    journalChanges.Add(CreateJournalChange(_offlineCurrentTableName!, ChangeJournalOperationKind.Delete,
                        values, null, identity, null));
                    if (!recordsToRemove.Add(record))
                        throw new InvalidOperationException("Выбранные строки указывают на одну и ту же запись.");
                }

                var replacementGroups = replacements.GroupBy(BuildOfflineReplacementGroupKey, StringComparer.Ordinal).ToList();
                var replacementUpdates = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
                foreach (var group in replacementGroups)
                {
                    var first = group.First();
                    var groupKey = BuildOfflineReplacementGroupKey(first);
                    var sourceRecord = source.GetRecords(first.SourceTable).SingleOrDefault(record =>
                        first.KeyColumns.Select((column, index) => (column, value: first.KeyValues[index])).All(pair =>
                            record.Values.TryGetPropertyValue(pair.column, out var value) && OfflineObjectDeletionGuard.ValuesEqual(value, pair.value)));
                    if (sourceRecord == null) throw new InvalidOperationException("Зависимая запись для замены больше не найдена.");
                    var before = OfflineRecordValues(sourceRecord);
                    var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    var after = new Dictionary<string, object>(before, StringComparer.OrdinalIgnoreCase);
                    foreach (var replacement in group)
                    {
                        if (replacement.SourceColumns.Count != replacement.NewValues.Count)
                            throw new InvalidOperationException("Количество полей и значений в замене не совпадает.");
                        for (var i = 0; i < replacement.SourceColumns.Count; i++)
                            AssignOfflineReplacementValue(updates, replacement.SourceColumns[i], replacement.NewValues[i]);
                        foreach (var extra in replacement.AdditionalAssignments)
                            AssignOfflineReplacementValue(updates, extra.Key, extra.Value);
                    }
                    replacementUpdates[groupKey] = updates;
                    foreach (var update in updates) after[update.Key] = update.Value!;
                    var identityBefore = first.KeyColumns.Select((column, index) => (column, value: first.KeyValues[index]))
                        .ToDictionary(pair => pair.column, pair => pair.value, StringComparer.OrdinalIgnoreCase);
                    var identityAfter = new Dictionary<string, object>(identityBefore, StringComparer.OrdinalIgnoreCase);
                    foreach (var key in identityAfter.Keys.ToList())
                        if (after.TryGetValue(key, out var value)) identityAfter[key] = value;
                    journalChanges.Insert(0, CreateJournalChange(first.SourceTable, ChangeJournalOperationKind.Update,
                        before, after, identityBefore, identityAfter));
                }

                await source.ExecuteMutationTransactionAsync(async () =>
                {
                    foreach (var group in replacementGroups)
                    {
                        var first = group.First();
                        var updates = replacementUpdates[BuildOfflineReplacementGroupKey(first)];
                        foreach (var replacement in group)
                        {
                            if (replacement.KeyColumns.Count != replacement.KeyValues.Count ||
                                replacement.SourceColumns.Count != replacement.NewValues.Count)
                                throw new InvalidOperationException("Вариант замены содержит неполный ключ или несовпадающее число полей.");
                            if (replacement.ExpectedOldValues != null)
                            {
                                foreach (var expected in replacement.ExpectedOldValues)
                                {
                                    var current = source.GetRecords(replacement.SourceTable).SingleOrDefault(record =>
                                    {
                                        for (var keyIndex = 0; keyIndex < replacement.KeyColumns.Count; keyIndex++)
                                        {
                                            if (!record.Values.TryGetPropertyValue(replacement.KeyColumns[keyIndex], out var value) ||
                                                !OfflineObjectDeletionGuard.ValuesEqual(value, replacement.KeyValues[keyIndex])) return false;
                                        }
                                        return true;
                                    });
                                    if (current == null || !current.Values.TryGetPropertyValue(expected.Key, out var actual) ||
                                        !OfflineObjectDeletionGuard.ValuesEqual(actual, expected.Value))
                                        throw new InvalidOperationException("Зависимая запись изменилась после выбора замены. Удаление отменено.");
                                }
                            }
                        }
                        await source.UpdateRecordAsync(first.SourceTable, first.KeyColumns,
                            first.KeyColumns.Select((column, index) => (column, first.KeyValues[index]))
                                .ToDictionary(pair => pair.column, pair => (object?)pair.Item2, StringComparer.OrdinalIgnoreCase), updates);
                    }
                    _openedLocalProject.LocalRecords.RemoveAll(recordsToRemove.Contains);
                    source.InvalidateIndex();
                });
                _offlineGridDirty = false;
                _offlineProjectDirty = true;
                LogAction($"Offline: удалено строк: {rows.Count} · {_offlineCurrentTableName}",
                    category: "Удаление", source: _offlineCurrentTableName!,
                    details: $"Локальный проект: {_openedLocalProject.Manifest.Name}" + (replacements.Count > 0 ? $" · замен ссылок: {replacements.Count}" : ""),
                    journalChanges: journalChanges, operationKind: replacements.Count > 0 ? ChangeJournalOperationKind.Composite : ChangeJournalOperationKind.Delete,
                    databaseName: _openedLocalProject.Manifest.Name);
                OpenOfflineTable(_offlineCurrentTableName!);
                SetOfflineEditorButtons();
                StatusText.Text = "Строки удалены только из локального проекта. SQL Server не изменялся.";
            }
            catch (Exception ex)
            {
                Configurator.Infrastructure.ModernDialog.Show(this,
                    "Удаление остановлено: " + Configurator.Infrastructure.UserErrorMessage.Format(ex),
                    "Offline · удаление", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string BuildOfflineReplacementGroupKey(ReferenceReplacement replacement) =>
            JsonSerializer.Serialize(new
            {
                replacement.SourceTable,
                Identity = replacement.KeyColumns.Select((column, index) => new
                {
                    Column = column,
                    Value = replacement.KeyValues[index]
                }).ToArray()
            });

        private static void AssignOfflineReplacementValue(IDictionary<string, object?> values, string column, object? value)
        {
            if (values.TryGetValue(column, out var previous) && !OfflineObjectDeletionGuard.ValuesEqual(
                    previous is JsonNode node ? node : ProjectKeyCodec.ToNode(previous), value))
                throw new InvalidOperationException($"Для поля «{column}» выбраны разные замены. Удаление отменено.");
            values[column] = value;
        }

        private void SetOfflineEditorButtons()
        {
            var active = _isOfflineWorkspace && _offlineCurrentDataTable is not null && ProjectWriteGuard.IsDataTableWritable(_offlineCurrentTableName);
            _rightPanel.BtnAdd.IsEnabled = active && UserAuthorizationService.Has(Permission.Mode1Add);
            _rightPanel.BtnUpdate.IsEnabled = active && _rightPanel.DataTableGrid.SelectedItem is DataRowView && UserAuthorizationService.Has(Permission.Mode1Edit);
            _rightPanel.BtnDuplicate.IsEnabled = active && _rightPanel.DataTableGrid.SelectedItem is DataRowView && UserAuthorizationService.Has(Permission.Mode1Add);
            _rightPanel.BtnDelete.IsEnabled = active && _rightPanel.DataTableGrid.SelectedItems.Count > 0 && UserAuthorizationService.Has(Permission.Mode1Delete);
            _rightPanel.BtnFirst.IsEnabled = _rightPanel.BtnPrev.IsEnabled = _rightPanel.BtnNext.IsEnabled = _rightPanel.BtnLast.IsEnabled = false;
        }

        private async Task AutoSaveOfflineProjectAsync()
        {
            if (!_isOfflineWorkspace || _openedLocalProject is null || string.IsNullOrWhiteSpace(_openedLocalProjectPath)) return;
            if (!await _offlineSaveGate.WaitAsync(0)) return; // a manual save or project switch already owns the writer
            try
            {
                if (!_isOfflineWorkspace || _openedLocalProject is null || string.IsNullOrWhiteSpace(_openedLocalProjectPath)) return;
                var project = _openedLocalProject;
                var path = Path.GetFullPath(_openedLocalProjectPath);
                // Avoid serializing and replacing the complete package every two seconds
                // when the user is only browsing tables or changing modes.
                if (!_offlineProjectDirty && !_offlineGridDirty) return;
                if (!File.Exists(path))
                {
                    StatusText.Text = "Ошибка Offline: исходный файл удалён. Автосохранение остановлено.";
                    _offlineAutoSaveTimer.Stop();
                    return;
                }
                _offlineAutoSaveInProgress = true;
                CommitOfflineGridToProject();
                project.Manifest.ModifiedUtc = DateTimeOffset.UtcNow;
                var saveTimer = Stopwatch.StartNew();
                await _projectPackageService.SaveAsync(path, project, requireExisting: true, reservedFile: GetReservedProjectFile(path));
                _offlineProjectDirty = false;
                System.Diagnostics.Debug.WriteLine($"[OfflinePerf] autosave: records={project.LocalRecords.Count}, ms={saveTimer.ElapsedMilliseconds}");
                StatusText.Text = "Offline · автосохранено в открытый файл";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка автосохранения Offline: " + Configurator.Infrastructure.UserErrorMessage.Format(ex);
            }
            finally
            {
                _offlineAutoSaveInProgress = false;
                _offlineSaveGate.Release();
            }
        }

        private async void OfflineAutoSaveTimer_Tick(object? sender, EventArgs e)
            => await AutoSaveOfflineProjectAsync();

        private FileStream? GetReservedProjectFile(string path)
        {
            var canonical = Path.GetFullPath(path);
            return _offlineProjectLocks.TryGetValue(canonical, out var stream) ? stream : null;
        }

        private async Task SaveOfflineProjectAsync()
        {
            await _offlineSaveGate.WaitAsync();
            try
            {
                var project = _openedLocalProject ?? throw new InvalidOperationException("Offline-проект не выбран. Откройте существующий файл .cfgproj.");
                // A workspace switch is not a save command. Avoid full package serialization when browsing only.
                // Both flags are checked while holding the save gate to avoid racing the autosave writer.
                if (!Configurator.Core.Workspace.WorkspaceSwitchPolicy.ShouldPersistOfflineProject(_offlineProjectDirty, _offlineGridDirty))
                {
                    SessionTestLog.Trace("WORKSPACE-PERF", "switch-to-online-offline-save-skipped=true; reason=clean-project");
                    return;
                }
                var path = Path.GetFullPath(_openedLocalProjectPath ?? throw new InvalidOperationException("Не задан путь активного Offline-проекта."));
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        "Открытый Offline-файл был удалён или перемещён. Configurator не будет создавать его заново. Откройте существующий файл проекта повторно.", path);
                CommitOfflineGridToProject();
                await _projectPackageService.SaveAsync(path, project, requireExisting: true, reservedFile: GetReservedProjectFile(path));
                _offlineProjectDirty = false;
                _offlineGridDirty = false;
                StatusText.Text = $"Offline-проект сохранён локально · {DateTime.Now:T}";
            }
            finally { _offlineSaveGate.Release(); }
        }

        private async void SwitchToOnline_Click(object sender, RoutedEventArgs e)
        {
            await SwitchToOnlineWorkspaceAsync();
        }

        private async Task SwitchToOnlineWorkspaceAsync()
        {
            if (_currentMode == 4 && !FunctionalBlockEditorControl.ConfirmPendingEdits()) return;
            if (UserAuthorizationService.CurrentUser == null)
            {
                if (_loginDialogOpen) return;
                var login = new LoginWindow { Owner = this };
                _loginDialogOpen = true;
                bool authenticated;
                try { authenticated = login.ShowDialog() == true; }
                finally { _loginDialogOpen = false; }
                if (!authenticated)
                {
                    _offlineSelectionRequired = true;
                    UpdateWorkspaceModeUi();
                    StatusText.Text = "Для Online требуется авторизация. Можно открыть локальный проект.";
                    return;
                }
                ApplyAuthorizationUi();
            }

            var switchTimer = Stopwatch.StartNew();
            var saveTimer = Stopwatch.StartNew();
            // Switching the data source must never save, create, or replace an Offline package.
            // Offline persistence is explicit through SaveOfflineProjectAsync only.
            if (_isOfflineWorkspace && _openedLocalProject != null)
            {
                try { await SaveOfflineProjectAsync(); }
                catch (Exception ex)
                {
                    Configurator.Infrastructure.ModernDialog.Show(this,
                        "Не удалось сохранить Offline-проект. Переключение в Online отменено, чтобы не потерять изменения.\n\n" + ex.Message,
                        "Ошибка сохранения Offline", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            saveTimer.Stop();
            SessionTestLog.Trace("WORKSPACE-PERF", $"switch-to-online-offline-save-ms={saveTimer.ElapsedMilliseconds}");
            _offlineAutoSaveTimer.Stop();
            _workspaceModeChosen = true;
            _isOfflineWorkspace = false;
            _projectDataSource.Set(null);
            _offlineSelectionRequired = false;
            int modeToRestore = _currentMode is >= 1 and <= 4 &&
                (_currentMode != 4 || PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 4))
                ? _currentMode : 1;
            _currentMode = modeToRestore;
            UpdateWorkspaceModeUi();
            _fieldsBuilder.DisplayPrefix = "";
            EditorModeView.Visibility = modeToRestore == 1 ? Visibility.Visible : Visibility.Collapsed;
            Mode2View.Visibility = modeToRestore == 2 ? Visibility.Visible : Visibility.Collapsed;
            Mode3View.Visibility = modeToRestore == 3 ? Visibility.Visible : Visibility.Collapsed;
            Mode4View.Visibility = modeToRestore == 4 ? Visibility.Visible : Visibility.Collapsed;
            UpdateModeNavigation(modeToRestore);
            NavDatabase.IsEnabled = NavTemplates.IsEnabled = NavProject.IsEnabled = NavFbEditor.IsEnabled = !_offlineSelectionRequired;
            MenuDatabase.IsEnabled = !_offlineSelectionRequired && _currentMode == 1;
            CurrentProjectText.IsEnabled = true;
            CurrentProjectText.Content = string.IsNullOrWhiteSpace(_selectedDbName) ? "не выбран" : _selectedDbName;
            _offlineCurrentDataTable = null;
            _offlineCurrentTableName = null;
            _offlineCurrentColumns = new List<ColumnInfo>();
            _offlineRowRecordKeys.Clear();
            if (_rightPanel?.DataTableGrid != null) _rightPanel.DataTableGrid.ItemsSource = null;
            if (_rightPanel?.TableTitle != null) _rightPanel.TableTitle.Text = string.Empty;
            _activeVm = null;
            ClearActiveTable();
            var reconnectTimer = Stopwatch.StartNew();
            await TryReconnectDatabasesAsync();
            reconnectTimer.Stop();
            SessionTestLog.Trace("WORKSPACE-PERF", $"switch-to-online-reconnect-ms={reconnectTimer.ElapsedMilliseconds}; connectedDatabases={_connection.ConnectedDatabases.Count}");
            var treeTimer = Stopwatch.StartNew();
            RebuildTree();
            treeTimer.Stop();
            SessionTestLog.Trace("WORKSPACE-PERF", $"switch-to-online-tree-rebuild-ms={treeTimer.ElapsedMilliseconds}");
            UpdateAddDbButtonState();
            var activeDatabases = GetActiveDatabases();
            string activeProject = activeDatabases.Contains(_selectedDbName, StringComparer.OrdinalIgnoreCase) ? _selectedDbName : "";
            _selectedDbName = activeProject;
            UpdateCurrentProjectText();
            ConfigureActiveProjectEditor(modeToRestore, activeProject);
            StatusText.Text = "Online-режим активен. Offline-изменения остались в локальном .cfgproj.";
            switchTimer.Stop();
            SessionTestLog.Trace("WORKSPACE-PERF", $"switch-to-online-total-ms={switchTimer.ElapsedMilliseconds}; mode={modeToRestore}; selectedDatabase='{activeProject}'");
        }
    }
