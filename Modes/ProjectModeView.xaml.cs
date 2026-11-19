#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Configurator.Application.Architecture;
using Configurator.Core.Architecture;
using Configurator.Infrastructure.Database;
using Configurator.Core.Projects;

namespace Configurator;

public partial class ProjectModeView : UserControl
{
    private ClassArchitectureService _architecture = new();
    private ConnectionSettings _connection;
    private string _selectedDatabase = "";
    private string CurrentProjectIdentity => _connection == null ? "" : ProjectArchitectureResolver.GetProjectIdentity(_connection.Server, _selectedDatabase);
    private ClassDefinition _selectedClass;
    private DataTable _objects = new();
    private DataRow _selectedRow;
    private CancellationTokenSource _loadCts;
    private int _loadGeneration;
    private int _objectDetailsGeneration;
    private ClassRegistryService _classRegistry;
    private bool _filterSelectionGuard;
    private bool _suppressClassSelectionChanged;
    private bool _suppressConfigurationListSelectionChanged;
    private ConfigurationItem _selectedConfigurationItem;
    private DataTable _configurationRows = new();
    private string _configurationTableName = "";
    private string _configurationKeyColumn = "";
    private string _configurationDisplayColumn = "";
    private ConfigurationKind _configurationKind = ConfigurationKind.None;
    private ConfiguratorProject _offlineProject;
    private OfflineProjectDataSource _offlineSource;
    private IProjectCrudDataSource _crudSource;
    private ProjectCrudSession _crudSession;
    private ClassArchitectureReader _offlineReader;
    private OnlineProjectDataSource _onlineSource;
    private IProjectWorkspace _workspace;
    private LookupOptionsService _onlineLookupOptionsService;
    private string _onlineLookupDatabaseKey = string.Empty;
    private int _workspaceGeneration;

    public event EventHandler OfflineProjectChanged;
    public event Action<ChangeJournalEntry> ChangeJournalRequested;
    public IProjectWorkspace ActiveWorkspace => _workspace;

    public ProjectModeView()
    {
        InitializeComponent();
        InitializeProgramTools();
        ApplyAuthorizationUi();
        UserAuthorizationService.Changed += (_, __) => Dispatcher.Invoke(ApplyAuthorizationUi);
        ObjectList.ContextMenu = BuildObjectContextMenu();
        ObjectList.PreviewMouseRightButtonDown += ObjectList_PreviewMouseRightButtonDown;
        ObjectList.MouseDoubleClick += ObjectList_MouseDoubleClick;
        ObjectList.PreviewKeyDown += ObjectList_PreviewKeyDown;
        BtnDelete.IsEnabled = false;
        BtnDuplicate.IsEnabled = false;
        BtnSave.IsEnabled = false;
        InitializeEmptyFilterLists();
        RefreshClasses();
    }

    public void ConfigureOffline(ConfiguratorProject project)
    {
        ResetProgramTools();
        _workspaceGeneration++;
        CancelLoad();
        DisposeWorkspace();
        SetCrudSource(null);
        ClearProjectViewForSourceChange();
        ProjectUnavailableBanner.Visibility = Visibility.Collapsed;
        _offlineProject = project ?? throw new ArgumentNullException(nameof(project));
        _onlineSource = null;
        Configurator.Core.Architecture.LookupGenerationSettingsRuntime.Current = Configurator.Infrastructure.Projects.LookupGenerationSettingsStore.Load(project);
        Configurator.Core.Projects.SqlProviderGuard.SetOfflineActive(true);
        _onlineLookupOptionsService = null;
        _onlineLookupDatabaseKey = string.Empty;
        _offlineSource = new OfflineProjectDataSource(project);
        _offlineSource.ProjectChanged += (_, _) => OfflineProjectChanged?.Invoke(this, EventArgs.Empty);
        SetCrudSource(_offlineSource);
        _workspace = new ProjectWorkspace(_offlineSource, _crudSource, new Configurator.Infrastructure.Projects.ProjectArchitectureDataSource(project));
        _offlineReader = new ClassArchitectureReader(_offlineSource);
        _connection = null;
        _selectedDatabase = project.Manifest.Name;
        _architecture = new ClassArchitectureService(new Configurator.Infrastructure.Architecture.TemplateLibraryStore(project));
        EnsureOfflineClassesMapToSnapshotTables(project);
        _configurationKind = ConfigurationKind.Class;
        TxtProjectDatabase.Text = _selectedDatabase;
        _ = ConfigureProjectAsync();
    }

    public async Task RefreshAfterOfflineJournalRestoreAsync()
    {
        if (_offlineProject == null) return;
        _offlineSource?.InvalidateIndex();
        if (_selectedClass != null) await LoadObjectsAsync();
        if (_configurationKind != ConfigurationKind.Class && _configurationKind != ConfigurationKind.None)
            await LoadConfigurationValuesAsync(_configurationKind);
        RebuildObjectFilters();
    }

    public void ClearWorkspace()
    {
        ResetProgramTools();
        _workspaceGeneration++;
        CancelLoad();
        DisposeWorkspace();
        SetCrudSource(null);
        _offlineProject = null;
        _offlineSource = null;
        _offlineReader = null;
        _onlineSource = null;
        _selectedClass = null;
        _selectedRow = null;
        _selectedConfigurationItem = null;
        _configurationKind = ConfigurationKind.None;
        _configurationTableName = string.Empty;
        _configurationRows = new DataTable();
        _objects = new DataTable();
        ClassList.SelectedItem = null;
        ClassList.ItemsSource = Array.Empty<ClassDefinition>();
        ConfigurationList.SelectedItem = null;
        ConfigurationList.ItemsSource = Array.Empty<ConfigurationItem>();
        ObjectList.SelectedItem = null;
        ObjectList.ItemsSource = Array.Empty<ObjectItem>();
        TxtClassTitle.Text = string.Empty;
        TxtObjectCount.Text = string.Empty;
        TxtStatus.Text = "Проект не выбран";
        ClearObjectEditor();
        BtnNewObject.IsEnabled = BtnNewObjectsBatch.IsEnabled = BtnConfigDelete.IsEnabled = false;
        BtnDelete.IsEnabled = BtnDuplicate.IsEnabled = BtnSave.IsEnabled = false;
    }

    private void ClearProjectViewForSourceChange()
    {
        ResetProgramTools();
        _selectedClass = null;
        _selectedRow = null;
        _selectedConfigurationItem = null;
        ClassList.ItemsSource = Array.Empty<ClassDefinition>();
        ConfigurationList.ItemsSource = Array.Empty<ConfigurationItem>();
        ObjectList.ItemsSource = Array.Empty<ObjectItem>();
        TxtObjectCount.Text = string.Empty;
        ClearObjectEditor();
        BtnNewObject.IsEnabled = BtnNewObjectsBatch.IsEnabled = BtnConfigDelete.IsEnabled = false;
        BtnDelete.IsEnabled = BtnDuplicate.IsEnabled = BtnSave.IsEnabled = false;
    }

    private void EnsureOfflineClassesMapToSnapshotTables(ConfiguratorProject project)
    {
        var schemas = project.TableSchemas.Where(x => !string.IsNullOrWhiteSpace(x.TableName)).ToList();
        var classes = _architecture.Classes.ToList();
        var changed = false;
        // Keep the Online resolver's class-to-table rules, but resolve exclusively against
        // the schemas captured in this .cfgproj. System class names are labels, not table names
        // (Valve -> dbo.Valves, DSV -> dbo.Valves2S, Program -> dbo.Prog, etc.).
        var systemTableCandidates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Valve"] = new[] { "dbo.Valves", "dbo.Valve" },
            ["DSV"] = new[] { "dbo.Valve2S", "dbo.Valves2S", "dbo.Valve2s", "dbo.Valves2S" },
            ["Motor"] = new[] { "dbo.Motors", "dbo.Motor" },
            ["DI"] = new[] { "dbo.DiscretIN", "dbo.DiscretIn" },
            ["DO"] = new[] { "dbo.DiscretOUT", "dbo.DiscretOut" },
            ["AI"] = new[] { "dbo.AnaIN", "dbo.AnaIn", "dbo.AnalogIn" },
            ["AO"] = new[] { "dbo.AnaOUT", "dbo.AnaOut", "dbo.AnalogOut" },
            ["PID"] = new[] { "dbo.Reglers", "dbo.Regler" },
            ["Status"] = new[] { "dbo.Statuses", "dbo.Status" },
            ["Program"] = new[] { "dbo.Prog", "dbo.Programs", "dbo.Program" },
            ["Step"] = new[] { "dbo.Prog_Step", "dbo.ProgramStep" },
            ["Matrix"] = new[] { "dbo.Matrix", "dbo.Matrices", "dbo.Matrix_Devices" }
        };

        string ResolveSystemTable(ClassDefinition cls)
        {
            if (!systemTableCandidates.TryGetValue(cls.Name, out var candidates)) return null;
            foreach (var candidate in candidates)
            {
                var match = schemas.FirstOrDefault(t => t.TableName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.TableName;
            }
            return null;
        }

        // Older class-library entries sometimes store an unqualified table name (Valve),
        // while the snapshot uses dbo.Valve. Resolve the mapping against the local schema.
        foreach (var cls in classes)
        {
            var mapped = cls.PrimaryStorage?.TableName;
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                var exact = schemas.FirstOrDefault(t => t.TableName.Equals(mapped, StringComparison.OrdinalIgnoreCase));
                var leaf = mapped.Split('.').Last().Trim('[', ']');
                var matches = schemas.Where(t => t.TableName.Split('.').Last().Trim('[', ']').Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
                var resolved = exact?.TableName ?? (matches.Count == 1 ? matches[0].TableName : null);
                if (resolved != null && !resolved.Equals(mapped, StringComparison.OrdinalIgnoreCase))
                {
                    var primary = cls.StorageMappings.FirstOrDefault(x => string.Equals(x?.Role, "primary", StringComparison.OrdinalIgnoreCase));
                    if (primary != null) { primary.TableName = resolved; changed = true; }
                }
                else if (resolved == null)
                {
                    // A stale class-library mapping must not hide a real table in the snapshot.
                    // Rebind by class name when there is exactly one matching local table.
                    var byClassName = schemas.Where(t => t.TableName.Split('.').Last().Trim('[', ']')
                        .Equals(cls.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (byClassName.Count == 1)
                    {
                        var primary = cls.StorageMappings.FirstOrDefault(x => string.Equals(x?.Role, "primary", StringComparison.OrdinalIgnoreCase));
                        if (primary != null)
                        {
                            primary.TableName = byClassName[0].TableName;
                            primary.KeyColumns = byClassName[0].KeyColumns.ToList();
                            changed = true;
                        }
                    }
                }
                continue;
            }

            var systemTable = ResolveSystemTable(cls);
            var classMatches = schemas.Where(t => t.TableName.Split('.').Last().Trim('[', ']').Equals(cls.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var resolvedClassTable = systemTable ?? (classMatches.Count == 1 ? classMatches[0].TableName : null);
            if (!string.IsNullOrWhiteSpace(resolvedClassTable))
            {
                var schema = schemas.FirstOrDefault(t => t.TableName.Equals(resolvedClassTable, StringComparison.OrdinalIgnoreCase));
                cls.StorageMappings ??= new List<StorageMapping>();
                var primary = cls.StorageMappings.FirstOrDefault(x => string.Equals(x?.Role, "primary", StringComparison.OrdinalIgnoreCase));
                if (primary == null)
                {
                    primary = new StorageMapping { Role = "primary" };
                    cls.StorageMappings.Add(primary);
                }
                if (!primary.TableName.Equals(resolvedClassTable, StringComparison.OrdinalIgnoreCase) ||
                    (schema != null && (primary.KeyColumns == null || !primary.KeyColumns.SequenceEqual(schema.KeyColumns, StringComparer.OrdinalIgnoreCase))))
                {
                    primary.TableName = resolvedClassTable;
                    if (schema != null) primary.KeyColumns = schema.KeyColumns.ToList();
                    changed = true;
                }
            }
        }

        // Any captured table not represented by a class remains editable in Offline Mode 3.
        // This is a local fallback; it does not infer or write anything to SQL Server.
        var mappedTables = new HashSet<string>(classes.Select(c => c.PrimaryStorage?.TableName).OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        foreach (var schema in schemas)
        {
            if (mappedTables.Contains(schema.TableName)) continue;
            var leaf = schema.TableName.Split('.').Last().Trim('[', ']');
            if (classes.Any(c => c.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.PrimaryStorage?.TableName, schema.TableName, StringComparison.OrdinalIgnoreCase))) continue;
            classes.Add(new ClassDefinition
            {
                Id = "offline-table-" + schema.TableName,
                Name = leaf,
                Category = "Таблица БД",
                Available = true,
                Description = "Локальное представление таблицы снимка",
                StorageMappings = new List<StorageMapping> { new StorageMapping { Role = "primary", TableName = schema.TableName, KeyColumns = schema.KeyColumns.ToList() } },
                Fields = schema.Columns.Select(c => new FieldDefinition { Name = c.Name, DataType = c.DataType, Size = Math.Max(0, c.MaxLength), Nullable = c.IsNullable, Description = c.IsIdentity ? "IDENTITY" : c.IsComputed ? "COMPUTED" : "" }).ToList()
            });
            mappedTables.Add(schema.TableName);
            changed = true;
        }
        if (changed) _architecture.Save();
    }

    private void DisposeWorkspace()
    {
        (_workspace as IDisposable)?.Dispose();
        _workspace = null;
    }

    private IProjectWorkspace RequireWorkspace() => _workspace ??
        throw new InvalidOperationException("Рабочее пространство не активировано. Выберите Online-БД или откройте локальный проект.");

    private IProjectCrudDataSource CreateOnlineCrudSource()
    {
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return null;
        return new OnlineProjectCrudDataSource(_connection.ToConnectionString(_selectedDatabase));
    }

    private void SetCrudSource(IProjectCrudDataSource source)
    {
        _crudSession?.Dispose();
        _crudSession = source == null ? null : new ProjectCrudSession(source);
        _crudSource = _crudSession;
    }

    private IProjectCrudDataSource RequireCrudSource() => _crudSource ??
        throw new InvalidOperationException("Источник сохранения не привязан к активному проекту. Выберите Online-БД или откройте локальный проект.");

    public void Configure(ConnectionSettings connection, IEnumerable<string> databases, string selectedDatabase)
    {
        _workspaceGeneration++;
        CancelLoad();
        DisposeWorkspace();
        SetCrudSource(null);
        ClearProjectViewForSourceChange();
        _offlineProject = null; _offlineSource = null; _offlineReader = null; _onlineSource = null;
        Configurator.Core.Projects.SqlProviderGuard.SetOfflineActive(false);
        _onlineLookupOptionsService = null;
        _onlineLookupDatabaseKey = string.Empty;
        _architecture = new ClassArchitectureService();
        _connection = connection;
        _selectedDatabase = selectedDatabase ?? string.Empty;
        SetCrudSource(CreateOnlineCrudSource());
        if (_connection != null && !string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            _onlineSource = new OnlineProjectDataSource(_connection.ToConnectionString(_selectedDatabase), _selectedDatabase);
            _workspace = new ProjectWorkspace(_onlineSource, _crudSource, new Configurator.Infrastructure.Projects.ProjectArchitectureDataSource(_connection.Server, _selectedDatabase));
        }
        TxtProjectDatabase.Text = string.IsNullOrWhiteSpace(_selectedDatabase) ? "не выбран" : _selectedDatabase;
        ProjectUnavailableBanner.Visibility = Visibility.Visible;
        ProjectUnavailableMessage.Text = string.IsNullOrWhiteSpace(_selectedDatabase)
            ? "Online-проект ещё не выбран. Откройте или выберите подключённую базу данных."
            : $"Проверяем доступность базы «{_selectedDatabase}»…";
        SessionTestLog.Trace("РЕЖИМ3-КОНФИГУРАЦИЯ", $"Configure: database='{_selectedDatabase}', connectionPresent={_connection != null}, online=true, classesBefore={_architecture.Classes.Count}");
        _ = ConfigureProjectAsync();
    }

    public void SetProjectDatabase(string database)
    {
        CancelLoad();
        DisposeWorkspace();
        SetCrudSource(null);
        ClearProjectViewForSourceChange();
        _offlineProject = null; _offlineSource = null; _offlineReader = null; _onlineSource = null;
        Configurator.Core.Projects.SqlProviderGuard.SetOfflineActive(false);
        _onlineLookupOptionsService = null;
        _onlineLookupDatabaseKey = string.Empty;
        _selectedDatabase = database ?? string.Empty;
        SetCrudSource(CreateOnlineCrudSource());
        if (_connection != null && !string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            _onlineSource = new OnlineProjectDataSource(_connection.ToConnectionString(_selectedDatabase), _selectedDatabase);
            _workspace = new ProjectWorkspace(_onlineSource, _crudSource, new Configurator.Infrastructure.Projects.ProjectArchitectureDataSource(_connection.Server, _selectedDatabase));
        }
        TxtProjectDatabase.Text = string.IsNullOrWhiteSpace(_selectedDatabase) ? "не выбран" : _selectedDatabase;
        ProjectUnavailableBanner.Visibility = Visibility.Visible;
        ProjectUnavailableMessage.Text = string.IsNullOrWhiteSpace(_selectedDatabase)
            ? "Online-проект ещё не выбран. Откройте или выберите подключённую базу данных."
            : $"Проверяем доступность базы «{_selectedDatabase}»…";
        SessionTestLog.Trace("РЕЖИМ3-КОНФИГУРАЦИЯ", $"SetProjectDatabase: database='{_selectedDatabase}', connectionPresent={_connection != null}");
        _ = ConfigureProjectAsync();
    }

    private void SelectProject_Click(object sender, RoutedEventArgs e)
        => ProjectSelectionRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler ProjectSelectionRequested;

    private async Task ConfigureProjectAsync()
    {
        var generation = _loadGeneration;
        var configureTimer = Stopwatch.StartNew();
        SessionTestLog.Trace("РЕЖИМ3-ЗАГРУЗКА", $"ConfigureProjectAsync start; offline={_offlineProject != null}, connectionPresent={_connection != null}, database='{_selectedDatabase}', class='{_selectedClass?.Name ?? "<null>"}', classCount={_architecture.Classes.Count}");
        if (_offlineProject != null)
        {
            ProjectUnavailableBanner.Visibility = Visibility.Collapsed;
            try
            {
                RefreshClasses();
                await RefreshProjectAvailabilityAsync();
                if (generation != _loadGeneration) return;
                if (ClassList.SelectedItem is ClassDefinition selected)
                {
                    _selectedClass = selected;
                    var fieldsTimer = Stopwatch.StartNew();
                    await LoadActualClassFieldsAsync();
                    if (generation != _loadGeneration) return;
                    fieldsTimer.Stop();
                    SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"offline fields-load-ms={fieldsTimer.ElapsedMilliseconds}");
                    var objectsTimer = Stopwatch.StartNew();
                    await LoadObjectsAsync();
                    if (generation != _loadGeneration) return;
                    objectsTimer.Stop();
                    SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"offline objects-load-ms={objectsTimer.ElapsedMilliseconds}; sqlUsed=false");
                }
                if (ClassList.SelectedItem is not ClassDefinition)
                {
                    _selectedClass = null;
                    _objects = new DataTable();
                    ObjectList.SelectedItem = null;
                    ObjectList.ItemsSource = Array.Empty<ObjectItem>();
                    ClearObjectEditor();
                    TxtObjectCount.Text = string.Empty;
                    TxtClassTitle.Text = "Выберите конфигурацию";
                }
                await PopulateGlobalFilterListsAsync();
                RebuildObjectFilters();
                TxtStatus.Text = "Offline · классы и объекты читаются из локального проекта; SQL отключён";
                configureTimer.Stop();
                SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"offline configure-total-ms={configureTimer.ElapsedMilliseconds}; sqlUsed=false; classes={_architecture.Classes.Count}");
            }
            catch (Exception ex)
            {
                if (generation != _loadGeneration) return;
                SessionTestLog.Trace("ОШИБКА-РЕЖИМ3", $"Offline ConfigureProjectAsync: {ex}");
                TxtStatus.Text = "Ошибка локального проекта: " + Configurator.Infrastructure.UserErrorMessage.Format(ex);
                configureTimer.Stop();
                SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"offline configure-total-ms={configureTimer.ElapsedMilliseconds}; result=error; sqlUsed=false");
            }
            return;
        }
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            ProjectUnavailableBanner.Visibility = Visibility.Visible;
            ProjectUnavailableMessage.Text = "Online-проект ещё не выбран. Откройте или выберите подключённую базу данных.";
            _selectedClass = null;
            _selectedRow = null;
            _selectedConfigurationItem = null;
            ClassList.ItemsSource = Array.Empty<ClassDefinition>();
            ConfigurationList.ItemsSource = Array.Empty<ConfigurationItem>();
            ObjectList.ItemsSource = Array.Empty<ObjectItem>();
            ClearObjectEditor();
            TxtClassTitle.Text = "Проект не выбран";
            TxtObjectCount.Text = "";
            TxtStatus.Text = "Нет доступного проекта. Восстановите связь с БД и выберите проект.";
            SessionTestLog.Trace("РЕЖИМ3-ЗАГРУЗКА", $"EARLY RETURN: no connection/database; connectionPresent={_connection != null}, database='{_selectedDatabase}'");
            configureTimer.Stop();
            SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"online configure-total-ms={configureTimer.ElapsedMilliseconds}; result=no-project");
            BtnNewObject.IsEnabled = BtnNewObjectsBatch.IsEnabled = BtnConfigDelete.IsEnabled = false;
            return;
        }
        SessionTestLog.Trace("БД-ПОДКЛЮЧЕНИЕ", $"Testing database connection for '{_selectedDatabase}'");
        var connectionTimer = Stopwatch.StartNew();
        bool connectionAvailable = await _connection.TestConnectionAsync(_selectedDatabase);
        if (generation != _loadGeneration) return;
        if (!connectionAvailable)
        {
            ProjectUnavailableBanner.Visibility = Visibility.Visible;
            ProjectUnavailableMessage.Text = $"База «{_selectedDatabase}» выбрана, но подключение недоступно. Проверьте соединение и повторите выбор.";
            SessionTestLog.Trace("БД-ПОДКЛЮЧЕНИЕ", $"FAILED: database '{_selectedDatabase}' unavailable");
            _selectedClass = null;
            _selectedRow = null;
            ClassList.ItemsSource = Array.Empty<ClassDefinition>();
            ConfigurationList.ItemsSource = Array.Empty<ConfigurationItem>();
            ObjectList.ItemsSource = Array.Empty<ObjectItem>();
            ClearObjectEditor();
            TxtClassTitle.Text = "БД недоступна";
            TxtStatus.Text = "Связь с БД потеряна. Все операции с проектом заблокированы.";
            connectionTimer.Stop();
            SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"online connection-test-ms={connectionTimer.ElapsedMilliseconds}; result=unavailable");
            configureTimer.Stop();
            SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"online configure-total-ms={configureTimer.ElapsedMilliseconds}; result=unavailable");
            BtnNewObject.IsEnabled = BtnNewObjectsBatch.IsEnabled = BtnConfigDelete.IsEnabled = false;
            return;
        }
        connectionTimer.Stop();
        SessionTestLog.Trace("БД-ПОДКЛЮЧЕНИЕ", $"OK: database '{_selectedDatabase}' connection test passed");
        SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"online connection-test-ms={connectionTimer.ElapsedMilliseconds}; result=available");
        ProjectUnavailableBanner.Visibility = Visibility.Collapsed;
        var availabilityTimer = Stopwatch.StartNew();
        await RefreshProjectAvailabilityAsync();
        if (generation != _loadGeneration) return;
        availabilityTimer.Stop();
        SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"online project-availability-ms={availabilityTimer.ElapsedMilliseconds}");
        SessionTestLog.Trace("РЕЖИМ3-ЗАГРУЗКА", $"After RefreshProjectAvailabilityAsync: classes={_architecture.Classes.Count}, selectedClass='{_selectedClass?.Name ?? "<null>"}', selectedTable='{_selectedClass?.PrimaryStorage?.TableName ?? "<null>"}'");
        var filtersTimer = Stopwatch.StartNew();
        await PopulateGlobalFilterListsAsync();
        if (generation != _loadGeneration) return;
        filtersTimer.Stop();
        SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"online global-filters-ms={filtersTimer.ElapsedMilliseconds}");
        configureTimer.Stop();
        SessionTestLog.Trace("РЕЖИМ3-ПРОИЗВОДИТЕЛЬНОСТЬ", $"online configure-total-ms={configureTimer.ElapsedMilliseconds}; classes={_architecture.Classes.Count}");
        SessionTestLog.Trace("РЕЖИМ3-ЗАГРУЗКА", $"ConfigureProjectAsync complete; selectedClass='{_selectedClass?.Name ?? "<null>"}', selectedTable='{_selectedClass?.PrimaryStorage?.TableName ?? "<null>"}', status='{TxtStatus.Text}'");
    }

    private void RefreshClasses()
    {
        var classes = _architecture.Classes
            .Where(x => x.Available)
            .OrderBy(x => x.ClassNumber > 0 ? x.ClassNumber : int.MaxValue)
            .ThenBy(x => x.Name)
            .ToList();
        // Replacing ItemsSource can raise SelectionChanged even though this is a
        // programmatic refresh. Suppress that event so a refresh cannot trigger a
        // second fields/objects load for the previously selected class.
        _suppressClassSelectionChanged = true;
        try
        {
            ClassList.ItemsSource = classes;
        }
        finally
        {
            _suppressClassSelectionChanged = false;
        }
        BuildConfigurationList(classes);
    }

    private void BuildConfigurationList(IReadOnlyList<ClassDefinition> classes)
    {
        if (ConfigurationList == null) return;
        var items = new List<ConfigurationItem>
        {
            new(ConfigurationKind.Plc, "PLC", "PLC", true, false, null),
            new(ConfigurationKind.Area, "Area", "Area", true, false, null)
        };
        items.AddRange(classes.Select(cls => new ConfigurationItem(
            ConfigurationKind.Class, cls.Name, cls.Name, false, false, cls)));
        ConfigurationList.ItemsSource = items;
    }

    private async void ConfigurationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConfigurationListSelectionChanged) return;
        var previousItem = e.RemovedItems.OfType<ConfigurationItem>().FirstOrDefault();
        if (ConfigurationList.SelectedItem is not ConfigurationItem item || item.IsSeparator)
        {
            if (ConfigurationList.SelectedItem == null && e.RemovedItems.Count > 0)
            {
                if (previousItem?.Class != null)
                {
                    bool canLeave = previousItem.Class.Name.Equals("Program", StringComparison.OrdinalIgnoreCase)
                        ? await ProgramSupportEditor.PrepareForContextChangeAsync()
                        : previousItem.Class.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase)
                            ? await MatrixCatalogEditor.PrepareForContextChangeAsync()
                            : true;
                    if (!canLeave)
                    {
                        _suppressConfigurationListSelectionChanged = true;
                        ConfigurationList.SelectedItem = previousItem;
                        _suppressConfigurationListSelectionChanged = false;
                        return;
                    }
                }
                ClassList.SelectedItem = null;
            }
            return;
        }
        if (previousItem?.Class != null && !SameSpecialConfiguration(previousItem.Class, item.Class))
        {
            bool canLeave = previousItem.Class.Name.Equals("Program", StringComparison.OrdinalIgnoreCase)
                ? await ProgramSupportEditor.PrepareForContextChangeAsync()
                : previousItem.Class.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase)
                    ? await MatrixCatalogEditor.PrepareForContextChangeAsync()
                    : true;
            if (!canLeave)
            {
                _suppressConfigurationListSelectionChanged = true;
                ConfigurationList.SelectedItem = previousItem;
                _suppressConfigurationListSelectionChanged = false;
                return;
            }
        }
        _selectedConfigurationItem = item;
        if (item.Kind == ConfigurationKind.Class && item.Class != null)
        {
            ClassList.SelectedItem = item.Class;
            return;
        }
        _selectedClass = null;
        if (ClassList.SelectedItem != null) ClassList.SelectedItem = null;
        await LoadConfigurationValuesAsync(item.Kind);
    }

    private static bool SameSpecialConfiguration(ClassDefinition current, ClassDefinition next)
        => current?.Name != null && next?.Name != null &&
           current.Name.Equals(next.Name, StringComparison.OrdinalIgnoreCase) &&
           (current.Name.Equals("Program", StringComparison.OrdinalIgnoreCase) ||
            current.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase));

    private async Task LoadConfigurationValuesAsync(ConfigurationKind kind, string preferredConfigurationKey = null)
    {
        string selectedConfigurationKey = !string.IsNullOrWhiteSpace(preferredConfigurationKey)
            ? preferredConfigurationKey
            : _configurationKind == kind ? GetConfigurationKeyValue() : string.Empty;
        if (_offlineProject != null)
        {
            _configurationKind = kind;
            UpdateObjectFilterIndicator();
            var candidates = kind == ConfigurationKind.Plc ? new[] { "dbo.PLC", "dbo.PLCs", "PLC", "PLCs" } : kind == ConfigurationKind.Area ? new[] { "dbo.Area", "dbo.Areas", "Area", "Areas" } : Array.Empty<string>();
            var localTable = _offlineProject.TableSchemas.FirstOrDefault(x => candidates.Any(t => x.TableName.Equals(t, StringComparison.OrdinalIgnoreCase)))?.TableName
                ?? _offlineProject.LocalRecords.Select(x => x.TableName).FirstOrDefault(t => candidates.Any(c => t.Equals(c, StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrWhiteSpace(localTable)) { _objects = _configurationRows = new DataTable(); ObjectList.ItemsSource = Array.Empty<ObjectItem>(); BtnNewObject.IsEnabled = false; BtnConfigDelete.IsEnabled = false; TxtStatus.Text = $"В снимке нет справочника {kind}. Пересоздай снапшот из Online."; return; }
            _configurationTableName = localTable;
            var schema = _offlineProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(localTable, StringComparison.OrdinalIgnoreCase));
            _configurationKeyColumn = kind == ConfigurationKind.Area
                ? schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("Record", StringComparison.OrdinalIgnoreCase)) ?? schema?.KeyColumns.FirstOrDefault() ?? "Record"
                : schema?.KeyColumns.FirstOrDefault() ?? "Record";
            _configurationDisplayColumn = kind == ConfigurationKind.Area
                ? schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaNameL2", StringComparison.OrdinalIgnoreCase))
                    ?? schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaNameL1", StringComparison.OrdinalIgnoreCase))
                    ?? schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaName", StringComparison.OrdinalIgnoreCase))
                    ?? _configurationKeyColumn
                : schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("NameL1", StringComparison.OrdinalIgnoreCase) || n.Equals("Name", StringComparison.OrdinalIgnoreCase) || n.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase)) ?? _configurationKeyColumn;
            _configurationRows = await _offlineReader.ReadRowsAsync(localTable, 5000);
            _objects = _configurationRows;
            var items = new List<ObjectItem>();
            for (int i = 0; i < _configurationRows.Rows.Count; i++)
            {
                var row = _configurationRows.Rows[i];
                var key = row.Table.Columns.Contains(_configurationKeyColumn) ? Convert.ToString(row[_configurationKeyColumn]) ?? "" : i.ToString();
                var display = row.Table.Columns.Contains(_configurationDisplayColumn) ? Convert.ToString(row[_configurationDisplayColumn]) ?? key : key;
                if (kind == ConfigurationKind.Area && row.Table.Columns.Contains("Record") && row["Record"] != DBNull.Value)
                {
                    var record = Convert.ToString(row["Record"]) ?? string.Empty;
                    display = string.IsNullOrWhiteSpace(display) ? record : $"{record}. {display}";
                }
                items.Add(new ObjectItem { Name = string.IsNullOrWhiteSpace(display) ? key : display, PlcDisplay = $"Значение: {key}", AreaDisplay = "", RowIndex = i });
            }
            ObjectList.ItemsSource = items;
            // PLC/Area must initialize their preview on first load, not only after a manual click.
            ObjectList.SelectedItem = null;
            if (items.Count > 0)
            {
                var selected = items.FirstOrDefault(item =>
                {
                    var row = _configurationRows.Rows[item.RowIndex];
                    return row.Table.Columns.Contains(_configurationKeyColumn)
                        && string.Equals(Convert.ToString(row[_configurationKeyColumn]), selectedConfigurationKey, StringComparison.OrdinalIgnoreCase);
                }) ?? items[0];
                ObjectList.SelectedItem = selected;
                ObjectList.ScrollIntoView(selected);
                SessionTestLog.Trace("РЕЖИМ3-ПРЕВЬЮ", $"Configuration preview initialized: kind={kind}, table='{_configurationTableName}', rowCount={items.Count}, selectedIndex={ObjectList.SelectedIndex}, offline={_offlineProject != null}");
            }
            else
            {
                ClearObjectEditor();
                SessionTestLog.Trace("РЕЖИМ3-ПРЕВЬЮ", $"Configuration preview empty: kind={kind}, table='{_configurationTableName}', offline={_offlineProject != null}");
            }
            TxtClassTitle.Text = "Элементы проекта";
            TxtObjectCount.Text = $"{items.Count} значений";
            BtnNewObject.Content = "＋  Добавить"; BtnNewObject.IsEnabled = UserAuthorizationService.Has(Permission.ManageObjects);
            BtnNewObjectsBatch.Visibility = Visibility.Collapsed; BtnConfigDelete.Visibility = Visibility.Visible;
            BtnConfigDelete.IsEnabled = false;
            BtnDuplicate.Visibility = BtnDelete.Visibility = Visibility.Collapsed;
            BtnSave.Visibility = Visibility.Visible;
            BtnObjectSearch.IsEnabled = BtnObjectFilter.IsEnabled = false;
            TxtStatus.Text = $"Offline · справочник {kind}: {items.Count} значений";
            ApplyAuthorizationUi();
            return;
        }
        _configurationKind = kind;
        UpdateObjectFilterIndicator();
        _configurationRows = new DataTable();
        _configurationTableName = kind == ConfigurationKind.Plc ? "dbo.PLC" : "dbo.Areas";
        // dbo.Areas.Record is the relationship key stored in object tables' Area field.
        // AreaName is a user-facing label and must never replace that key.
        _configurationKeyColumn = "Record";
        _configurationDisplayColumn = "";
        ObjectList.ItemsSource = null;
        ClearObjectEditor();
        BtnNewObjectsBatch.Visibility = Visibility.Collapsed;
        BtnNewObject.Content = "＋  Добавить";
        BtnConfigDelete.Visibility = Visibility.Visible;
        BtnConfigDelete.IsEnabled = false;
        TxtClassTitle.Text = "Элементы проекта";
        TxtObjectCount.Text = "";
        BtnNewObject.IsEnabled = UserAuthorizationService.Has(Permission.ManageObjects);
        BtnDuplicate.Visibility = BtnDelete.Visibility = Visibility.Collapsed;
        BtnSave.Visibility = Visibility.Visible;
        BtnObjectSearch.IsEnabled = BtnObjectFilter.IsEnabled = false;

        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        try
        {
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var columns = await db.GetTableColumnsAsync(_configurationTableName);
            if (columns.Count == 0)
            {
                TxtStatus.Text = $"Таблица {_configurationTableName} не найдена.";
                return;
            }
            _configurationKeyColumn = columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name
                ?? columns.FirstOrDefault(c => c.Name.Equals(_configurationKeyColumn, StringComparison.OrdinalIgnoreCase))?.Name
                ?? columns.FirstOrDefault(c => c.Name.Equals("Id", StringComparison.OrdinalIgnoreCase))?.Name
                ?? columns.FirstOrDefault()?.Name ?? "";
            _configurationDisplayColumn = kind == ConfigurationKind.Plc
                ? columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("NameL1", StringComparison.OrdinalIgnoreCase))
                    ?? columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    ?? columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase))
                    ?? _configurationKeyColumn
                : columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaNameL2", StringComparison.OrdinalIgnoreCase))
                    ?? columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaNameL1", StringComparison.OrdinalIgnoreCase))
                    ?? columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaName", StringComparison.OrdinalIgnoreCase))
                    ?? _configurationKeyColumn;
            var select = columns.Select(c => c.Name).ToList();
            _configurationRows = await db.GetTableDataPageAsync(_configurationTableName, 1, 5000, select, null);
            var items = new List<ObjectItem>();
            for (int i = 0; i < _configurationRows.Rows.Count; i++)
            {
                var row = _configurationRows.Rows[i];
                var key = row[_configurationKeyColumn] == DBNull.Value ? "" : Convert.ToString(row[_configurationKeyColumn]) ?? "";
                var display = row.Table.Columns.Contains(_configurationDisplayColumn) && row[_configurationDisplayColumn] != DBNull.Value
                    ? Convert.ToString(row[_configurationDisplayColumn]) ?? key : key;
                if (string.IsNullOrWhiteSpace(display)) display = key;
                if (kind == ConfigurationKind.Area && row.Table.Columns.Contains("Record") && row["Record"] != DBNull.Value)
                {
                    var record = Convert.ToString(row["Record"]) ?? string.Empty;
                    display = string.IsNullOrWhiteSpace(display) ? record : $"{record}. {display}";
                }
                items.Add(new ObjectItem { Name = display, PlcDisplay = $"Значение: {key}", AreaDisplay = "", RowIndex = i });
            }
            ObjectList.ItemsSource = items;
            // PLC/Area must initialize their preview on first load, not only after a manual click.
            ObjectList.SelectedItem = null;
            if (items.Count > 0)
            {
                var selected = items.FirstOrDefault(item =>
                {
                    var row = _configurationRows.Rows[item.RowIndex];
                    return row.Table.Columns.Contains(_configurationKeyColumn)
                        && string.Equals(Convert.ToString(row[_configurationKeyColumn]), selectedConfigurationKey, StringComparison.OrdinalIgnoreCase);
                }) ?? items[0];
                ObjectList.SelectedItem = selected;
                ObjectList.ScrollIntoView(selected);
                SessionTestLog.Trace("РЕЖИМ3-ПРЕВЬЮ", $"Configuration preview initialized: kind={kind}, table='{_configurationTableName}', rowCount={items.Count}, selectedIndex={ObjectList.SelectedIndex}, offline={_offlineProject != null}");
            }
            else
            {
                ClearObjectEditor();
                SessionTestLog.Trace("РЕЖИМ3-ПРЕВЬЮ", $"Configuration preview empty: kind={kind}, table='{_configurationTableName}', offline={_offlineProject != null}");
            }
            TxtObjectCount.Text = $"{items.Count} значений";
            TxtStatus.Text = $"{TxtClassTitle.Text}: {items.Count}";
            ApplyAuthorizationUi();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Не удалось загрузить конфигурацию: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
        }
    }

    private async Task ResolveMissingOnlineStorageMappingsAsync()
    {
        if (_offlineProject != null || _connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        try
        {
            var schema = await new DatabaseArchitectureAnalyzer(_connection.ToConnectionString(_selectedDatabase)).AnalyzeAsync();
            if (schema?.Tables == null || schema.Tables.Count == 0) return;
            var candidates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Valve"] = new[] { "dbo.Valves", "dbo.Valve" },
                ["DSV"] = new[] { "dbo.Valves2S", "dbo.Valve2S", "dbo.Valve2s" },
                ["Motor"] = new[] { "dbo.Motors", "dbo.Motor" },
                ["DI"] = new[] { "dbo.DiscretIN", "dbo.DiscretIn" },
                ["DO"] = new[] { "dbo.DiscretOUT", "dbo.DiscretOut" },
                ["AI"] = new[] { "dbo.AnaIN", "dbo.AnaIn", "dbo.AnalogIn" },
                ["AO"] = new[] { "dbo.AnaOUT", "dbo.AnaOut", "dbo.AnalogOut" },
                ["PID"] = new[] { "dbo.Reglers", "dbo.Regler" },
                ["Status"] = new[] { "dbo.Statuses", "dbo.Status" },
                ["Program"] = new[] { "dbo.Prog", "dbo.Programs", "dbo.Program" },
                ["Step"] = new[] { "dbo.Prog_Step", "dbo.ProgramStep" },
                ["Matrix"] = new[] { "dbo.Matrix", "dbo.Matrices", "dbo.Matrix_Devices" }
            };
            foreach (var cls in _architecture.Classes)
            {
                if (!string.IsNullOrWhiteSpace(cls.PrimaryStorage?.TableName)) continue;
                string table = null;
                if (candidates.TryGetValue(cls.Name, out var options))
                    table = options.FirstOrDefault(n => schema.FindTable(n) != null);
                if (table == null)
                {
                    var matches = schema.Tables.Where(t => t.Name.Equals(cls.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 1) table = matches[0].FullName;
                }
                if (table == null) continue;
                cls.StorageMappings ??= new List<StorageMapping>();
                cls.StorageMappings.Add(new StorageMapping { Role = "primary", TableName = table });
                SessionTestLog.Trace("РЕЖИМ3-СОПОСТАВЛЕНИЕ", $"Resolved missing primary storage: class='{cls.Name}', table='{table}' (runtime mapping only)");
            }
        }
        catch (Exception ex)
        {
            SessionTestLog.Trace("РЕЖИМ3-СОПОСТАВЛЕНИЕ", $"Could not infer missing mappings from database schema: {ex}");
        }
    }

    private async Task RefreshProjectAvailabilityAsync()
    {
        if (_offlineProject != null) { RefreshClasses(); return; }
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            RefreshClasses();
            return;
        }

        foreach (var cls in _architecture.Classes) cls.Available = true;
        try
        {
            _classRegistry = new ClassRegistryService(_connection.ToConnectionString(_selectedDatabase));
            var availability = await _classRegistry.GetAvailabilityAsync();
            foreach (var cls in _architecture.Classes)
                cls.Available = availability.TryGetValue(cls.ClassNumber, out bool available) ? available : true;
        }
        catch (Exception ex)
        {
            // If a legacy DB has no registry yet, system defaults remain available.
            SessionTestLog.Trace("РЕЖИМ3-РЕЕСТР-КЛАССОВ", $"Registry lookup failed; using defaults. {ex}");
        }

        await ResolveMissingOnlineStorageMappingsAsync();
        RefreshClasses();
        SessionTestLog.Trace("РЕЖИМ3-КЛАССЫ", $"RefreshClasses completed: total={_architecture.Classes.Count}, available={_architecture.Classes.Count(c => c.Available)}, selected='{_selectedClass?.Name ?? "<null>"}'");
    }

    private async void ClassList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressClassSelectionChanged) return;
        if (ClassList.SelectedItem is not ClassDefinition cls)
        {
            ResetProgramTools();
            CancelLoad();
            _selectedClass = null;
            _selectedRow = null;
            _selectedConfigurationItem = null;
            _objects = new DataTable();
            ObjectList.SelectedItem = null;
            ObjectList.ItemsSource = Array.Empty<ObjectItem>();
            ClearObjectEditor();
            TxtObjectCount.Text = string.Empty;
            TxtClassTitle.Text = string.Empty;
            TxtStatus.Text = "Выберите конфигурацию";
            BtnNewObject.IsEnabled = BtnNewObjectsBatch.IsEnabled = BtnDelete.IsEnabled = BtnDuplicate.IsEnabled = BtnSave.IsEnabled = false;
            BtnObjectSearch.IsEnabled = BtnObjectFilter.IsEnabled = false;
            return;
        }
        CancelLoad();
        _selectedClass = cls;
        _objects = new DataTable();
        ObjectList.SelectedItem = null;
        ObjectList.ItemsSource = Array.Empty<ObjectItem>();
        TxtObjectCount.Text = string.Empty;
        ClearObjectEditor();
        SessionTestLog.Trace("РЕЖИМ3-КЛАСС", $"Selected class='{cls.Name}', classNumber={cls.ClassNumber}, primaryStorage='{cls.PrimaryStorage?.TableName ?? "<null>"}', fieldCount={cls.Fields?.Count ?? 0}, database='{_selectedDatabase}'");
        _configurationKind = ConfigurationKind.Class;
        UpdateObjectFilterIndicator();
        BtnDuplicate.Visibility = BtnDelete.Visibility = BtnSave.Visibility = Visibility.Visible;
        BtnObjectSearch.IsEnabled = BtnObjectFilter.IsEnabled = true;
        _selectedConfigurationItem = new ConfigurationItem(ConfigurationKind.Class, cls.Name, cls.Name, false, false, cls);
        BtnConfigDelete.IsEnabled = false;
        BtnConfigDelete.Visibility = Visibility.Collapsed;
        BtnNewObjectsBatch.Visibility = Visibility.Visible;
        await LoadActualClassFieldsAsync();
        TxtClassTitle.Text = cls.Name;
        BtnNewObject.IsEnabled = !string.IsNullOrWhiteSpace(cls.PrimaryStorage?.TableName) && !string.IsNullOrWhiteSpace(_selectedDatabase);
        ClearObjectEditor();
        await LoadObjectsAsync();
        RebuildObjectFilters();
        ConfigureProgramToolsForClass(cls);
    }

    private async Task LoadActualClassFieldsAsync()
    {
        SessionTestLog.Trace("РЕЖИМ3-ПОЛЯ", $"START: class='{_selectedClass?.Name ?? "<null>"}', table='{_selectedClass?.PrimaryStorage?.TableName ?? "<null>"}', offline={_offlineProject != null}, database='{_selectedDatabase}'");
        if (_selectedClass?.PrimaryStorage == null) { SessionTestLog.Trace("РЕЖИМ3-ПОЛЯ", "EARLY RETURN: PrimaryStorage is null"); return; }
        if (_selectedClass.Fields == null) _selectedClass.Fields = new List<FieldDefinition>();
        if (_offlineProject != null)
        {
            if (_offlineReader == null) _offlineReader = new ClassArchitectureReader(_offlineSource ?? new OfflineProjectDataSource(_offlineProject));
            var localFields = await _offlineReader.LoadFieldsAsync(_selectedClass.PrimaryStorage.TableName, _selectedClass.Name);
            // Prefer the class model fields when present; schema fields are the physical snapshot.
            if (_selectedClass.Fields.Count == 0) _selectedClass.Fields = localFields ?? new List<FieldDefinition>();
            return;
        }
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        try
        {
            var reader = new Configurator.Infrastructure.Database.ClassArchitectureReader(_connection.ToConnectionString(_selectedDatabase));
            var actual = await reader.LoadFieldsAsync(_selectedClass.PrimaryStorage.TableName, _selectedClass.Name);
            SessionTestLog.Trace("РЕЖИМ3-ПОЛЯ", $"Database field metadata returned {actual.Count} fields for '{_selectedClass.PrimaryStorage.TableName}'");
            if (actual.Count > 0)
            {
                try
                {
                    var schema = await new DatabaseArchitectureAnalyzer(_connection.ToConnectionString(_selectedDatabase)).AnalyzeAsync();
                    if (schema != null) ApplyInferredFieldRelations(actual, schema, _selectedClass.PrimaryStorage.TableName);
                }
                catch { /* relation metadata is supplementary; SQL fields remain usable */ }
                _selectedClass.Fields = actual;
            }
        }
        catch (Exception ex) { SessionTestLog.Trace("ОШИБКА-РЕЖИМ3-ПОЛЯ", ex.ToString()); }
        SessionTestLog.Trace("РЕЖИМ3-ПОЛЯ", $"END: class='{_selectedClass?.Name ?? "<null>"}', fields={_selectedClass?.Fields?.Count ?? 0}");
    }

    private static void ApplyInferredFieldRelations(List<FieldDefinition> fields, DatabaseSchema schema, string sourceTable)
    {
        if (fields == null || schema == null || schema.Relations == null || string.IsNullOrWhiteSpace(sourceTable)) return;
        foreach (var field in fields.Where(f => f != null))
        {
            if (!string.IsNullOrWhiteSpace(field.LookupTable) && !string.IsNullOrWhiteSpace(field.LookupKeyField)) continue;
            var relation = schema.Relations.FirstOrDefault(r =>
                r != null && !string.IsNullOrWhiteSpace(r.SourceTable) &&
                r.SourceTable.Equals(sourceTable, StringComparison.OrdinalIgnoreCase) &&
                r.SourceColumns != null && r.SourceColumns.Count == 1 &&
                !string.IsNullOrWhiteSpace(r.SourceColumns[0]) &&
                r.SourceColumns[0].Equals(field.Name, StringComparison.OrdinalIgnoreCase));
            if (relation == null || relation.TargetColumns == null || relation.TargetColumns.Count != 1) continue;
            field.LookupTable = relation.TargetTable;
            field.LookupKeyField = relation.TargetColumns[0];
            var target = string.IsNullOrWhiteSpace(relation.TargetTable) ? null : schema.FindTable(relation.TargetTable);
            field.LookupDisplayField = target?.Columns
                .Select(c => c.Name)
                .FirstOrDefault(n => n.Equals("NameL1", StringComparison.OrdinalIgnoreCase))
                ?? target?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("Name", StringComparison.OrdinalIgnoreCase))
                ?? target?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase))
                ?? relation.TargetColumns[0];
        }
    }

    private async Task LoadObjectsAsync()
    {
        CancelLoad();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        var generation = _loadGeneration;
        var requestedClass = _selectedClass;
        var requestedDatabase = _selectedDatabase;
        SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", $"START: class='{_selectedClass?.Name ?? "<null>"}', table='{_selectedClass?.PrimaryStorage?.TableName ?? "<null>"}', database='{_selectedDatabase}', offline={_offlineProject != null}, connectionPresent={_connection != null}");
        if (_selectedClass?.PrimaryStorage == null || (_offlineProject == null && string.IsNullOrWhiteSpace(_selectedDatabase)))
        {
            _objects = new DataTable();
            ObjectList.SelectedItem = null;
            ObjectList.ItemsSource = Array.Empty<ObjectItem>();
            ClearObjectEditor();
            TxtObjectCount.Text = string.Empty;
            SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", $"EARLY RETURN: storageMissing={_selectedClass?.PrimaryStorage == null}, databaseMissing={string.IsNullOrWhiteSpace(_selectedDatabase)}");
            TxtStatus.Text = _selectedClass?.PrimaryStorage == null ? "Выберите конфигурацию." : "Проект не выбран.";
            return;
        }

        try
        {
            if (_offlineProject != null)
            {
                if (_offlineReader == null) _offlineReader = new ClassArchitectureReader(_offlineSource ?? new OfflineProjectDataSource(_offlineProject));
                var mappedTable = (_offlineSource ?? new OfflineProjectDataSource(_offlineProject)).ResolveTableName(_selectedClass.PrimaryStorage.TableName);
                var loadedOfflineObjects = await _offlineReader.ReadRowsAsync(mappedTable, 5000, ct);
                if (ct.IsCancellationRequested || generation != _loadGeneration || !ReferenceEquals(requestedClass, _selectedClass) || requestedDatabase != _selectedDatabase) return;
                _objects = loadedOfflineObjects;
                ObjectList.ItemsSource = null;
                RebuildObjectFilters();
                ApplyObjectFilters();
                SelectInitialObjectForPreview();
                TxtStatus.Text = $"Offline · {_selectedClass.Name} · объектов: {_objects.Rows.Count}";
                BtnNewObject.IsEnabled = UserAuthorizationService.Has(Permission.AddData); BtnNewObjectsBatch.IsEnabled = UserAuthorizationService.Has(Permission.AddData);
                return;
            }
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var columns = await db.GetTableColumnsAsync(_selectedClass.PrimaryStorage.TableName, ct);
            SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", $"Schema response: table='{_selectedClass.PrimaryStorage.TableName}', columns={columns.Count}, names=[{string.Join(", ", columns.Select(c => c.Name))}]");
            if (columns.Count == 0) { SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", "EARLY RETURN: database returned zero columns"); return; }
            var order = columns.Take(3).Select(x => x.Name).ToList();
            SessionTestLog.Trace("SQL-ЗАПРОС", $"Mode3 objects: SELECT * FROM [{_selectedClass.PrimaryStorage.TableName.Replace(".", "].[")}] ORDER BY [{string.Join("], [", order)}] OFFSET 0 ROWS FETCH NEXT 5000 ROWS ONLY; table='{_selectedClass.PrimaryStorage.TableName}'");
            var loadedObjects = await db.GetTableDataPageAsync(_selectedClass.PrimaryStorage.TableName, 1, 5000, order, null, ct);
            if (ct.IsCancellationRequested || generation != _loadGeneration || !ReferenceEquals(requestedClass, _selectedClass) || requestedDatabase != _selectedDatabase)
            {
                SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", $"STALE RESULT IGNORED: requestedClass='{requestedClass?.Name}', currentClass='{_selectedClass?.Name}', requestedDatabase='{requestedDatabase}', currentDatabase='{_selectedDatabase}'");
                return;
            }
            _objects = loadedObjects;
            ObjectList.ItemsSource = null;
            SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", $"Query response: rows={_objects.Rows.Count}, columns={_objects.Columns.Count}, columnNames=[{string.Join(", ", _objects.Columns.Cast<DataColumn>().Select(c => c.ColumnName))}]");
            RebuildObjectFilters();
            ApplyObjectFilters();
            SelectInitialObjectForPreview();
            TxtStatus.Text = $"{_selectedClass.Name} · объектов: {_objects.Rows.Count}";
            SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", $"END OK: rows={_objects.Rows.Count}, status='{TxtStatus.Text}'");
        }
        catch (OperationCanceledException) { SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", "CANCELLED: load token cancelled"); }
        catch (Exception ex)
        {
            SessionTestLog.Trace("ОШИБКА-РЕЖИМ3-ОБЪЕКТЫ", ex.ToString());
            TxtStatus.Text = $"Не удалось загрузить объекты: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
        }
    }

    private List<ObjectItem> BuildObjectNames(DataTable table, ClassDefinition cls)
    {
        var result = new List<ObjectItem>();
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            string plc = row.Table.Columns.Contains("PLC") ? Convert.ToString(row["PLC"]) ?? "" : "";
            string area = row.Table.Columns.Contains("Area") ? Convert.ToString(row["Area"]) ?? "" : "";
            result.Add(new ObjectItem
            {
                Name = PickName(row, cls),
                PlcDisplay = string.IsNullOrWhiteSpace(plc) ? "" : $"PLC: {FindFilterTitle(CmbPlcFilter, plc)}",
                AreaDisplay = string.IsNullOrWhiteSpace(area) ? "" : $"Area: {FindFilterTitle(CmbAreaFilter, area)}",
                RowIndex = i
            });
        }
        return result;
    }

    private static string PickName(DataRow row, ClassDefinition cls)
    {
        // A row/class can be temporarily absent during duplicate/refresh flows.
        // Keep the preview label generation defensive instead of throwing NRE.
        if (row == null) return cls?.Name ?? "Объект";
        string className = string.IsNullOrWhiteSpace(cls?.Name) ? "Объект" : cls.Name;
        foreach (var field in new[] { "Name", "Name_L1", "NameL1", "Tag" })
            if (row.Table.Columns.Contains(field) && row[field] != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(row[field])))
                return Convert.ToString(row[field]);
        string plc = row.Table.Columns.Contains("PLC") ? Convert.ToString(row["PLC"]) : "";
        string record = row.Table.Columns.Contains("Record") ? Convert.ToString(row["Record"]) : "";
        return string.IsNullOrWhiteSpace(plc) && string.IsNullOrWhiteSpace(record) ? $"{className} · {row.GetHashCode()}" : $"{className} · PLC={plc} · Record={record}";
    }


    private static string FindFilterTitle(ComboBox combo, string key)
    {
        if (combo?.ItemsSource is IEnumerable<FilterOption> options)
        {
            var option = options.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (option != null && !string.IsNullOrWhiteSpace(option.Title)) return option.Title == key ? key : $"{option.Title} ({key})";
        }
        return key;
    }

    private void ObjectList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete || ObjectList.SelectedItems.Count == 0) return;
        DeleteObject_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void ObjectList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(ObjectList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is ObjectItem item)
        {
            ObjectList.SelectedItems.Clear();
            ObjectList.SelectedItem = item;
        }
    }

    private ContextMenu BuildObjectContextMenu()
    {
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Открыть объект" }; edit.Click += (s, e) => BuildObjectEditor();
        menu.Items.Add(edit);
        return menu;
    }

    private async void ObjectList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_offlineProject == null || _configurationKind == ConfigurationKind.Class || _selectedRow == null) return;
        if (!UserAuthorizationService.Require(Permission.ManageObjects)) return;
        var schema = _offlineProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(_configurationTableName, StringComparison.OrdinalIgnoreCase));
        if (schema == null) { Configurator.Infrastructure.ModernDialog.Show("В локальном проекте отсутствует схема справочника.", "Редактирование", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var columns = schema.Columns.Select(c => new ColumnInfo { Name = c.Name, DataType = c.DataType, IsIdentity = c.IsIdentity, IsNullable = c.IsNullable, IsComputed = c.IsComputed, MaxLength = c.MaxLength, Precision = c.Precision, Scale = c.Scale, HasDefault = c.HasDefault }).ToList();
        var initial = _selectedRow.Table.Columns.Cast<DataColumn>().ToDictionary(c => c.ColumnName, c => _selectedRow[c], StringComparer.OrdinalIgnoreCase);
        var dialog = new AddRowWindow(_configurationTableName, columns, 0, _configurationKeyColumn, null, null, null, null, new Dictionary<string, List<LookupOption>>(), initial, true) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        if (!dialog.Confirmed || dialog.Values == null) return;
        var keyColumns = schema.KeyColumns;
        if (keyColumns.Count == 0) throw new InvalidOperationException("В локальном снимке не сохранён ключ справочника.");
        var keyValues = keyColumns.ToDictionary(k => k, k => (object?)(_selectedRow.Table.Columns.Contains(k) ? _selectedRow[k] : null), StringComparer.OrdinalIgnoreCase);
        var updateValues = dialog.Values.Where(pair => !keyColumns.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase);
        await RequireCrudSource().UpdateRecordAsync(_configurationTableName, keyColumns, keyValues, updateValues);
        await LoadConfigurationValuesAsync(_configurationKind);
        TxtStatus.Text = "Значение справочника изменено в локальном проекте";
    }

    /// <summary>
    /// После первоначальной загрузки класса выбирает первый видимый объект,
    /// чтобы панель превью получила данные сразу, без ручного переключения объектов.
    /// Не переключает классы и не запускает дополнительную загрузку источника данных.
    /// </summary>
    private void SelectInitialObjectForPreview()
    {
        if (ObjectList.SelectedItem != null) return;
        if (ObjectList.Items.Count == 0) return;

        var firstItem = ObjectList.Items.OfType<ObjectItem>().FirstOrDefault();
        if (firstItem == null) return;

        ObjectList.SelectedItem = firstItem;
        ObjectList.ScrollIntoView(firstItem);
        SessionTestLog.Trace(
            "РЕЖИМ3-ПРЕВЬЮ",
            $"Initial preview selected: class='{_selectedClass?.Name ?? "<null>"}', rowIndex={firstItem.RowIndex}, items={ObjectList.Items.Count}, offline={_offlineProject != null}");
    }

    private void ObjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ObjectList.SelectedItems.Cast<ObjectItem>().ToList();
        if (_configurationKind != ConfigurationKind.Class)
        {
            ClearObjectEditor();
            BtnConfigDelete.IsEnabled = selected.Count == 1 && UserAuthorizationService.Has(Permission.ManageObjects);
            BtnDuplicate.Visibility = BtnDelete.Visibility = Visibility.Collapsed;
            BtnSave.Visibility = Visibility.Visible;
            if (selected.Count == 1)
            {
                var configItem = selected[0];
                if (configItem.RowIndex >= 0 && configItem.RowIndex < _configurationRows.Rows.Count)
                {
                    _selectedRow = _configurationRows.Rows[configItem.RowIndex];
                    TxtObjectName.Text = configItem.Name;
                    TxtObjectMeta.Text = _configurationKind == ConfigurationKind.Plc ? "PLC" : "Area";
                    TxtObjectContext.Text = $"Значение: {GetConfigurationKeyValue()}";
                    TxtPreviewGlyph.Text = _configurationKind == ConfigurationKind.Plc ? "PLC" : "AREA";
                    TxtPreviewGlyph.Visibility = Visibility.Visible;
                    ImgObjectTypePreview.Source = null;
                    ImgObjectTypePreview.Visibility = Visibility.Collapsed;
                    BuildObjectEditor();
                    TxtStatus.Text = $"Выбрано значение: {configItem.Name}";
                }
            }
            ApplyAuthorizationUi();
            return;
        }
        if (selected.Count == 0)
        {
            ClearObjectEditor();
            return;
        }
        var item = selected[0];
        if (item.RowIndex < 0 || item.RowIndex >= _objects.Rows.Count) return;
        _selectedRow = _objects.Rows[item.RowIndex];
        BuildObjectEditor();
        BtnDelete.IsEnabled = true;
        BtnDuplicate.IsEnabled = selected.Count == 1;
        BtnSave.IsEnabled = selected.Count == 1;
        if (_selectedRow != null)
        {
            var plc = GetText("PLC");
            var area = GetText("Area");
            var record = GetText("Record");
            TxtObjectContext.Text = string.Join("   ·   ", new[] {
                string.IsNullOrWhiteSpace(plc) ? null : $"PLC: {plc}",
                string.IsNullOrWhiteSpace(area) ? null : $"Area: {area}",
                string.IsNullOrWhiteSpace(record) ? null : $"Record: {record}"
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        ApplyAuthorizationUi();
        TxtStatus.Text = selected.Count > 1
            ? $"Выбрано объектов: {selected.Count} · редактор показывает первый"
            : $"Выбран объект: {item.Name}";
    }

    private void BuildObjectEditor()
    {
        RenderLookupObjectDetailsAsync();
    }

    private void ShowActionFeedback(string message, bool isError = false)
    {
        if (ActionFeedbackText != null) ActionFeedbackText.Text = message;
        if (ActionFeedbackIcon != null) ActionFeedbackIcon.Visibility = isError ? Visibility.Collapsed : Visibility.Visible;
    }

    private string GetText(string field) => _selectedRow != null && _selectedRow.Table.Columns.Contains(field) && _selectedRow[field] != DBNull.Value ? Convert.ToString(_selectedRow[field]) ?? "" : "";

    private void ApplyAuthorizationUi()
    {
        bool classMode = _configurationKind == ConfigurationKind.Class;
        bool configMode = _configurationKind is ConfigurationKind.Plc or ConfigurationKind.Area;
        bool hasSelection = _selectedRow != null;
        if (BtnNewObject != null) BtnNewObject.IsEnabled = (classMode ? UserAuthorizationService.Has(Permission.AddData) : UserAuthorizationService.Has(Permission.ManageObjects));
        if (BtnNewObjectsBatch != null) BtnNewObjectsBatch.IsEnabled = classMode && UserAuthorizationService.Has(Permission.AddData);
        if (BtnConfigDelete != null)
        {
            BtnConfigDelete.Visibility = configMode ? Visibility.Visible : Visibility.Collapsed;
            BtnConfigDelete.IsEnabled = configMode && UserAuthorizationService.Has(Permission.ManageObjects) && ObjectList.SelectedItems.Count == 1;
        }
        if (BtnObjectSearch != null) BtnObjectSearch.IsEnabled = classMode && _selectedClass != null;
        if (BtnObjectFilter != null) BtnObjectFilter.IsEnabled = classMode && _selectedClass != null;
        if (BtnDuplicate != null) BtnDuplicate.Visibility = classMode ? Visibility.Visible : Visibility.Collapsed;
        if (BtnDelete != null) BtnDelete.Visibility = classMode ? Visibility.Visible : Visibility.Collapsed;
        if (BtnSave != null)
        {
            BtnSave.Visibility = classMode || configMode ? Visibility.Visible : Visibility.Collapsed;
            BtnSave.IsEnabled = classMode
                ? hasSelection && UserAuthorizationService.Has(Permission.EditData)
                : configMode && hasSelection && UserAuthorizationService.Has(Permission.ManageObjects);
        }
        if (BtnDuplicate != null) BtnDuplicate.IsEnabled = _offlineProject == null && UserAuthorizationService.Has(Permission.AddData) && BtnDuplicate.IsEnabled;
        if (BtnDelete != null) BtnDelete.IsEnabled = UserAuthorizationService.Has(Permission.DeleteData) && BtnDelete.IsEnabled;
    }

    private static Dictionary<string, object> SnapshotRow(DataRow row)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (row == null) return values;
        foreach (DataColumn column in row.Table.Columns)
            values[column.ColumnName] = row[column] == DBNull.Value ? DBNull.Value : row[column];
        return values;
    }

    private static Dictionary<string, ChangeJournalValue> JournalValues(IReadOnlyDictionary<string, object> values)
        => values.ToDictionary(x => x.Key, x => ChangeJournalValue.FromObject(x.Value), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, object> ResolveMode3Identity(IReadOnlyDictionary<string, object> values)
    {
        var keys = _offlineSource?.GetSchema(_selectedClass?.PrimaryStorage?.TableName ?? "")?.KeyColumns
            ?? _selectedClass?.PrimaryStorage?.KeyColumns ?? new List<string>();
        if (keys.Count == 0) keys = new[] { "PLC", "Record", "Area" }.Where(values.ContainsKey).ToList();
        return keys.Where(values.ContainsKey).ToDictionary(k => k, k => values[k], StringComparer.OrdinalIgnoreCase);
    }

    private void RecordMode3Mutation(string table, ChangeJournalOperationKind kind, string description,
        IReadOnlyDictionary<string, object> before, IReadOnlyDictionary<string, object> after,
        IReadOnlyDictionary<string, object> identityBefore, IReadOnlyDictionary<string, object> identityAfter, int expectedGeneration = -1,
        bool restoreIncludesAllChanges = true, string details = null,
        IReadOnlyList<ChangeJournalRowChange> relatedChanges = null,
        ChangeJournalOperationKind? journalOperationKind = null)
    {
        if (expectedGeneration >= 0 && expectedGeneration != _workspaceGeneration)
        {
            System.Diagnostics.Debug.WriteLine($"[ChangeJournal] Ignored stale Mode 3 event. capturedGeneration={expectedGeneration}; currentGeneration={_workspaceGeneration}");
            return;
        }
        if (before != null && after != null)
        {
            var changed = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Any(key =>
            {
                before.TryGetValue(key, out var a); after.TryGetValue(key, out var b);
                return !string.Equals(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    Convert.ToString(b, System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);
            });
            if (!changed) return;
        }
        var change = new ChangeJournalRowChange
        {
            TableName = table ?? "",
            Kind = kind,
            Before = JournalValues(before ?? new Dictionary<string, object>()),
            After = JournalValues(after ?? new Dictionary<string, object>()),
            IdentityBefore = JournalValues(identityBefore ?? new Dictionary<string, object>()),
            IdentityAfter = JournalValues(identityAfter ?? new Dictionary<string, object>())
        };
        var offline = _offlineProject != null;
        var server = _connection?.Server ?? "";
        var database = _selectedDatabase ?? "";
        var entityIdentity = identityAfter?.Count > 0 ? identityAfter : identityBefore;
        var projectId = offline ? (_offlineProject.Manifest?.ProjectId ?? "") : "online:" + server.Trim().ToLowerInvariant() + "/" + database.Trim().ToLowerInvariant();
        ChangeJournalRequested?.Invoke(new ChangeJournalEntry
        {
            Category = "Объект",
            Source = table ?? "",
            Description = description,
            Details = details ?? (identityAfter?.Count > 0 ? string.Join(", ", identityAfter.Select(x => $"{x.Key}={x.Value}")) :
                identityBefore?.Count > 0 ? string.Join(", ", identityBefore.Select(x => $"{x.Key}={x.Value}")) : "Идентификатор источником не возвращён"),
            OperationKind = journalOperationKind ?? kind,
            RestoreIncludesAllChanges = restoreIncludesAllChanges,
            ProjectType = offline ? "Offline" : "Online",
            ProjectId = projectId,
            ProjectName = offline ? (_offlineProject.Manifest?.Name ?? "") : database,
            Server = offline ? "" : server,
            Database = offline ? "" : database,
            Mode = 3,
            UserRole = UserAuthorizationService.CurrentRole?.Name ?? "",
            EntityType = "Объект",
            EntityIdentity = entityIdentity?.Count > 0 ? string.Join("; ", entityIdentity.Select(x => $"{x.Key}={x.Value}")) : "",
            Result = "Успешно",
            Changes = new List<ChangeJournalRowChange> { change }.Concat(relatedChanges ?? Array.Empty<ChangeJournalRowChange>()).ToList()
        });
    }

    private HashSet<string> CaptureOfflineRecordKeys(string table)
        => _offlineProject?.LocalRecords.Where(r => r.TableName.Equals(table, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Key).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

    private void RecordCreatedMode3Object(string table, IReadOnlyDictionary<string, object?> suppliedValues,
        HashSet<string> previousOfflineKeys, string description, int expectedGeneration = -1,
        ChangeJournalOperationKind operationKind = ChangeJournalOperationKind.Insert,
        IReadOnlyDictionary<string, object> sourceValues = null)
    {
        if (expectedGeneration >= 0 && expectedGeneration != _workspaceGeneration)
        {
            System.Diagnostics.Debug.WriteLine($"[ChangeJournal] Ignored stale Mode 3 create event. capturedGeneration={expectedGeneration}; currentGeneration={_workspaceGeneration}");
            return;
        }
        var values = suppliedValues.ToDictionary(x => x.Key, x => x.Value == null ? DBNull.Value : x.Value, StringComparer.OrdinalIgnoreCase);
        if (_offlineProject != null)
        {
            var added = _offlineProject.LocalRecords.FirstOrDefault(r => r.TableName.Equals(table, StringComparison.OrdinalIgnoreCase) && !previousOfflineKeys.Contains(r.Key));
            if (added != null)
            {
                values = added.Values.ToDictionary(x => x.Key, x => x.Value == null ? (object)DBNull.Value : x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            }
        }
        var identity = ResolveMode3Identity(values);
        var sourceIdentity = sourceValues == null ? null : ResolveMode3Identity(sourceValues);
        RecordMode3Mutation(table, operationKind, description, sourceValues, values, sourceIdentity, identity, expectedGeneration);
    }

    private string DescribeDuplicate(string sourceName, string targetName, IReadOnlyDictionary<string, object> targetValues)
    {
        var identity = ResolveMode3Identity(targetValues);
        var identityText = identity.Count == 0
            ? "ключ не определён"
            : string.Join(", ", identity.Select(pair => $"{pair.Key}={pair.Value}"));
        return $"Дублировано «{sourceName}» → «{targetName}»; новый ключ: {identityText}";
    }

    private async void SaveObject_Click(object sender, RoutedEventArgs e)
    {
        SessionTestLog.Trace("РЕЖИМ3-СОХРАНЕНИЕ", $"Save requested; kind={_configurationKind}; selected={_selectedRow != null}; offline={_offlineProject != null}");
        if (_configurationKind is ConfigurationKind.Plc or ConfigurationKind.Area)
        {
            await SaveConfigurationObjectAsync();
            return;
        }
        if (!UserAuthorizationService.Require(Permission.EditData)) return;
        SaveObjectWithLookupControlsAsync();
    }

    private async Task SaveConfigurationObjectAsync()
    {
        if (!UserAuthorizationService.Require(Permission.ManageObjects) || _selectedRow == null) return;
        try
        {
            var row = _selectedRow;
            var schema = _offlineProject != null
                ? _offlineSource?.GetSchema(_configurationTableName)
                : await (_onlineSource ?? throw new InvalidOperationException("Online-источник схемы не привязан к выбранному проекту."))
                    .GetSchemaAsync(_configurationTableName);
            var keyColumns = schema?.HasStableKey == true
                ? schema.KeyColumns.ToList()
                : new List<string>();
            if (keyColumns.Count == 0)
                throw new InvalidOperationException("У справочника не найден подтверждённый устойчивый ключ. Изменение отменено.");

            var keyValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyColumn in keyColumns)
            {
                if (!row.Table.Columns.Contains(keyColumn) || row[keyColumn] == DBNull.Value)
                    throw new InvalidOperationException($"В выбранной строке отсутствует ключ «{keyColumn}». Изменение отменено.");
                keyValues[keyColumn] = row[keyColumn];
            }

            var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var control in FindEditorControls(ObjectEditor))
            {
                if (control.Tag is not string fieldName || !row.Table.Columns.Contains(fieldName)
                    || IsConfigurationIdentityField(fieldName) || keyColumns.Contains(fieldName, StringComparer.OrdinalIgnoreCase)) continue;

                var column = row.Table.Columns[fieldName];
                var text = control switch
                {
                    TextBox textBox => textBox.Text,
                    ComboBox comboBox => Convert.ToString(comboBox.SelectedValue) ?? "",
                    _ => null
                };
                if (text == null) continue;
                object value;
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (!column.AllowDBNull)
                        throw new InvalidOperationException($"Поле «{fieldName}» обязательно для заполнения.");
                    value = DBNull.Value;
                }
                else if (column.DataType == typeof(string)) value = text;
                else
                {
                    try { value = Convert.ChangeType(text, column.DataType, System.Globalization.CultureInfo.InvariantCulture); }
                    catch { throw new InvalidOperationException($"Поле «{fieldName}» требует значение типа {column.DataType.Name}."); }
                }

                var previous = row[fieldName];
                if (!Equals(previous, value)) updates[fieldName] = value;
            }

            if (updates.Count == 0)
            {
                SessionTestLog.Trace("РЕЖИМ3-СОХРАНЕНИЕ", $"No changes detected; table='{_configurationTableName}'");
                TxtStatus.Text = "Изменений нет";
                ShowActionFeedback(TxtStatus.Text);
                BtnSave.IsEnabled = false;
                return;
            }

            var tableName = _configurationTableName;
            var selectedKey = GetConfigurationKeyValue();
            SessionTestLog.Trace("РЕЖИМ3-СОХРАНЕНИЕ", $"Updating table='{tableName}'; fields={string.Join(",", updates.Keys)}; offline={_offlineProject != null}");
            await RequireCrudSource().UpdateRecordAsync(tableName, keyColumns, keyValues, updates);
            SessionTestLog.Trace("РЕЖИМ3-СОХРАНЕНИЕ", $"Update completed; table='{tableName}'; offline={_offlineProject != null}");
            foreach (var pair in updates) row[pair.Key] = pair.Value ?? DBNull.Value;
            try
            {
                await RefreshConfigurationLookupsAsync();
                if (_configurationKind == ConfigurationKind.Plc && _selectedRow != null)
                    await RenderConfigurationObjectDetailsAsync(_selectedRow, ++_objectDetailsGeneration);
            }
            catch (Exception refreshError)
            {
                SessionTestLog.Trace("ОШИБКА-РЕЖИМ3-ПЕРЕОТОБРАЖЕНИЕ", $"Configuration saved but refresh failed; kind={_configurationKind}; table='{tableName}'; key='{selectedKey}'; offline={_offlineProject != null}; exception={refreshError}");
                TxtStatus.Text = $"Справочник {_configurationKind} сохранён; отображение не обновлено";
                ShowActionFeedback($"Изменения «{selectedKey}» сохранены, но список не удалось обновить", isError: true);
                return;
            }
            TxtStatus.Text = $"Справочник {_configurationKind} сохранён";
            ShowActionFeedback($"Изменения «{selectedKey}» сохранены");
        }
        catch (Exception ex)
        {
            ShowSaveError(ex, "справочник", "Сохранение справочника", _configurationTableName);
        }
    }

    private void ShowSaveError(Exception exception, string item, string dialogTitle, string tableName)
    {
        SessionTestLog.Trace("ОШИБКА-РЕЖИМ3-СОХРАНЕНИЕ", $"Save failed; item={item}; kind={_configurationKind}; table='{tableName}'; offline={_offlineProject != null}; exception={exception}");
        var userMessage = GetSafeSaveErrorMessage(exception, item);
        ShowActionFeedback($"Ошибка: {userMessage}", isError: true);
        Configurator.Infrastructure.ModernDialog.Show(userMessage, dialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string GetSafeSaveErrorMessage(Exception exception, string item)
    {
        var sqlException = FindSqlException(exception);
        if (sqlException != null)
        {
            return sqlException.Number is 2601 or 2627
                ? "Запись с таким значением уже существует. Измените его и повторите сохранение."
                : $"Не удалось сохранить {item}. Проверьте подключение и повторите попытку. Подробности записаны в журнал диагностики.";
        }

        var validationMessage = FindExplicitSaveValidationMessage(exception);
        return validationMessage
            ?? $"Не удалось сохранить {item}. Проверьте данные и повторите попытку. Подробности записаны в журнал диагностики.";
    }

    private static string? FindExplicitSaveValidationMessage(Exception exception)
    {
        if (exception is not InvalidOperationException) return null;
        var message = exception.Message;
        if (message is "Не удалось определить ключ изменяемой записи."
            or "В локальном снимке не удалось определить ключ записи."
            or "У справочника не найден устойчивый первичный ключ. Изменение отменено."
            or "У справочника не найден подтверждённый устойчивый ключ. Изменение отменено."
            or "Запись не найдена — возможно, она была изменена или удалена другим пользователем."
            or "Запись с таким ключом уже существует в локальном проекте."
            or "Переданные ключевые столбцы не совпадают со стабильным ключом проверенной схемы.")
            return message;
        if (message.StartsWith("В выбранной строке отсутствует ключ «", StringComparison.Ordinal)
            && message.EndsWith("». Изменение отменено.", StringComparison.Ordinal))
            return message;
        if (message.StartsWith("Поле «", StringComparison.Ordinal)
            && (message.EndsWith("» обязательно для заполнения.", StringComparison.Ordinal)
                || message.Contains("» требует значение типа ", StringComparison.Ordinal)))
            return message;
        return null;
    }

    private static Microsoft.Data.SqlClient.SqlException? FindSqlException(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            if (current is Microsoft.Data.SqlClient.SqlException sqlException)
                return sqlException;
        return null;
    }

    private void ProjectModeView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.S ||
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0 ||
            !BtnSave.IsEnabled) return;
        BtnSave.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        e.Handled = true;
    }

    private Dictionary<string, object> CollectValues()
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in FindTextBoxes(ObjectEditor))
        {
            string name = box.Tag as string;
            if (string.IsNullOrWhiteSpace(name) || !_objects.Columns.Contains(name)) continue;
            if (name.Equals("Area", StringComparison.OrdinalIgnoreCase) || new[] { "PLC_Class_Number", "PLCClassNumber", "PLCClasssNumber" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            result[name] = box.Text;
        }
        return result;
    }

    private async Task<Dictionary<string, object>> BuildIdentityAsync()
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (_offlineProject != null)
        {
            var localSchema = _offlineSource?.GetSchema(_selectedClass.PrimaryStorage.TableName);
            var localKeys = localSchema?.KeyColumns ?? _selectedClass.PrimaryStorage.KeyColumns ?? new List<string>();
            foreach (var key in localKeys)
                if (_objects.Columns.Contains(key)) result[key] = _selectedRow[key];
            if (result.Count > 0) return result;
            foreach (var name in new[] { "PLC", "Record", "Area" })
                if (_objects.Columns.Contains(name)) result[name] = _selectedRow[name];
            if (result.Count > 0) return result;
            throw new InvalidOperationException("В локальном снимке не удалось определить ключ записи.");
        }

        foreach (var name in new[] { "PLC", "Record", "Area" })
            if (_objects.Columns.Contains(name)) result[name] = _selectedRow[name];
        if (result.Count >= 2) return result;

        var primary = await new DatabaseService(_connection.ToConnectionString(_selectedDatabase))
            .GetPrimaryKeyColumnsAsync(_selectedClass.PrimaryStorage.TableName);
        if (primary.Count > 0)
        {
            foreach (var key in primary)
                if (_objects.Columns.Contains(key)) result[key] = _selectedRow[key];
            return result;
        }

        result.Clear();
        foreach (DataColumn column in _objects.Columns)
        {
            var name = column.ColumnName;
            if (!_selectedRow.Table.Columns.Contains(name)) continue;
            result[name] = _selectedRow[name];
        }
        return result;
    }

    private async void DuplicateObject_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Require(Permission.AddData)) return;
        if (_selectedRow == null || _selectedClass?.PrimaryStorage == null) return;
        var mutationGeneration = _workspaceGeneration;
        if (_offlineProject != null)
        {
            var sourceSnapshot = SnapshotRow(_selectedRow);
            var sourceName = PickName(_selectedRow, _selectedClass);
            var clone = _objects.NewRow();
            foreach (DataColumn column in _objects.Columns) clone[column] = _selectedRow[column];
            if (_objects.Columns.Contains("Record"))
            {
                int next = _objects.Rows.Cast<DataRow>().Where(r => r["Record"] != DBNull.Value).Select(r => int.TryParse(Convert.ToString(r["Record"]), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
                clone["Record"] = next;
            }
            var schema = _offlineSource?.GetSchema(_selectedClass.PrimaryStorage.TableName);
            var generatedColumns = new HashSet<string>(schema?.Columns.Where(c => c.IsIdentity || c.IsComputed).Select(c => c.Name) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var values = _objects.Columns.Cast<DataColumn>()
                .Where(c => !generatedColumns.Contains(c.ColumnName))
                .ToDictionary(c => c.ColumnName, c => (object?)(clone[c] == DBNull.Value ? null : clone[c]), StringComparer.OrdinalIgnoreCase);
            var tableName = _selectedClass.PrimaryStorage.TableName;
            var previousKeys = CaptureOfflineRecordKeys(tableName);
            await RequireCrudSource().CreateRecordAsync(tableName, values);
            var duplicateValues = values.ToDictionary(x => x.Key, x => x.Value == null ? (object)DBNull.Value : x.Value, StringComparer.OrdinalIgnoreCase);
            var targetName = PickName(clone, _selectedClass);
            var description = DescribeDuplicate(sourceName, targetName, duplicateValues);
            RecordCreatedMode3Object(tableName, duplicateValues.ToDictionary(x => x.Key, x => (object?)x.Value), previousKeys,
                description, mutationGeneration, ChangeJournalOperationKind.Duplicate, sourceSnapshot);
            await LoadObjectsAsync(); RebuildObjectFilters();
            TxtStatus.Text = "Объект скопирован в локальном проекте";
            ShowActionFeedback(description);
            return;
        }
        try
        {
            var sourceSnapshot = SnapshotRow(_selectedRow);
            var sourceName = PickName(_selectedRow, _selectedClass);
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var columns = await db.GetTableColumnsAsync(_selectedClass.PrimaryStorage.TableName);
            foreach (DataColumn c in _objects.Columns)
            {
                var meta = columns.FirstOrDefault(x => x.Name.Equals(c.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (meta != null && meta.IsReadOnly) continue;
                if (c.ColumnName.Equals("Record", StringComparison.OrdinalIgnoreCase) && _objects.Columns.Contains("PLC")) continue;
                values[c.ColumnName] = _selectedRow[c.ColumnName];
            }
            if (_objects.Columns.Contains("Record"))
            {
                int plc = _objects.Columns.Contains("PLC") && _selectedRow["PLC"] != DBNull.Value ? Convert.ToInt32(_selectedRow["PLC"]) : 0;
                int max = 0;
                foreach (DataRow row in _objects.Rows)
                {
                    if (_objects.Columns.Contains("PLC") && row["PLC"] != DBNull.Value && Convert.ToInt32(row["PLC"]) != plc) continue;
                    if (row["Record"] == DBNull.Value) continue;
                    if (int.TryParse(Convert.ToString(row["Record"]), out int r)) max = Math.Max(max, r);
                }
                values["Record"] = max + 1;
            }
            var tableName = _selectedClass.PrimaryStorage.TableName;
            await RequireCrudSource().CreateRecordAsync(tableName, values);
            var duplicateValues = values.ToDictionary(x => x.Key, x => (object)(x.Value ?? DBNull.Value), StringComparer.OrdinalIgnoreCase);
            var targetName = new[] { "Name", "Name_L1", "NameL1", "Tag" }
                .Where(values.ContainsKey)
                .Select(name => Convert.ToString(values[name]) ?? "")
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? sourceName;
            var description = DescribeDuplicate(sourceName, targetName, duplicateValues);
            RecordCreatedMode3Object(tableName, duplicateValues.ToDictionary(x => x.Key, x => (object?)x.Value), new HashSet<string>(StringComparer.Ordinal),
                description, mutationGeneration, ChangeJournalOperationKind.Duplicate, sourceSnapshot);
            await LoadObjectsAsync();
            TxtStatus.Text = "Объект продублирован";
            ShowActionFeedback(description);
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Дублирование", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteObject_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Require(Permission.DeleteData)) return;
        if (_selectedClass?.PrimaryStorage == null || ObjectList.SelectedItems.Count == 0) return;
        var selected = ObjectList.SelectedItems.Cast<ObjectItem>().ToList();
        var mutationGeneration = _workspaceGeneration;
        var names = selected.Select(x => x.Name).ToList();
        var message = selected.Count == 1
            ? $"Удалить объект «{names[0]}»?"
            : $"Удалить выбранные объекты ({selected.Count} шт.)?";
        if (Configurator.Infrastructure.ModernDialog.Show(message, "Удаление объекта", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (_offlineProject != null)
            {
                var deleteTimer = System.Diagnostics.Stopwatch.StartNew();
                var tableName = _selectedClass.PrimaryStorage.TableName;
                var localSchema = _offlineSource?.GetSchema(tableName);
                var keyColumns = localSchema?.KeyColumns ?? _selectedClass.PrimaryStorage.KeyColumns ?? new List<string>();
                if (keyColumns.Count == 0)
                    keyColumns = new[] { "PLC", "Record", "Area" }.Where(_objects.Columns.Contains).ToList();
                if (keyColumns.Count == 0) throw new InvalidOperationException("В локальном снимке не сохранён ключ для удаления записей.");
                var keyValues = new List<IReadOnlyDictionary<string, object?>>();
                var deletedSnapshots = new List<(Dictionary<string, object> Values, Dictionary<string, object> Identity)>();
                foreach (var item in selected)
                {
                    if (item.RowIndex < 0 || item.RowIndex >= _objects.Rows.Count) continue;
                    var row = _objects.Rows[item.RowIndex];
                    var snapshot = SnapshotRow(row);
                    var identitySnapshot = keyColumns.Where(snapshot.ContainsKey).ToDictionary(k => k, k => snapshot[k], StringComparer.OrdinalIgnoreCase);
                    deletedSnapshots.Add((snapshot, identitySnapshot));
                    var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var column in keyColumns)
                    {
                        if (!_objects.Columns.Contains(column))
                            throw new InvalidOperationException($"В загруженных данных отсутствует ключевой столбец «{column}».");
                        key[column] = row[column] == DBNull.Value ? null : row[column];
                    }
                    keyValues.Add(key);
                }
                var deleted = await RequireCrudSource().DeleteRecordsAsync(tableName, keyColumns, keyValues);
                foreach (var snapshot in deletedSnapshots.Take(Math.Max(0, deleted)))
                    RecordMode3Mutation(tableName, ChangeJournalOperationKind.Delete, "Удалён объект",
                        snapshot.Values, null, snapshot.Identity, null, mutationGeneration);
                await LoadObjectsAsync();
                SessionTestLog.Trace("РЕЖИМ3-УДАЛЕНИЕ", $"Offline delete complete; selected={selected.Count}, deleted={deleted}, table='{tableName}', rowsAfter={_objects.Rows.Count}, totalMs={deleteTimer.ElapsedMilliseconds}");
                TxtStatus.Text = $"Объекты удалены из локального проекта: {deleted}";
                ShowActionFeedback(selected.Count == 1 ? $"Объект «{names[0]}» удалён" : $"Удалено объектов: {deleted}");
                return;
            }
            var connectionString = _connection.ToConnectionString(_selectedDatabase);
            var analyzer = new DatabaseArchitectureAnalyzer(connectionString);
            var schema = await analyzer.AnalyzeAsync();
            var protection = new DeletionProtectionService(connectionString, schema);
            int affected = 0;
            foreach (var item in selected)
            {
                if (item.RowIndex < 0 || item.RowIndex >= _objects.Rows.Count) continue;
                _selectedRow = _objects.Rows[item.RowIndex];
                var beforeSnapshot = SnapshotRow(_selectedRow);
                var identity = await BuildIdentityAsync();

                // Include columns used by configured/known relations so a logical
                // target such as AreaName can be resolved even when it is not part
                // of the normal object identity.
                foreach (var relation in schema.Relations.Where(r => r.TargetTable.Equals(_selectedClass.PrimaryStorage.TableName, StringComparison.OrdinalIgnoreCase)))
                    foreach (var column in relation.TargetColumns)
                        if (_objects.Columns.Contains(column) && !identity.ContainsKey(column))
                            identity[column] = _selectedRow[column];

                var plan = await protection.BuildPlanAsync(new DeletionRequest
                {
                    TableName = _selectedClass.PrimaryStorage.TableName,
                    Key = identity
                });

                if (plan.ValidationErrors.Count > 0)
                {
                    Configurator.Infrastructure.ModernDialog.Show(
                        "Удаление заблокировано:\n\n" + string.Join("\n", plan.ValidationErrors),
                        "Зависимые данные", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                IReadOnlyList<ReferenceReplacement> replacements = Array.Empty<ReferenceReplacement>();
                if (plan.RequiresReplacement)
                {
                    var options = new List<DependencyReplacementWindow.DependencyOption>();
                    foreach (var hit in plan.Dependencies)
                    {
                        if (hit.Relation.SourceColumns.Count != hit.Relation.TargetColumns.Count)
                            throw new InvalidOperationException($"Не удалось подготовить замену для связи {hit.SourceTable} → {hit.TargetTable}.");
                        var candidates = await protection.GetReplacementCandidatesAsync(hit, identity);
                        if (candidates.Count == 0)
                            throw new InvalidOperationException($"Для зависимости {hit.SourceTable}.{hit.SourceColumnDescription} нет допустимого значения для замены.");
                        var current = hit.Relation.TargetColumns.FirstOrDefault(c => identity.ContainsKey(c));
                        options.Add(new DependencyReplacementWindow.DependencyOption
                        {
                            Hit = hit,
                            CurrentValue = current == null ? "" : identity[current],
                            Candidates = candidates
                        });
                    }
                    var dialog = new DependencyReplacementWindow(options) { Owner = Window.GetWindow(this) };
                    if (dialog.ShowDialog() != true) return;
                    replacements = dialog.Replacements;
                }

                await protection.ExecuteAsync(plan, replacements, (_, _, _) => Task.FromResult(true));
                RecordMode3Mutation(_selectedClass.PrimaryStorage.TableName, ChangeJournalOperationKind.Delete,
                    plan.RequiresReplacement ? $"Удалён объект; заменено зависимых значений: {replacements.Count}" : "Удалён объект",
                    beforeSnapshot, null, identity, null, mutationGeneration,
                    restoreIncludesAllChanges: !plan.RequiresReplacement,
                    details: plan.RequiresReplacement
                        ? $"Зависимости изменены вместе с удалением. Отмена отключена, потому что журнал не хранит полное состояние заменённых строк. Ключ: {string.Join(", ", identity.Select(x => $"{x.Key}={x.Value}"))}."
                        : null);
                affected++;
            }
            await LoadObjectsAsync();
            TxtStatus.Text = $"Удалено объектов: {selected.Count} · строк: {affected}";
            ShowActionFeedback(TxtStatus.Text);
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Удаление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string GetConfigurationKeyValue()
    {
        if (_selectedRow == null || string.IsNullOrWhiteSpace(_configurationKeyColumn) || !_selectedRow.Table.Columns.Contains(_configurationKeyColumn)) return "";
        return _selectedRow[_configurationKeyColumn] == DBNull.Value ? "" : Convert.ToString(_selectedRow[_configurationKeyColumn]) ?? "";
    }

    private async Task AddConfigurationValueAsync()
    {
        if (!UserAuthorizationService.Require(Permission.ManageObjects)) return;
        if (_offlineProject != null)
        {
            try
            {
                var schema = _offlineProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(_configurationTableName, StringComparison.OrdinalIgnoreCase));
                if (schema == null) throw new InvalidOperationException($"В снимке отсутствует схема {_configurationTableName}.");
                var columns = schema.Columns.Select(c => new ColumnInfo { Name = c.Name, DataType = c.DataType, IsIdentity = c.IsIdentity, IsNullable = c.IsNullable, IsComputed = c.IsComputed, MaxLength = c.MaxLength, Precision = c.Precision, Scale = c.Scale, HasDefault = c.HasDefault }).ToList();
                var recordColumn = columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
                int nextRecord = 1;
                if (!string.IsNullOrWhiteSpace(recordColumn) && _configurationRows.Columns.Contains(recordColumn)) nextRecord = _configurationRows.Rows.Cast<DataRow>().Select(r => r[recordColumn] == DBNull.Value ? 0 : int.TryParse(Convert.ToString(r[recordColumn]), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
                var dialog = new AddRowWindow(_configurationTableName, columns, nextRecord, recordColumn, null, null, null, null, new Dictionary<string, List<LookupOption>>()) { Owner = Window.GetWindow(this) };
                dialog.ShowDialog();
                if (!dialog.Confirmed || dialog.Values == null || dialog.Values.Count == 0) return;
                await RequireCrudSource().CreateRecordAsync(_configurationTableName, dialog.Values);
                string insertedKey = dialog.Values
                    .Where(pair => pair.Key.Equals(_configurationKeyColumn, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Value == null || pair.Value == DBNull.Value ? null : Convert.ToString(pair.Value))
                    .FirstOrDefault();
                await RefreshConfigurationLookupsAsync(insertedKey);
                TxtStatus.Text = "Значение справочника добавлено в локальный проект";
                ShowActionFeedback(string.IsNullOrWhiteSpace(insertedKey)
                    ? "Значение справочника добавлено"
                    : $"Добавлено значение {_configurationKind}: {insertedKey}");
            }
            catch (Exception ex)
            {
                ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
                Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Добавление локального справочника", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        try
        {
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var columns = await db.GetTableColumnsAsync(_configurationTableName);
            if (columns.Count == 0) throw new InvalidOperationException($"Не найдена таблица {_configurationTableName}.");
            var recordColumn = columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
            var keyColumn = columns.FirstOrDefault(c => c.Name.Equals(_configurationKeyColumn, StringComparison.OrdinalIgnoreCase))?.Name ?? _configurationKeyColumn;
            int nextRecord = 1;
            if (!string.IsNullOrWhiteSpace(recordColumn) && columns.First(c => c.Name.Equals(recordColumn, StringComparison.OrdinalIgnoreCase)).DataType.Contains("int", StringComparison.OrdinalIgnoreCase))
            {
                var data = await db.GetTableDataPageAsync(_configurationTableName, 1, 5000, new[] { recordColumn }, null);
                nextRecord = data.Rows.Cast<DataRow>().Select(r => r[recordColumn] == DBNull.Value ? 0 : Convert.ToInt32(r[recordColumn])).DefaultIfEmpty(0).Max() + 1;
            }
            var lookups = new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
            // Configuration entries such as PLC/Area also have reference fields (e.g. Type -> PLC Types).
            // Populate AddRowWindow lookup editors using the same inference service as object editing.
            var lookupService = new LookupOptionsService(db);
            foreach (var column in columns)
            {
                try
                {
                    var options = await lookupService.TryGetAsync(_configurationKind.ToString(), _configurationTableName, column.Name);
                    if (options.Count > 0) lookups[column.Name] = options.ToList();
                }
                catch { /* A field without a resolvable lookup remains a regular editor. */ }
            }
            var dialog = new AddRowWindow(_configurationTableName, columns, nextRecord, recordColumn, _connection.ToConnectionString(_selectedDatabase), null, null, null, lookups)
            { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            if (!dialog.Confirmed || dialog.Values == null || dialog.Values.Count == 0) return;
            await RequireCrudSource().CreateRecordAsync(_configurationTableName, dialog.Values);
            string insertedKey = dialog.Values
                .Where(pair => pair.Key.Equals(_configurationKeyColumn, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value == null || pair.Value == DBNull.Value ? null : Convert.ToString(pair.Value))
                .FirstOrDefault();
            await RefreshConfigurationLookupsAsync(insertedKey);
            ShowActionFeedback(string.IsNullOrWhiteSpace(insertedKey)
                ? "Значение справочника добавлено"
                : $"Добавлено значение {_configurationKind}: {insertedKey}");
        }
        catch (Exception ex)
        {
            ShowSaveError(ex, "значение справочника", "Добавление конфигурации", _configurationTableName);
        }
    }

    private async void DeleteConfigurationValue_Click(object sender, RoutedEventArgs e)
    {
        if (_configurationKind == ConfigurationKind.Class || _selectedRow == null) return;
        if (!UserAuthorizationService.Require(Permission.ManageObjects)) return;
        var mutationGeneration = _workspaceGeneration;
        var beforeSnapshot = SnapshotRow(_selectedRow);
        var valueLabel = GetConfigurationKeyValue();
        if (_offlineProject != null)
        {
            if (Configurator.Infrastructure.ModernDialog.Show("Удалить выбранное значение из локального справочника?", "Offline · удаление", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                SessionTestLog.Trace("CRUD", $"operation=DeleteConfigurationValue; result=cancelled; source=offline; table='{_configurationTableName}'; projectId='{_offlineProject?.Manifest.ProjectId}'");
                return;
            }
            var deleteTimer = System.Diagnostics.Stopwatch.StartNew();
            SessionTestLog.Trace("CRUD", $"operation=DeleteConfigurationValue; phase=start; source=offline; table='{_configurationTableName}'; projectId='{_offlineProject?.Manifest.ProjectId}'");
            try
            {
                var source = _offlineSource ?? throw new InvalidOperationException("Локальный источник данных не инициализирован. Проект не изменён.");
                if (!await DeleteOfflineConfigurationValueAsync(source, mutationGeneration))
                {
                    deleteTimer.Stop();
                    SessionTestLog.Trace("CRUD", $"operation=DeleteConfigurationValue; result=cancelled; source=offline; table='{_configurationTableName}'; projectId='{_offlineProject?.Manifest.ProjectId}'");
                    return;
                }
                deleteTimer.Stop();
                SessionTestLog.Trace("CRUD", $"operation=DeleteConfigurationValue; phase=complete; result=success; source=offline; table='{_configurationTableName}'; projectId='{_offlineProject?.Manifest.ProjectId}'; elapsedMs={deleteTimer.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                deleteTimer.Stop();
                SessionTestLog.Trace("CRUD", $"operation=DeleteConfigurationValue; phase=complete; result=error; source=offline; table='{_configurationTableName}'; projectId='{_offlineProject?.Manifest.ProjectId}'; elapsedMs={deleteTimer.ElapsedMilliseconds}; exception={ex}");
                ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
                Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Offline · удаление", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }
        try
        {
            var connectionString = _connection.ToConnectionString(_selectedDatabase);
            var analyzer = new DatabaseArchitectureAnalyzer(connectionString);
            var schema = await analyzer.AnalyzeAsync();
            var table = _configurationTableName;
            var identity = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var primary = await new DatabaseService(connectionString).GetPrimaryKeyColumnsAsync(table);
            var keyColumns = primary.Count > 0 ? primary : new List<string> { _configurationKeyColumn };
            foreach (var key in keyColumns)
                if (_selectedRow.Table.Columns.Contains(key)) identity[key] = _selectedRow[key];

            // Include logical target keys (for example Areas.AreaName) used by dependent relations.
            foreach (var relation in schema.Relations.Where(r => r.TargetTable.Equals(table, StringComparison.OrdinalIgnoreCase)))
                foreach (var column in relation.TargetColumns)
                    if (_selectedRow.Table.Columns.Contains(column) && !identity.ContainsKey(column))
                        identity[column] = _selectedRow[column];

            if (identity.Count == 0) throw new InvalidOperationException("Не удалось определить ключ выбранного значения.");
            var protection = new DeletionProtectionService(connectionString, schema);
            var plan = await protection.BuildPlanAsync(new DeletionRequest { TableName = table, Key = identity });
            if (plan.ValidationErrors.Count > 0)
            {
                Configurator.Infrastructure.ModernDialog.Show("Удаление заблокировано:\n\n" + string.Join("\n", plan.ValidationErrors), "Конфигурация", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            IReadOnlyList<ReferenceReplacement> replacements = Array.Empty<ReferenceReplacement>();
            if (plan.RequiresReplacement)
            {
                var options = new List<DependencyReplacementWindow.DependencyOption>();
                foreach (var hit in plan.Dependencies)
                {
                    var candidates = await protection.GetReplacementCandidatesAsync(hit, identity);
                    if (candidates.Count == 0) throw new InvalidOperationException($"Для зависимости {hit.SourceTable}.{hit.SourceColumnDescription} нет допустимого значения для замены.");
                    var current = hit.Relation.TargetColumns.FirstOrDefault(c => identity.ContainsKey(c));
                    options.Add(new DependencyReplacementWindow.DependencyOption { Hit = hit, CurrentValue = current == null ? "" : identity[current], Candidates = candidates });
                }
                var dialog = new DependencyReplacementWindow(options) { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() != true) return;
                replacements = dialog.Replacements;
            }
            await protection.ExecuteAsync(plan, replacements, (_, _, _) => Task.FromResult(true));
            var description = $"Удалено значение {_configurationKind}: «{valueLabel}»"
                + (plan.RequiresReplacement ? $"; заменено зависимых значений: {replacements.Count}" : "");
            RecordMode3Mutation(table, ChangeJournalOperationKind.Delete, description, beforeSnapshot, null,
                identity, null, mutationGeneration,
                restoreIncludesAllChanges: !plan.RequiresReplacement,
                details: plan.RequiresReplacement
                    ? "Зависимости заменены вместе с удалением. Отмена отключена, потому что журнал не содержит исходные значения всех изменённых строк."
                    : null);
            await RefreshConfigurationLookupsAsync();
            TxtStatus.Text = description;
            ShowActionFeedback(description);
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Удаление конфигурации", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void NewObject_Click(object sender, RoutedEventArgs e)
    {
        if (_configurationKind != ConfigurationKind.Class)
        {
            await AddConfigurationValueAsync();
            return;
        }
        if (!UserAuthorizationService.Require(Permission.AddData)) return;
        var mutationGeneration = _workspaceGeneration;
        if (_offlineProject != null)
        {
            if (_selectedClass?.PrimaryStorage == null) return;
            try
            {
                var tableName = _selectedClass.PrimaryStorage.TableName;
                var schema = _offlineProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
                if (schema == null) throw new InvalidOperationException($"В локальном снимке отсутствует схема {tableName}.");
                var columns = schema.Columns.Select(c => new ColumnInfo { Name = c.Name, DataType = c.DataType, IsIdentity = c.IsIdentity, IsNullable = c.IsNullable, IsComputed = c.IsComputed, MaxLength = c.MaxLength, Precision = c.Precision, Scale = c.Scale, HasDefault = c.HasDefault }).ToList();
                int nextRecord = 1;
                if (_objects.Columns.Contains("Record"))
                    nextRecord = _objects.Rows.Cast<DataRow>().Where(r => r["Record"] != DBNull.Value).Select(r => int.TryParse(Convert.ToString(r["Record"]), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
                var recordColumn = columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
                var plcColumn = columns.FirstOrDefault(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase))?.Name;
                var classNumberColumn = columns.FirstOrDefault(c => new[] { "PLCClassNumber", "PLC_Class_Number", "PLCClasssNumber" }.Contains(c.Name, StringComparer.OrdinalIgnoreCase))?.Name;
                var localLookups = LoadOfflineLookupOptionsForProjectTable(tableName, columns);
                Func<string, CancellationToken, Task<int>> localRecordProvider = null;
                if (!string.IsNullOrWhiteSpace(recordColumn) && !string.IsNullOrWhiteSpace(plcColumn))
                {
                    localRecordProvider = (plcValue, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var max = _offlineProject.LocalRecords
                            .Where(r => r.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                            .Select(r => (Record: r, Plc: r.Values.TryGetPropertyValue(plcColumn, out var pv) ? pv?.ToString() : null,
                                Rec: r.Values.TryGetPropertyValue(recordColumn, out var rv) ? rv?.ToString() : null))
                            .Where(x => string.Equals(x.Plc, plcValue, StringComparison.OrdinalIgnoreCase))
                            .Select(x => int.TryParse(x.Rec, out var n) ? n : 0).DefaultIfEmpty(0).Max();
                        return Task.FromResult(max + 1);
                    };
                }
                var dialog = new AddRowWindow(tableName, columns, nextRecord, recordColumn, null, plcColumn,
                    string.IsNullOrWhiteSpace(classNumberColumn) ? null : _selectedClass.ClassNumber, classNumberColumn,
                    localLookups, scopedRecordProvider: localRecordProvider) { Owner = Window.GetWindow(this) };
                dialog.ShowDialog();
                if (!dialog.Confirmed || dialog.Values == null) return;
                var previousKeys = CaptureOfflineRecordKeys(tableName);
                await RequireCrudSource().CreateRecordAsync(tableName, dialog.Values);
                RecordCreatedMode3Object(tableName, dialog.Values, previousKeys, "Создан объект", mutationGeneration);
                await LoadObjectsAsync();
                RebuildObjectFilters();
                TxtStatus.Text = "Объект добавлен в локальный проект";
                ShowActionFeedback(TxtStatus.Text);
            }
            catch (Exception ex)
            {
                ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
                Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Новый локальный объект", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        if (_selectedClass?.PrimaryStorage == null || _connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        try
        {
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var columns = await db.GetTableColumnsAsync(_selectedClass.PrimaryStorage.TableName);
            if (columns.Count == 0) throw new InvalidOperationException("Не удалось получить структуру таблицы класса.");

            bool hasRecord = columns.Any(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));
            bool hasPlc = columns.Any(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase));
            int nextRecord = !hasPlc && hasRecord ? await NextRecordAsync(db) : 0;
            var lookupOptions = await LoadLookupOptionsForProjectTableAsync(_selectedClass.PrimaryStorage.TableName);
            if (!lookupOptions.ContainsKey("PLC"))
            {
                var plcOptions = await GetOnlinePlcOptionsAsync(db);
                if (plcOptions.Count > 0)
                    lookupOptions["PLC"] = plcOptions.Select(x => new LookupOption { Key = x.Key, Display = x.Title }).ToList();
            }
            string nameField = columns.FirstOrDefault(c => c.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Name
                               ?? columns.FirstOrDefault(c => c.Name.Equals("Tag", StringComparison.OrdinalIgnoreCase))?.Name;
            string classNumberColumn = columns.FirstOrDefault(c => c.Name.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase))?.Name
                ?? columns.FirstOrDefault(c => c.Name.Equals("PLC_Class_Number", StringComparison.OrdinalIgnoreCase))?.Name
                ?? columns.FirstOrDefault(c => c.Name.Equals("PLCClasssNumber", StringComparison.OrdinalIgnoreCase))?.Name;

            var dialog = new AddRowWindow(
                _selectedClass.PrimaryStorage.TableName, columns, nextRecord,
                hasRecord ? columns.First(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase)).Name : "",
                _connection.ToConnectionString(_selectedDatabase),
                hasPlc ? columns.First(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase)).Name : null,
                string.IsNullOrWhiteSpace(classNumberColumn) ? null : _selectedClass.ClassNumber,
                classNumberColumn, lookupOptions) { Owner = Window.GetWindow(this) };

            // Если таблица содержит Name, предлагаем имя класса как удобное начальное значение.
            if (!string.IsNullOrWhiteSpace(nameField))
            {
                // AddRowWindow уже подставляет значения только после открытия; имя пользователь введёт в самом редакторе.
            }
            dialog.ShowDialog();
            if (!dialog.Confirmed || dialog.Values == null || dialog.Values.Count == 0) return;

            var tableName = _selectedClass.PrimaryStorage.TableName;
            await RequireCrudSource().CreateRecordAsync(tableName, dialog.Values);
            RecordCreatedMode3Object(tableName, dialog.Values, new HashSet<string>(StringComparer.Ordinal), "Создан объект", mutationGeneration);
            await LoadObjectsAsync();
            ShowActionFeedback("Объект добавлен");
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Новый объект", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void NewObjectsBatch_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Require(Permission.AddData)) return;
        if (_selectedClass?.PrimaryStorage == null) return;

        var tableName = _selectedClass.PrimaryStorage.TableName;
        bool offline = _offlineProject != null;
        DatabaseService db = null;
        List<ColumnInfo> columns;
        List<(string Key, string Title)> plcOptions;
        try
        {
            if (offline)
            {
                var schema = _offlineProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
                if (schema == null) throw new InvalidOperationException($"В локальном снимке отсутствует схема {tableName}.");
                columns = schema.Columns.Select(c => new ColumnInfo
                {
                    Name = c.Name, DataType = c.DataType, IsIdentity = c.IsIdentity, IsNullable = c.IsNullable,
                    IsComputed = c.IsComputed, MaxLength = c.MaxLength, Precision = c.Precision, Scale = c.Scale, HasDefault = c.HasDefault
                }).ToList();
                plcOptions = await GetOfflinePlcOptionsAsync();
            }
            else
            {
                if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
                db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
                columns = await db.GetTableColumnsAsync(tableName);
                plcOptions = await GetOnlinePlcOptionsAsync(db);
            }

            if (columns.Count == 0) throw new InvalidOperationException("Не удалось получить структуру таблицы класса.");
            var nameColumn = columns.FirstOrDefault(c => c.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                ?? columns.FirstOrDefault(c => c.Name.Equals("Tag", StringComparison.OrdinalIgnoreCase));
            if (nameColumn == null) throw new InvalidOperationException("Для массового создания не найдено поле Name или Tag.");

            string recordColumn = columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name;
            string plcColumn = columns.FirstOrDefault(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase))?.Name;
            string classNumberColumn = columns.FirstOrDefault(c => new[] { "PLCClassNumber", "PLC_Class_Number", "PLCClasssNumber" }.Contains(c.Name, StringComparer.OrdinalIgnoreCase))?.Name;
            var required = columns.Where(c => !c.IsReadOnly && !c.IsIdentity && !c.IsComputed && !c.IsNullable && !c.HasDefault
                && !c.Name.Equals(nameColumn.Name, StringComparison.OrdinalIgnoreCase)
                && !c.Name.Equals(recordColumn, StringComparison.OrdinalIgnoreCase)
                && !c.Name.Equals(plcColumn, StringComparison.OrdinalIgnoreCase)
                && !c.Name.Equals(classNumberColumn, StringComparison.OrdinalIgnoreCase)).ToList();
            if (required.Count > 0)
                throw new InvalidOperationException("Невозможно корректно заполнить обязательные поля автоматически: " + string.Join(", ", required.Select(c => c.Name)));
            if (offline)
            {
                var unknownDefaults = columns.Where(c => !c.IsReadOnly && !c.IsNullable && c.HasDefault
                    && !c.Name.Equals(nameColumn.Name, StringComparison.OrdinalIgnoreCase)
                    && !c.Name.Equals(recordColumn, StringComparison.OrdinalIgnoreCase)
                    && !c.Name.Equals(plcColumn, StringComparison.OrdinalIgnoreCase)
                    && !c.Name.Equals(classNumberColumn, StringComparison.OrdinalIgnoreCase)).ToList();
                if (unknownDefaults.Count > 0)
                    throw new InvalidOperationException("Локальный снимок хранит признак наличия значения по умолчанию, но не само выражение SQL DEFAULT. Чтобы не записать NULL вместо корректного значения, массовое добавление остановлено для полей: " + string.Join(", ", unknownDefaults.Select(c => c.Name)) + ". Создайте новый снимок с поддержкой выражений DEFAULT или заполните эти поля через форму с явными значениями.");
            }

            DataTable uniquenessRows;
            if (offline)
            {
                // Validate against the complete local package, not the UI's paged 5,000-row view.
                uniquenessRows = new DataTable();
                var localTable = _offlineProject.LocalRecords
                    .Where(r => r.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)).ToList();
                var localColumns = columns.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var column in localColumns) uniquenessRows.Columns.Add(column, typeof(string));
                foreach (var record in localTable)
                {
                    var row = uniquenessRows.NewRow();
                    foreach (var kv in record.Values)
                        if (uniquenessRows.Columns.Contains(kv.Key)) row[kv.Key] = kv.Value is null ? DBNull.Value : kv.Value.ToString();
                    uniquenessRows.Rows.Add(row);
                }
            }
            else
            {
                // Online uniqueness checks use all rows in the live table rather than the paged UI cache.
                uniquenessRows = await db.GetAllTableRowsAsync(tableName);
            }

            var existingNames = uniquenessRows.Columns.Contains(nameColumn.Name)
                ? uniquenessRows.Rows.Cast<DataRow>().Select(r => Convert.ToString(r[nameColumn.Name])).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                : new List<string>();
            var existingRecordKeys = new List<string>();
            if (!string.IsNullOrWhiteSpace(recordColumn) && uniquenessRows.Columns.Contains(recordColumn))
            {
                foreach (DataRow existing in uniquenessRows.Rows)
                {
                    var rec = Convert.ToString(existing[recordColumn]);
                    var plc = !string.IsNullOrWhiteSpace(plcColumn) && uniquenessRows.Columns.Contains(plcColumn) ? Convert.ToString(existing[plcColumn]) : "";
                    if (!string.IsNullOrWhiteSpace(rec)) existingRecordKeys.Add($"{plc}|{rec}");
                }
            }

            var dialog = new MultiObjectEditorWindow(_selectedClass.Name, plcOptions, existingNames,
                existingRecordKeys, !string.IsNullOrWhiteSpace(recordColumn), !string.IsNullOrWhiteSpace(plcColumn),
                async rows =>
                {
                    if (offline)
                    {
                        var schema = _offlineSource?.GetSchema(_selectedClass.PrimaryStorage.TableName);
                        var generatedColumns = new HashSet<string>(schema?.Columns.Where(c => c.IsIdentity || c.IsComputed).Select(c => c.Name) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                        foreach (var item in rows)
                        {
                            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                            {
                                [nameColumn.Name] = item.Name
                            };
                            if (!string.IsNullOrWhiteSpace(plcColumn)) values[plcColumn] = item.PlcKey;
                            if (!string.IsNullOrWhiteSpace(recordColumn)) values[recordColumn] = item.Record;
                            if (!string.IsNullOrWhiteSpace(classNumberColumn)) values[classNumberColumn] = _selectedClass.ClassNumber;
                            foreach (var generated in generatedColumns) values.Remove(generated);
                            await RequireCrudSource().CreateRecordAsync(_selectedClass.PrimaryStorage.TableName, values);
                        }
                        await LoadObjectsAsync();
                        RebuildObjectFilters();
                        ApplyObjectFilters();
                        TxtStatus.Text = $"Создано объектов: {rows.Count} · локальный проект";
                        ShowActionFeedback(TxtStatus.Text);
                        return null;
                    }

                    int inserted = 0;
                    foreach (var item in rows)
                    {
                        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            [nameColumn.Name] = item.Name
                        };
                        if (!string.IsNullOrWhiteSpace(plcColumn))
                        {
                            if (!int.TryParse(item.PlcKey, out var plcId))
                            {
                                ShowActionFeedback($"Ошибка: PLC «{item.PlcKey}» не является допустимым ключом.", isError: true);
                                return $"PLC «{item.PlcKey}» не является допустимым ключом. Добавлено до ошибки: {inserted}.";
                            }
                            values[plcColumn] = plcId;
                        }
                        if (!string.IsNullOrWhiteSpace(recordColumn))
                        {
                            if (!int.TryParse(item.Record, out var recordValue))
                            {
                                ShowActionFeedback($"Ошибка: Record «{item.Record}» должен быть целым числом.", isError: true);
                                return $"Record «{item.Record}» должен быть целым числом. Добавлено до ошибки: {inserted}.";
                            }
                            values[recordColumn] = recordValue;
                        }
                        if (!string.IsNullOrWhiteSpace(classNumberColumn)) values[classNumberColumn] = _selectedClass.ClassNumber;
                        try { await RequireCrudSource().CreateRecordAsync(tableName, values); inserted++; }
                        catch (Exception ex)
                        {
                            await LoadObjectsAsync();
                            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
                            return $"Сохранение прервано после {inserted} из {rows.Count} объектов. Уже добавленные записи не отменены слоем данных. Ошибка: {ex.Message}";
                        }
                    }
                    await LoadObjectsAsync();
                    TxtStatus.Text = $"Создано объектов: {inserted}";
                        ShowActionFeedback(TxtStatus.Text);
                    return null;
                })
            { Owner = Window.GetWindow(this) };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Массовое добавление объектов", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<List<(string Key, string Title)>> GetOnlinePlcOptionsAsync(DatabaseService db)
    {
        foreach (var table in new[] { "dbo.PLCs", "dbo.PLC" })
        {
            try
            {
                var columns = await db.GetTableColumnsAsync(table);
                if (columns.Count == 0) continue;
                var data = await db.GetTableDataPageAsync(table, 1, 5000,
                    columns.Take(3).Select(c => c.Name).ToList(), null);
                var keyColumn = data.Columns.Cast<DataColumn>().FirstOrDefault(c =>
                    c.ColumnName.Equals("Record", StringComparison.OrdinalIgnoreCase) ||
                    c.ColumnName.Equals("Id", StringComparison.OrdinalIgnoreCase));
                if (keyColumn == null) continue;
                var displayColumn = data.Columns.Cast<DataColumn>().FirstOrDefault(c =>
                    c.ColumnName.Equals("NameL1", StringComparison.OrdinalIgnoreCase) ||
                    c.ColumnName.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                    c.ColumnName.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase));
                var result = data.Rows.Cast<DataRow>()
                    .Select(r => (Key: Convert.ToString(r[keyColumn]) ?? "",
                        Title: displayColumn == null ? (Convert.ToString(r[keyColumn]) ?? "") : (Convert.ToString(r[displayColumn]) ?? Convert.ToString(r[keyColumn]) ?? "")))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToList();
                if (result.Count > 0) return result;
            }
            catch { /* Try the alternate known PLC table name. */ }
        }
        return new List<(string Key, string Title)>();
    }

    private static object ConvertForColumn(object value, Type targetType)
    {
        if (value == null || value == DBNull.Value) return DBNull.Value;
        var text = Convert.ToString(value);
        if (targetType == typeof(string)) return text ?? string.Empty;
        try { return Convert.ChangeType(value, targetType); } catch { return text ?? string.Empty; }
    }

    private async Task<List<(string Key, string Title)>> GetOfflinePlcOptionsAsync()
    {
        var result = new List<(string Key, string Title)>();
        var table = _offlineProject?.TableSchemas.FirstOrDefault(x => new[] { "dbo.PLC", "dbo.PLCs", "PLC", "PLCs" }.Contains(x.TableName, StringComparer.OrdinalIgnoreCase))?.TableName;
        if (table == null) return new List<(string, string)>();
        var rows = await _offlineReader.ReadRowsAsync(table, 5000);
        var key = rows.Columns.Cast<DataColumn>().FirstOrDefault(c => c.ColumnName.Equals("Record", StringComparison.OrdinalIgnoreCase) || c.ColumnName.Equals("Id", StringComparison.OrdinalIgnoreCase))?.ColumnName;
        var title = rows.Columns.Cast<DataColumn>().FirstOrDefault(c => c.ColumnName.Equals("NameL1", StringComparison.OrdinalIgnoreCase) || c.ColumnName.Equals("Name", StringComparison.OrdinalIgnoreCase) || c.ColumnName.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase))?.ColumnName;
        if (key != null) foreach (DataRow row in rows.Rows) { var k = Convert.ToString(row[key]) ?? ""; if (!string.IsNullOrWhiteSpace(k)) result.Add((k, title != null && row[title] != DBNull.Value ? Convert.ToString(row[title]) ?? k : "PLC " + k)); }
        return result;
    }

    private Dictionary<string, List<LookupOption>> LoadOfflineLookupOptionsForProjectTable(string tableName, IReadOnlyCollection<ColumnInfo> columns)
    {
        var result = new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
        if (_offlineProject == null || _selectedClass == null) return result;
        foreach (var column in columns)
        {
            var field = _selectedClass.Fields.FirstOrDefault(f => f.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase))
                ?? new FieldDefinition { Name = column.Name };
            var options = GetOfflineLookupOptions(field, _selectedClass.Name, tableName);
            if (options.Count > 0 || !string.IsNullOrWhiteSpace(field.LookupTable))
                result[column.Name] = options.ToList();
        }
        return result;
    }

    private async Task<Dictionary<string, List<LookupOption>>> LoadLookupOptionsForProjectTableAsync(string tableName)
    {
        var result = new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
        var lookup = new LookupOptionsService(new DatabaseService(_connection.ToConnectionString(_selectedDatabase)));
        // Не опрашиваем БД для каждого произвольного поля: это было причиной долгого
        // открытия окна нового объекта. Сначала берём явные связи класса, затем только
        // поля, для которых есть известная системная эвристика.
        foreach (var field in _selectedClass.Fields)
        {
            bool candidate = LookupGenerationRuleSelector.Find(LookupGenerationSettingsRuntime.Current, _selectedClass.Name, tableName, field.Name) != null
                || !string.IsNullOrWhiteSpace(field.LookupTable)
                || field.Name.Equals("Area", StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals("AreaName", StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals("Class", StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals("Type", StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals("Unit", StringComparison.OrdinalIgnoreCase);
            if (!candidate) continue;
            try
            {
                var options = await lookup.TryGetAsync(_selectedClass.Name, tableName, field.Name, default, field.LookupTable, field.LookupKeyField, field.LookupDisplayField);
                if (options.Count > 0 || !string.IsNullOrWhiteSpace(field.LookupTable)) result[field.Name] = options.ToList();
            }
            catch { }
        }
        return result;
    }

    private async Task<int> NextRecordAsync(DatabaseService db)
    {
        if (!_objects.Columns.Contains("Record")) return 1;
        int plc = _objects.Columns.Contains("PLC") ? 1 : 0;
        int max = _objects.Rows.Cast<DataRow>().Where(r => !_objects.Columns.Contains("PLC") || r["PLC"] == DBNull.Value || Convert.ToInt32(r["PLC"]) == plc).Select(r => r["Record"] == DBNull.Value ? 0 : Convert.ToInt32(r["Record"])).DefaultIfEmpty(0).Max();
        await Task.CompletedTask;
        return max + 1;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedDatabase) || _connection == null) return;
        if (!await _connection.TestConnectionAsync(_selectedDatabase))
        {
            Configurator.Infrastructure.ModernDialog.Show($"Связь с БД «{_selectedDatabase}» потеряна. Обновление невозможно.", "Объекты проекта", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ConfigureProjectAsync();
        if (_selectedClass != null)
        {
            await LoadActualClassFieldsAsync();
            await LoadObjectsAsync();
        }
    }

    private IEnumerable<TextBox> FindTextBoxes(Panel root)
    {
        foreach (var child in root.Children)
        {
            if (child is TextBox box) yield return box;
            else if (child is Panel panel) foreach (var nested in FindTextBoxes(panel)) yield return nested;
        }
    }

    private void ClearObjectEditor()
    {
        _objectDetailsGeneration++;
        _selectedRow = null;
        _lookupWarnings.Clear();
        _lookupWarningFields.Clear();
        UpdateLookupWarningsButton();
        ObjectEditor.Children.Clear();
        TxtObjectName.Text = "Выберите объект";
        TxtObjectMeta.Text = "";
        TxtObjectContext.Text = "";
        TxtPreviewGlyph.Text = "OBJ";
        TxtPreviewGlyph.Visibility = Visibility.Visible;
        ImgObjectTypePreview.Source = null;
        ImgObjectTypePreview.Visibility = Visibility.Collapsed;
        BtnDelete.IsEnabled = false;
        BtnDuplicate.IsEnabled = false;
        BtnSave.IsEnabled = false;
    }

    private void CancelLoad()
    {
        _loadGeneration++;
        try { _loadCts?.Cancel(); } catch { }
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private Window _objectSearchWindow;
    private Window _objectFilterWindow;

    private void ObjectSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClass == null || _objects == null) return;
        if (_objectSearchWindow != null) { _objectSearchWindow.Activate(); return; }
        var window = new Window { Title = "Поиск объекта", Width = 440, Height = 520, MinWidth = 360, MinHeight = 320, Owner = Window.GetWindow(this), ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = true, Background = (System.Windows.Media.Brush)FindResource("BrushPanel") };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var search = new TextBox { Height = 34, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(9, 5, 9, 5), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Имя объекта" };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var results = new ListBox { BorderThickness = new Thickness(1), Padding = new Thickness(3), DisplayMemberPath = nameof(ObjectItem.Name) };
        panel.Children.Add(results);
        List<ObjectItem> all = BuildObjectNames(_objects, _selectedClass);
        void RefreshResults() { results.ItemsSource = all.Where(o => o.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).Take(500).ToList(); }
        void Choose() { if (results.SelectedItem is not ObjectItem item) return; CmbPlcFilter.SelectedIndex = 0; CmbAreaFilter.SelectedIndex = 0; ApplyObjectFilters(); ObjectList.SelectedItem = ((IEnumerable<ObjectItem>)ObjectList.ItemsSource ?? Array.Empty<ObjectItem>()).FirstOrDefault(o => o.RowIndex == item.RowIndex); ObjectList.ScrollIntoView(ObjectList.SelectedItem); }
        search.TextChanged += (_, _) => RefreshResults(); results.MouseDoubleClick += (_, _) => Choose(); results.KeyDown += (_, ev) => { if (ev.Key == System.Windows.Input.Key.Enter) Choose(); };
        window.Content = panel; window.Closed += (_, _) => _objectSearchWindow = null; _objectSearchWindow = window; RefreshResults(); window.Show();
    }

    private void ObjectFilterWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClass == null) return;
        if (_objectFilterWindow != null) { _objectFilterWindow.Activate(); return; }
        var window = new Window { Title = "Фильтр объектов", Width = 380, Height = 270, MinWidth = 340, MinHeight = 240, Owner = Window.GetWindow(this), ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = true, Background = (System.Windows.Media.Brush)FindResource("BrushPanel") };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "PLC", Margin = new Thickness(0, 0, 0, 4) });
        var plc = new ComboBox { Height = 34, Padding = new Thickness(6, 2, 6, 2), ItemsSource = CmbPlcFilter.ItemsSource, DisplayMemberPath = nameof(FilterOption.Title), SelectedValuePath = nameof(FilterOption.Key), SelectedValue = GetSelectedFilterKey(CmbPlcFilter), Margin = new Thickness(0, 0, 0, 14) }; panel.Children.Add(plc);
        panel.Children.Add(new TextBlock { Text = "Area", Margin = new Thickness(0, 0, 0, 4) });
        var area = new ComboBox { Height = 34, Padding = new Thickness(6, 2, 6, 2), ItemsSource = CmbAreaFilter.ItemsSource, DisplayMemberPath = nameof(FilterOption.Title), SelectedValuePath = nameof(FilterOption.Key), SelectedValue = GetSelectedFilterKey(CmbAreaFilter), Margin = new Thickness(0, 0, 0, 16) }; panel.Children.Add(area);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "Сбросить", MinWidth = 96, Height = 32, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 10, 0) };
        var apply = new Button { Content = "Применить", MinWidth = 108, Height = 32, Padding = new Thickness(12, 0, 12, 0) };
        reset.Click += (_, _) => { CmbPlcFilter.SelectedIndex = 0; CmbAreaFilter.SelectedIndex = 0; ApplyObjectFilters(); plc.SelectedIndex = 0; area.SelectedIndex = 0; };
        apply.Click += (_, _) => { CmbPlcFilter.SelectedItem = plc.SelectedItem; CmbAreaFilter.SelectedItem = area.SelectedItem; ApplyObjectFilters(); };
        buttons.Children.Add(reset); buttons.Children.Add(apply); panel.Children.Add(buttons);
        window.Content = panel; window.Closed += (_, _) => _objectFilterWindow = null; _objectFilterWindow = window; window.Show();
    }


    private enum ConfigurationKind { None, Plc, Area, Class, Separator }

    private sealed record ConfigurationItem(ConfigurationKind Kind, string Key, string Title, bool IsGroup, bool IsSeparator, ClassDefinition Class);

    private sealed class ObjectItem
    {
        public string Name { get; set; } = "";
        public string PlcDisplay { get; set; } = "";
        public string AreaDisplay { get; set; } = "";
        public int RowIndex { get; set; }
    }
}
