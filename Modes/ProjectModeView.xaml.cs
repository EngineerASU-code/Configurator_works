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
using Configurator.Core.Workspace;

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
    private bool _deletingSelection;

    public event EventHandler OfflineProjectChanged;
    public event Action<ChangeJournalEntry> ChangeJournalRequested;
    public IProjectWorkspace ActiveWorkspace => _workspace;

    private readonly MenuItem SaveItem = new() { Header = "Сохранить", IsEnabled = false };
    private readonly MenuItem DeleteObjectItem = new() { Header = "Удалить", IsEnabled = false };
    private readonly MenuItem DeleteConfigurationItem = new() { Header = "Удалить", IsEnabled = false };

    public ProjectModeView()
    {
        InitializeComponent();
        InitializeProgramTools();
        ApplyAuthorizationUi();
        UserAuthorizationService.Changed += (_, __) => Dispatcher.Invoke(ApplyAuthorizationUi);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.N || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control ||
                !ObjectList.IsVisible || !BtnNewObject.IsEnabled) return;
            e.Handled = true;
            if (!e.IsRepeat) NewObject_Click(this, e);
        };
        SaveItem.Click += SaveObject_Click;
        DeleteObjectItem.Click += DeleteSelection_Click;
        DeleteConfigurationItem.Click += DeleteSelection_Click;
        ObjectList.ContextMenu = BuildObjectContextMenu();
        ObjectList.PreviewMouseRightButtonDown += ObjectList_PreviewMouseRightButtonDown;
        ObjectList.MouseDoubleClick += ObjectList_MouseDoubleClick;
        ObjectList.PreviewKeyDown += ObjectList_PreviewKeyDown;
        DeleteObjectItem.IsEnabled = false;
        SaveItem.IsEnabled = false;
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
        BtnNewObject.IsEnabled = DeleteConfigurationItem.IsEnabled = false;
        DeleteObjectItem.IsEnabled = SaveItem.IsEnabled = false;
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
        BtnNewObject.IsEnabled = DeleteConfigurationItem.IsEnabled = false;
        DeleteObjectItem.IsEnabled = SaveItem.IsEnabled = false;
    }

    private void EnsureOfflineClassesMapToSnapshotTables(ConfiguratorProject project)
    {
        var schemas = project.TableSchemas.Where(x => !string.IsNullOrWhiteSpace(x.TableName)).ToList();
        var classes = _architecture.Classes.ToList();
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
                    if (primary != null) { primary.TableName = resolved; }
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
                }
            }
        }

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
            BtnNewObject.IsEnabled = DeleteConfigurationItem.IsEnabled = false;
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
            BtnNewObject.IsEnabled = DeleteConfigurationItem.IsEnabled = false;
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
            .Where(x => x.Available && !x.Id.StartsWith("offline-table-", StringComparison.OrdinalIgnoreCase))
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
            new(ConfigurationKind.Area, "Area", "Area", true, false, null),
            new(ConfigurationKind.Objects, "Objects", "Objects", true, false, null)
        };
        items.AddRange(classes.Select(cls => new ConfigurationItem(
            ConfigurationKind.Class, cls.Name, cls.Name, false, false, cls)));
        var view = new System.Windows.Data.ListCollectionView(items);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ConfigurationItem.GroupTitle)));
        ConfigurationList.ItemsSource = view;
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
                        ? await ProgramManagementEditor.PrepareToLeaveProgramAsync()
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
                ? await ProgramManagementEditor.PrepareToLeaveProgramAsync()
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
        if (kind == ConfigurationKind.Objects)
        {
            _configurationKind = kind;
            _configurationTableName = _configurationKeyColumn = _configurationDisplayColumn = string.Empty;
            _objects = _configurationRows = new DataTable();
            ObjectList.ItemsSource = Array.Empty<ObjectItem>();
            ClearObjectEditor();
            TxtClassTitle.Text = "Objects";
            TxtObjectCount.Text = string.Empty;
            TxtStatus.Text = string.Empty;
            UpdateObjectFilterIndicator();
            ApplyAuthorizationUi();
            await LoadAssemblyListAsync();
            return;
        }
        string selectedConfigurationKey = !string.IsNullOrWhiteSpace(preferredConfigurationKey)
            ? preferredConfigurationKey
            : _configurationKind == kind ? GetConfigurationKeyValue() : string.Empty;
        var workspace = _workspace;
        _configurationKind = kind;
        _configurationTableName = string.Empty;
        _objects = _configurationRows = new DataTable();
        ObjectList.ItemsSource = null;
        ClearObjectEditor();
        BtnNewObject.IsEnabled = DeleteConfigurationItem.IsEnabled = false;
        TxtObjectCount.Text = string.Empty;
        TxtClassTitle.Text = kind == ConfigurationKind.Plc ? "PLC" : "Area";
        UpdateObjectFilterIndicator();
        var names = kind == ConfigurationKind.Plc ? new[] { "dbo.PLC", "dbo.PLCs" } : new[] { "dbo.Areas", "dbo.Area" };
        ProjectTableSchema schema = null;
        try
        {
            foreach (var name in names)
            {
                if (workspace == null) break;
                schema = await workspace.GetSchemaAsync(name);
                if (workspace != _workspace || _configurationKind != kind) return;
                if (schema?.Columns.Count > 0) break;
            }
        }
        catch (Exception ex)
        {
            if (workspace == _workspace && _configurationKind == kind) TxtStatus.Text = Infrastructure.UserErrorMessage.Format(ex);
            return;
        }
        if (schema == null || schema.Columns.Count == 0)
        {
            TxtStatus.Text = $"В проекте отсутствует справочник {kind}.";
            return;
        }
        _configurationTableName = schema.TableName;
        if (_offlineProject != null)
        {
            var localTable = schema.TableName;
            _configurationKeyColumn = kind == ConfigurationKind.Area
                ? schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("Record", StringComparison.OrdinalIgnoreCase)) ?? schema?.KeyColumns.FirstOrDefault() ?? "Record"
                : schema?.KeyColumns.FirstOrDefault() ?? "Record";
            _configurationDisplayColumn = kind == ConfigurationKind.Area
                ? schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaNameL2", StringComparison.OrdinalIgnoreCase))
                    ?? schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaNameL1", StringComparison.OrdinalIgnoreCase))
                    ?? schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("AreaName", StringComparison.OrdinalIgnoreCase))
                    ?? _configurationKeyColumn
                : schema?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("NameL1", StringComparison.OrdinalIgnoreCase) || n.Equals("Name", StringComparison.OrdinalIgnoreCase) || n.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase)) ?? _configurationKeyColumn;
            var rows = await _offlineReader.ReadRowsAsync(localTable, 5000);
            if (workspace != _workspace || _configurationKind != kind) return;
            _configurationRows = rows;
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
            TxtClassTitle.Text = kind == ConfigurationKind.Plc ? "PLC" : "Area";
            TxtObjectCount.Text = $"{items.Count} значений";
            BtnNewObject.Content = "＋  Добавить"; BtnNewObject.IsEnabled = UserAuthorizationService.Has(Permission.ManageObjects);
            DeleteConfigurationItem.Visibility = Visibility.Visible;
            DeleteConfigurationItem.IsEnabled = false;
            DeleteObjectItem.Visibility = Visibility.Collapsed;
            SaveItem.Visibility = Visibility.Visible;
            BtnObjectSearch.IsEnabled = BtnObjectFilter.IsEnabled = false;
            TxtStatus.Text = $"Offline · справочник {kind}: {items.Count} значений";
            ApplyAuthorizationUi();
            return;
        }
        _configurationKind = kind;
        UpdateObjectFilterIndicator();
        _configurationRows = new DataTable();

        // dbo.Areas.Record is the relationship key stored in object tables' Area field.
        // AreaName is a user-facing label and must never replace that key.
        _configurationKeyColumn = "Record";
        _configurationDisplayColumn = "";
        ObjectList.ItemsSource = null;
        ClearObjectEditor();
        BtnNewObject.Content = "＋  Добавить";
        DeleteConfigurationItem.Visibility = Visibility.Visible;
        DeleteConfigurationItem.IsEnabled = false;
        TxtClassTitle.Text = kind == ConfigurationKind.Plc ? "PLC" : "Area";
        TxtObjectCount.Text = "";
        BtnNewObject.IsEnabled = UserAuthorizationService.Has(Permission.ManageObjects);
        DeleteObjectItem.Visibility = Visibility.Collapsed;
        SaveItem.Visibility = Visibility.Visible;
        BtnObjectSearch.IsEnabled = BtnObjectFilter.IsEnabled = false;

        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        try
        {
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var columns = schema.Columns;
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
            var rows = await db.GetTableDataPageAsync(_configurationTableName, 1, 5000, select, null);
            if (workspace != _workspace || _configurationKind != kind) return;
            _configurationRows = rows;
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
            if (workspace != _workspace || _configurationKind != kind) return;
            _configurationTableName = string.Empty;
            BtnNewObject.IsEnabled = false;
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
            BtnNewObject.IsEnabled = DeleteObjectItem.IsEnabled = SaveItem.IsEnabled = false;
            BtnObjectSearch.IsEnabled = BtnObjectFilter.IsEnabled = false;
            return;
        }
        CancelLoad();
        ResetProgramTools();
        _selectedClass = cls;
        _objects = new DataTable();
        ObjectList.SelectedItem = null;
        ObjectList.ItemsSource = Array.Empty<ObjectItem>();
        TxtObjectCount.Text = string.Empty;
        ClearObjectEditor();
        SessionTestLog.Trace("РЕЖИМ3-КЛАСС", $"Selected class='{cls.Name}', classNumber={cls.ClassNumber}, primaryStorage='{cls.PrimaryStorage?.TableName ?? "<null>"}', fieldCount={cls.Fields?.Count ?? 0}, database='{_selectedDatabase}'");
        _configurationKind = ConfigurationKind.Class;
        UpdateObjectFilterIndicator();
        DeleteObjectItem.Visibility = SaveItem.Visibility = Visibility.Visible;
        BtnObjectSearch.IsEnabled = BtnObjectFilter.IsEnabled = true;
        _selectedConfigurationItem = new ConfigurationItem(ConfigurationKind.Class, cls.Name, cls.Name, false, false, cls);
        DeleteConfigurationItem.IsEnabled = false;
        DeleteConfigurationItem.Visibility = Visibility.Collapsed;
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

        var listTable = requestedClass.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase)
            ? "dbo.Matrix_List"
            : requestedClass.PrimaryStorage.TableName;
        try
        {
            if (_offlineProject != null)
            {
                if (_offlineReader == null) _offlineReader = new ClassArchitectureReader(_offlineSource ?? new OfflineProjectDataSource(_offlineProject));
                var mappedTable = (_offlineSource ?? new OfflineProjectDataSource(_offlineProject)).ResolveTableName(listTable);
                var loadedOfflineObjects = await _offlineReader.ReadRowsAsync(mappedTable, 5000, ct);
                if (ct.IsCancellationRequested || generation != _loadGeneration || !ReferenceEquals(requestedClass, _selectedClass) || requestedDatabase != _selectedDatabase) return;
                _objects = loadedOfflineObjects;
                ObjectList.ItemsSource = null;
                RebuildObjectFilters();
                ApplyObjectFilters();
                SelectInitialObjectForPreview();
                TxtStatus.Text = $"Offline · {_selectedClass.Name} · объектов: {_objects.Rows.Count}";
                ApplyAuthorizationUi();
                return;
            }
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var columns = await db.GetTableColumnsAsync(listTable, ct);
            SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", $"Schema response: table='{listTable}', columns={columns.Count}, names=[{string.Join(", ", columns.Select(c => c.Name))}]");
            if (columns.Count == 0) { SessionTestLog.Trace("РЕЖИМ3-ОБЪЕКТЫ", "EARLY RETURN: database returned zero columns"); return; }
            var order = columns.Take(3).Select(x => x.Name).ToList();
            SessionTestLog.Trace("SQL-ЗАПРОС", $"Mode3 objects: SELECT * FROM [{listTable.Replace(".", "].[")}] ORDER BY [{string.Join("], [", order)}] OFFSET 0 ROWS FETCH NEXT 5000 ROWS ONLY; table='{listTable}'");
            var loadedObjects = await db.GetTableDataPageAsync(listTable, 1, 5000, order, null, ct);
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
            ApplyAuthorizationUi();
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
        foreach (var field in new[] { "Name", "Name_L1", "NameL1", "Tag", "Name_L2", "NameL2", "TagName" })
            if (row.Table.Columns.Contains(field) && row[field] != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(row[field])))
                return Convert.ToString(row[field]);
        string plc = row.Table.Columns.Contains("PLC") ? Convert.ToString(row["PLC"]) : "";
        string record = row.Table.Columns.Contains("Record") ? Convert.ToString(row["Record"]) : "";
        return string.IsNullOrWhiteSpace(plc) && string.IsNullOrWhiteSpace(record) ? "Без имени" : $"Без имени · PLC={plc} · Record={record}";
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

    private async void ObjectList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_configurationKind == ConfigurationKind.Objects && e.Key == System.Windows.Input.Key.D &&
            System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            if (!e.IsRepeat && ObjectList.SelectedItems.Count == 1 && ObjectList.SelectedItem is ObjectItem { AssemblyProgram: { } program })
                await EditAssemblyAsync(program, null);
            return;
        }
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
            e.Key is System.Windows.Input.Key.C or System.Windows.Input.Key.V or System.Windows.Input.Key.D && SupportsObjectCopy)
        {
            e.Handled = true;
            if (e.IsRepeat) return;
            if (e.Key == System.Windows.Input.Key.C) CopySelectedObject();
            else if (e.Key == System.Windows.Input.Key.V) PasteCopiedObject();
            else if (BtnNewObject.IsEnabled && (ObjectList.SelectedItems.Count == 1 || _configurationKind == ConfigurationKind.Class && ObjectList.SelectedItems.Count > 1))
                await DuplicateListAsync();
            return;
        }
        var deleteButton = _configurationKind == ConfigurationKind.Class ? DeleteObjectItem : DeleteConfigurationItem;
        Mode3DeleteShortcut.Handle(e, deleteButton.IsEnabled && deleteButton.Visibility == Visibility.Visible, () => DeleteSelection_Click(deleteButton, e));
    }

    private void ObjectList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(ObjectList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is ObjectItem item)
        {
            if (!container.IsSelected) ObjectList.SelectedItem = item;
            e.Handled = true;
        }
    }

    private ContextMenu BuildObjectContextMenu()
    {
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Открыть объект" }; edit.Click += (s, e) => BuildObjectEditor();
        menu.Items.Add(edit);
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            bool classMode = _configurationKind == ConfigurationKind.Class;
            bool single = SupportsObjectCopy && ObjectList.SelectedItems.Count == 1 && _selectedRow != null;
            void Add(string title, bool enabled, RoutedEventHandler action, string gesture = null)
            {
                var item = new MenuItem { Header = title, IsEnabled = enabled, InputGestureText = gesture ?? string.Empty };
                item.Click += action;
                menu.Items.Add(item);
            }
            if (_configurationKind == ConfigurationKind.Objects)
            {
                var program = (ObjectList.SelectedItem as ObjectItem)?.AssemblyProgram;
                Add("Дублировать Object…", ObjectList.SelectedItems.Count == 1 && program != null && CanEditAssembly(true),
                    async (_, _) => await EditAssemblyAsync(program, null), "Ctrl+D");
                Add("Удалить Object…", CanDeleteAssembly, AssemblyDeleteClick);
                return;
            }
            Add("Добавить", BtnNewObject.IsEnabled, NewObject_Click);
            Add("Дублировать", (single || classMode && ObjectList.SelectedItems.Count > 1) && BtnNewObject.IsEnabled, async (_, _) => await DuplicateListAsync(), "Ctrl+D");
            Add("Копировать", single, (_, _) => CopySelectedObject());
            Add("Вставить", SupportsObjectCopy && BtnNewObject.IsEnabled && _copiedObject.HasValue, (_, _) => PasteCopiedObject());
            var delete = classMode ? DeleteObjectItem : DeleteConfigurationItem;
            menu.Items.Add(SaveItem);
            menu.Items.Add(delete);
            menu.Items.Add(edit);
        };
        return menu;
    }

    private async void ObjectList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_configurationKind is not (ConfigurationKind.Plc or ConfigurationKind.Area) || _selectedRow == null) return;
        try
        {
            if (!UserAuthorizationService.Require(Permission.ManageObjects)) return;
            var generation = _workspaceGeneration;
            var selected = _selectedRow;
            var schema = await RequireWorkspace().GetSchemaAsync(_configurationTableName);
            if (generation != _workspaceGeneration || !ReferenceEquals(selected, _selectedRow)) return;
            if (schema?.HasStableKey != true || schema.KeyColumns.Count == 0)
                throw new InvalidOperationException("Для редактирования справочника требуется однозначный ключ записи.");
            var columns = schema.Columns.Select(c => new ColumnInfo { Name = c.Name, DataType = c.DataType, IsIdentity = c.IsIdentity, IsNullable = c.IsNullable, IsComputed = c.IsComputed || c.IsReadOnly, MaxLength = c.MaxLength, Precision = c.Precision, Scale = c.Scale, HasDefault = c.HasDefault }).ToList();
            var initial = _selectedRow.Table.Columns.Cast<DataColumn>().ToDictionary(c => c.ColumnName, c => _selectedRow[c], StringComparer.OrdinalIgnoreCase);
            var dialog = new AddRowWindow(_configurationTableName, columns, 0, _configurationKeyColumn, null, null, null, null, new Dictionary<string, List<LookupOption>>(), initial, true) { Owner = Window.GetWindow(this) };
            dialog.CanApply = () => generation == _workspaceGeneration && ReferenceEquals(selected, _selectedRow);
            await dialog.ShowRowEditorAsync();
            if (!dialog.Confirmed || dialog.Values == null || !dialog.CanApply()) return;
            var keyColumns = schema.KeyColumns;

            var keyValues = keyColumns.ToDictionary(k => k, k => (object?)(_selectedRow.Table.Columns.Contains(k) ? _selectedRow[k] : null), StringComparer.OrdinalIgnoreCase);
            var updateValues = dialog.Values.Where(pair => !keyColumns.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase);
            await UpdateAndJournalAsync(_configurationTableName, keyColumns, keyValues, updateValues, _selectedRow, "Изменено значение справочника");
            await LoadConfigurationValuesAsync(_configurationKind);
            TxtStatus.Text = "Значение справочника изменено";
        }
        catch (Exception ex) { Infrastructure.ModernDialog.Show(Infrastructure.UserErrorMessage.Format(ex), "Редактирование справочника"); }
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
        if (_configurationKind == ConfigurationKind.Objects)
        {
            ShowAssemblySelection();
            return;
        }
        ShowSelectedProgram();
        ShowSelectedMatrix();
        var selected = ObjectList.SelectedItems.Cast<ObjectItem>().ToList();
        if (_configurationKind != ConfigurationKind.Class)
        {
            ClearObjectEditor();
            DeleteConfigurationItem.IsEnabled = selected.Count > 0 && UserAuthorizationService.Has(Permission.ManageObjects);
            DeleteObjectItem.Visibility = Visibility.Collapsed;
            SaveItem.Visibility = Visibility.Visible;
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
        DeleteObjectItem.IsEnabled = true;
        SaveItem.IsEnabled = selected.Count == 1;
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

    private void NavigationPanel_Click(object sender, RoutedEventArgs e)
    {
        bool configuration = ReferenceEquals(sender, ConfigurationHeader) || ReferenceEquals(sender, ConfigurationRestore);
        var panel = configuration ? (FrameworkElement)ConfigurationPanel : ElementsPanel;
        var button = configuration ? ConfigurationRestore : ElementsRestore;
        bool show = panel.Visibility == Visibility.Collapsed;
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        button.Visibility = show ? Visibility.Collapsed : Visibility.Visible;

        bool configOpen = ConfigurationPanel.Visibility == Visibility.Visible;
        bool elementsOpen = ElementsPanel.Visibility == Visibility.Visible;
        Grid.SetColumn(RestoreTabs, configOpen ? 2 : 0);
        RestoreTabs.Visibility = configOpen && elementsOpen ? Visibility.Collapsed : Visibility.Visible;
        ConfigurationColumn.Width = new GridLength(configOpen ? 330 : 32);
        ConfigurationGap.Width = new GridLength(configOpen ? 10 : 5);
        ElementsColumn.Width = new GridLength(elementsOpen ? 390 : configOpen ? 32 : 0);
        ElementsGap.Width = new GridLength(elementsOpen ? 10 : configOpen ? 5 : 0);
        (show ? (configuration ? ConfigurationHeader : ElementsHeader) : button).Focus();
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
        bool matrixCatalog = classMode && _selectedClass?.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase) == true;
        if (BtnNewObject != null) BtnNewObject.IsEnabled = (classMode ? UserAuthorizationService.Has(Permission.AddData) : configMode && !string.IsNullOrEmpty(_configurationTableName) && UserAuthorizationService.Has(Permission.ManageObjects));
        if (DeleteConfigurationItem != null)
        {
            DeleteConfigurationItem.Visibility = configMode ? Visibility.Visible : Visibility.Collapsed;
            DeleteConfigurationItem.IsEnabled = configMode && UserAuthorizationService.Has(Permission.ManageObjects) && ObjectList.SelectedItems.Count > 0;
        }
        if (BtnObjectSearch != null) BtnObjectSearch.IsEnabled = classMode && _selectedClass != null;
        if (BtnObjectFilter != null) BtnObjectFilter.IsEnabled = classMode && _selectedClass != null;
        if (DeleteObjectItem != null) DeleteObjectItem.Visibility = classMode ? Visibility.Visible : Visibility.Collapsed;
        if (SaveItem != null)
        {
            SaveItem.Visibility = classMode || configMode ? Visibility.Visible : Visibility.Collapsed;
            SaveItem.IsEnabled = classMode
                ? hasSelection && UserAuthorizationService.Has(Permission.EditData)
                : configMode && hasSelection && UserAuthorizationService.Has(Permission.ManageObjects);
        }
        if (classMode && _selectedClass?.PrimaryStorage?.TableName is string table &&
            !ProjectWriteGuard.IsDataTableWritable(table))
        {
            BtnNewObject.IsEnabled = SaveItem.IsEnabled =
                DeleteObjectItem.IsEnabled = false;
            return;
        }
        if (matrixCatalog && BtnNewObject != null) BtnNewObject.IsEnabled = false;
        if (matrixCatalog && SaveItem != null) SaveItem.IsEnabled = false;
        if (DeleteObjectItem != null) DeleteObjectItem.IsEnabled = !matrixCatalog && UserAuthorizationService.Has(Permission.DeleteData) && DeleteObjectItem.IsEnabled;
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

    private async Task UpdateAndJournalAsync(string table, IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, object> identity, IReadOnlyDictionary<string, object> values, DataRow row, string description)
    {
        var generation = _workspaceGeneration;
        var before = SnapshotRow(row);
        var after = new Dictionary<string, object>(before, StringComparer.OrdinalIgnoreCase);
        foreach (var field in values) after[field.Key] = field.Value ?? DBNull.Value;
        var newKey = keys.ToDictionary(key => key, key => after[key], StringComparer.OrdinalIgnoreCase);
        await RequireCrudSource().UpdateRecordAsync(table, keys, identity, values);
        RecordMode3Mutation(table, ChangeJournalOperationKind.Update, description, before, after, identity, newKey, generation);
    }

    private async Task CreateConfigurationAndJournalAsync(string table, Dictionary<string, object> values)
    {
        var generation = _workspaceGeneration;
        var session = _crudSession ?? throw new InvalidOperationException("Проект не выбран.");
        var keys = _offlineSource != null
            ? _offlineSource.GetSchema(table)?.KeyColumns ?? new List<string>()
            : await new DatabaseService(_connection.ToConnectionString(_selectedDatabase)).GetPrimaryKeyColumnsAsync(table);
        var after = (await session.CreateRecordsAsync(table, new[] { values })).Single();
        var identity = keys.Count > 0 ? SelectIdentity(after, keys) : after;
        RecordMode3Mutation(table, ChangeJournalOperationKind.Insert, $"Добавлено значение справочника {table}",
            null, after, null, identity, generation);
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
                return !System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(a ?? DBNull.Value, b ?? DBNull.Value);
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
        var changes = kind == ChangeJournalOperationKind.Delete
            ? (relatedChanges ?? Array.Empty<ChangeJournalRowChange>()).Append(change).ToList()
            : new List<ChangeJournalRowChange> { change }.Concat(relatedChanges ?? Array.Empty<ChangeJournalRowChange>()).ToList();
        var identity = identityAfter?.Count > 0 ? identityAfter : identityBefore;
        PublishMode3Mutation(table, journalOperationKind ?? kind, description, changes, identity,
            details ?? (identity?.Count > 0 ? string.Join(", ", identity.Select(x => $"{x.Key}={x.Value}")) : "Идентификатор источником не возвращён"), restoreIncludesAllChanges);
    }

    private void PublishMode3Mutation(string table, ChangeJournalOperationKind kind, string description,
        List<ChangeJournalRowChange> changes, IReadOnlyDictionary<string, object> entityIdentity, string details, bool restoreIncludesAllChanges)
    {
        var offline = _offlineProject != null;
        var server = _connection?.Server ?? "";
        var database = _selectedDatabase ?? "";
        var projectId = offline ? (_offlineProject.Manifest?.ProjectId ?? "") : "online:" + server.Trim().ToLowerInvariant() + "/" + database.Trim().ToLowerInvariant();
        ChangeJournalRequested?.Invoke(new ChangeJournalEntry
        {
            Category = "Объект",
            Source = table ?? "",
            Description = description,
            Details = details,
            OperationKind = kind,
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
            Changes = changes
        });
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
        if (_selectedClass?.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase) == true) return;
        if (IsProgramCardActive)
        {
            await ProgramManagementEditor.SaveProgramCardAsync();
            return;
        }
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
                SaveItem.IsEnabled = false;
                return;
            }

            var tableName = _configurationTableName;
            var selectedKey = GetConfigurationKeyValue();
            SessionTestLog.Trace("РЕЖИМ3-СОХРАНЕНИЕ", $"Updating table='{tableName}'; fields={string.Join(",", updates.Keys)}; offline={_offlineProject != null}");
            await UpdateAndJournalAsync(tableName, keyColumns, keyValues, updates, row, $"Изменено значение справочника «{selectedKey}»");
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
            System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control ||
            !ObjectActionsToolbar.IsVisible || SaveItem.Visibility != Visibility.Visible || !SaveItem.IsEnabled || e.IsRepeat) return;
        SaveObject_Click(SaveItem, e);
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

    private async void DeleteSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_deletingSelection || sender is not MenuItem { Visibility: Visibility.Visible, IsEnabled: true }) return;
        _deletingSelection = true;
        try
        {
            bool configuration = ReferenceEquals(sender, DeleteConfigurationItem);
            if (configuration && ObjectList.SelectedItems.Count == 1) await DeleteConfigurationValueAsync();
            else await DeleteObjectAsync(configuration);
        }
        finally { _deletingSelection = false; }
    }

    private async Task DeleteObjectAsync(bool configuration)
    {
        if (!UserAuthorizationService.Require(configuration ? Permission.ManageObjects : Permission.DeleteData)) return;
        if (ObjectList.SelectedItems.Count == 0 || (!configuration && _selectedClass?.PrimaryStorage == null)) return;
        var selected = ObjectList.SelectedItems.Cast<ObjectItem>().ToList();
        var mutationGeneration = _workspaceGeneration;
        var tableName = configuration ? _configurationTableName : _selectedClass.PrimaryStorage.TableName;
        var data = configuration ? _configurationRows : _objects;
        var rows = selected.Where(item => item.RowIndex >= 0 && item.RowIndex < data.Rows.Count)
            .Select(item => SnapshotRow(data.Rows[item.RowIndex])).ToArray();
        if (rows.Length != selected.Count) return;
        if (rows.Length == 1 && tableName.Equals(ProgramManagementRepository.ProgramTable, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteProgramFromListAsync(rows[0], mutationGeneration);
            return;
        }
        var names = selected.Select(x => x.Name).ToList();
        var message = selected.Count == 1
            ? $"Удалить объект «{names[0]}»?"
            : $"Удалить выбранные объекты ({selected.Count} шт.)?";
        if (Configurator.Infrastructure.ModernDialog.Show(message, "Удаление объекта", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        void RecordDeletedObjects(List<(Dictionary<string, object> Values, Dictionary<string, object> Identity)> deletedRows,
            List<ChangeJournalRowChange> dependents)
        {
            var changes = new List<ChangeJournalRowChange>(dependents);
            foreach (var row in deletedRows.SkipLast(1))
                changes.Add(new ChangeJournalRowChange { TableName = tableName, Kind = ChangeJournalOperationKind.Delete,
                    Before = JournalValues(row.Values), IdentityBefore = JournalValues(row.Identity) });
            var last = deletedRows[^1];
            var description = $"Удалено объектов: {deletedRows.Count}" +
                (dependents.Count == 0 ? "" : $" · заменено ссылок в строках: {dependents.Count}");
            RecordMode3Mutation(tableName, ChangeJournalOperationKind.Delete, description, last.Values, null,
                last.Identity, null, mutationGeneration, relatedChanges: changes,
                journalOperationKind: dependents.Count == 0 ? ChangeJournalOperationKind.Delete : ChangeJournalOperationKind.Composite);
        }
        try
        {
            if (mutationGeneration != _workspaceGeneration) return;
            if (_offlineSource != null)
            {
                var deleteTimer = System.Diagnostics.Stopwatch.StartNew();
                var localSchema = _offlineSource?.GetSchema(tableName);
                var keyColumns = localSchema?.KeyColumns;
                if (localSchema?.HasStableKey != true || keyColumns.Count == 0) throw new InvalidOperationException("В локальном снимке не сохранён ключ для удаления записей.");
                var source = _offlineSource;
                var sourceRecords = source.GetRecords(tableName);
                var targetRecords = rows.Select(snapshot =>
                {
                    var identity = SelectIdentity(snapshot, keyColumns);
                    return sourceRecords.SingleOrDefault(record => identity.All(key =>
                        record.Values.TryGetPropertyValue(key.Key, out var value) &&
                        OfflineObjectDeletionGuard.ValuesEqual(value, key.Value)))
                        ?? throw new InvalidOperationException("Не удалось сопоставить выбранный объект со снимком. Удаление отменено.");
                }).ToList();
                var targetValues = targetRecords.Select(record => record.Values.ToDictionary(pair => pair.Key,
                    pair => (object?)(pair.Value?.DeepClone()), StringComparer.OrdinalIgnoreCase)).ToList();
                if (configuration)
                {
                    var relations = BuildOfflineDeletionRelations(source, tableName);
                    var blockedTables = targetRecords.SelectMany(target =>
                    {
                        var key = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (var column in relations.SelectMany(r => r.TargetColumns).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (!TryReadProjectValue(target, localSchema, column, out var value))
                                throw new InvalidOperationException($"В записи отсутствует поле связи «{column}».");
                            key[column] = value;
                        }
                        return FindOfflineDependents(source, target, key, relations);
                    }).Select(d => d.Hit.SourceTable).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToArray();
                    if (blockedTables.Length > 0)
                        throw new InvalidOperationException("Связанные значения удаляйте по одному для выбора замены. Таблицы: " + string.Join(", ", blockedTables));
                }
                var replacementOptions = OfflineObjectDeletionGuard.BuildOptions(source, tableName, targetValues);
                if (replacementOptions.Count > 0 && selected.Count > 1)
                    throw new InvalidOperationException("Объекты со связанными строками нужно удалять по одному, чтобы проверить выбранные замены.");
                IReadOnlyList<ReferenceReplacement> replacements = Array.Empty<ReferenceReplacement>();
                if (replacementOptions.Count > 0)
                {
                    var dialog = new DependencyReplacementWindow(replacementOptions, requireExistingCandidates: true)
                    { Owner = Window.GetWindow(this) };
                    if (dialog.ShowDialog() != true) return;
                    replacements = dialog.Replacements;
                    if (replacements.Count != replacementOptions.Count)
                        throw new InvalidOperationException("Не удалось сопоставить варианты замены со связанными строками. Удаление отменено.");
                }

                var keyValues = new List<IReadOnlyDictionary<string, object?>>();
                var deletedSnapshots = new List<(Dictionary<string, object> Values, Dictionary<string, object> Identity)>();
                foreach (var snapshot in rows)
                {
                    var identity = SelectIdentity(snapshot, keyColumns);
                    deletedSnapshots.Add((snapshot, identity));
                    keyValues.Add(identity.ToDictionary(pair => pair.Key,
                        pair => pair.Value == DBNull.Value ? null : pair.Value, StringComparer.OrdinalIgnoreCase));
                }
                var replacementGroups = replacements.Select((replacement, index) =>
                {
                    var option = replacementOptions[index];
                    var dependent = source.GetRecords(option.Hit.SourceTable).SingleOrDefault(record =>
                        option.Hit.SourceKey.All(key => record.Values.TryGetPropertyValue(key.Key, out var value) &&
                            OfflineObjectDeletionGuard.ValuesEqual(value, key.Value)))
                        ?? throw new InvalidOperationException("Связанная строка для замены больше не найдена.");
                    return (Replacement: replacement, Dependent: dependent, Option: option);
                }).GroupBy(item => item.Dependent).ToList();
                var relatedChanges = new List<ChangeJournalRowChange>();
                foreach (var group in replacementGroups)
                {
                    var first = group.First();
                    var dependentSchema = source.GetSchema(first.Replacement.SourceTable)
                        ?? throw new InvalidOperationException($"В локальном снимке отсутствует схема «{first.Replacement.SourceTable}».");
                    var before = SnapshotProjectRecord(first.Dependent, dependentSchema);
                    var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in group)
                    {
                        foreach (var expected in item.Replacement.ExpectedOldValues)
                            if (!item.Dependent.Values.TryGetPropertyValue(expected.Key, out var actual) ||
                                !OfflineObjectDeletionGuard.ValuesEqual(actual, expected.Value))
                                throw new InvalidOperationException("Связанная строка изменилась после выбора замены. Удаление отменено.");
                        if (item.Replacement.SourceColumns.Count != item.Replacement.NewValues.Count)
                            throw new InvalidOperationException("Количество полей и значений в замене не совпадает.");
                        for (var i = 0; i < item.Replacement.SourceColumns.Count; i++)
                            AddOfflineReplacementAssignment(updates, item.Replacement.SourceColumns[i], item.Replacement.NewValues[i]);
                        foreach (var assignment in item.Replacement.AdditionalAssignments)
                            AddOfflineReplacementAssignment(updates, assignment.Key, assignment.Value);
                    }
                    var after = new Dictionary<string, object>(before, StringComparer.OrdinalIgnoreCase);
                    foreach (var update in updates) after[update.Key] = update.Value ?? DBNull.Value;
                    var identityBefore = ReadStableIdentity(first.Dependent, dependentSchema);
                    var identityAfter = IdentityFromValues(after, dependentSchema);
                    if (identityBefore.Count == 0 || identityAfter.Count == 0)
                        throw new InvalidOperationException($"Не удалось сохранить ключ связанной строки «{first.Replacement.SourceTable}» в журнале.");
                    relatedChanges.Add(new ChangeJournalRowChange
                    {
                        TableName = first.Replacement.SourceTable,
                        Kind = ChangeJournalOperationKind.Update,
                        Before = JournalValues(before),
                        After = JournalValues(after),
                        IdentityBefore = JournalValues(identityBefore),
                        IdentityAfter = JournalValues(identityAfter)
                    });
                }

                await source.ExecuteMutationTransactionAsync(async () =>
                {
                    foreach (var group in replacementGroups)
                    {
                        var first = group.First();
                        var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in group)
                        {
                            foreach (var expected in item.Replacement.ExpectedOldValues)
                                if (!item.Dependent.Values.TryGetPropertyValue(expected.Key, out var actual) ||
                                    !OfflineObjectDeletionGuard.ValuesEqual(actual, expected.Value))
                                    throw new InvalidOperationException("Связанная строка изменилась после выбора замены. Удаление отменено.");
                            for (var i = 0; i < item.Replacement.SourceColumns.Count; i++)
                                AddOfflineReplacementAssignment(updates, item.Replacement.SourceColumns[i], item.Replacement.NewValues[i]);
                            foreach (var assignment in item.Replacement.AdditionalAssignments)
                                AddOfflineReplacementAssignment(updates, assignment.Key, assignment.Value);
                        }
                        await source.UpdateRecordAsync(first.Replacement.SourceTable,
                            first.Replacement.KeyColumns,
                            first.Replacement.KeyColumns.Select((column, index) => (column, first.Replacement.KeyValues[index]))
                                .ToDictionary(pair => pair.column, pair => (object?)pair.Item2, StringComparer.OrdinalIgnoreCase), updates);
                    }
                    var removed = await RequireCrudSource().DeleteRecordsAsync(tableName, keyColumns, keyValues);
                    if (removed != keyValues.Count)
                        throw new InvalidOperationException("Состав выбранных строк изменился. Удаление отменено.");
                });
                int deleted = keyValues.Count;
                RecordDeletedObjects(deletedSnapshots, relatedChanges);
                if (configuration) await RefreshConfigurationLookupsAsync();
                else await LoadObjectsAsync();
                SessionTestLog.Trace("РЕЖИМ3-УДАЛЕНИЕ", $"Offline delete complete; selected={selected.Count}, deleted={deleted}, table='{tableName}', rowsAfter={_objects.Rows.Count}, totalMs={deleteTimer.ElapsedMilliseconds}");
                TxtStatus.Text = $"Объекты удалены из локального проекта: {deleted}";
                ShowActionFeedback($"Удалено объектов: {deleted}");
                return;
            }
            var connectionString = _connection.ToConnectionString(_selectedDatabase);
            var analyzer = new DatabaseArchitectureAnalyzer(connectionString);
            var schema = await analyzer.AnalyzeAsync();
            var protection = new DeletionProtectionService(connectionString, schema);
            var verifiedKey = await new DatabaseService(connectionString).GetPrimaryKeyColumnsAsync(tableName);
            if (verifiedKey.Count == 0)
                throw new InvalidOperationException("Для удаления требуется подтверждённый первичный или уникальный ключ.");
            var deletions = new List<(DeletionExecutionItem Item, Dictionary<string, object> Before, Dictionary<string, object> Identity)>();
            foreach (var beforeSnapshot in rows)
            {
                var identity = SelectIdentity(beforeSnapshot, verifiedKey);
                var relationColumns = schema.Relations.Where(r => r.TargetTable.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(r => r.TargetColumns).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var column in relationColumns)
                    if (beforeSnapshot.TryGetValue(column, out var value))
                        identity.TryAdd(column, value);

                var plan = await protection.BuildPlanAsync(new DeletionRequest
                {
                    TableName = tableName,
                    Key = identity,
                    ExpectedValues = beforeSnapshot
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
                    if (selected.Count > 1)
                        throw new InvalidOperationException("Связанные значения удаляйте по одному для выбора замены. Таблицы: " + string.Join(", ", plan.Dependencies.Select(hit => hit.SourceTable).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t)));
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

                deletions.Add((new DeletionExecutionItem(plan, replacements), beforeSnapshot, identity));
            }

            if (deletions.Count == 0 || mutationGeneration != _workspaceGeneration) return;
            var dependentChanges = await MainWindow.CaptureReplacementChangesAsync(connectionString,
                deletions.SelectMany(x => x.Item.Replacements).ToList());
            await protection.ExecuteBatchAsync(deletions.Select(x => x.Item).ToArray(),
                (_, _, _) => Task.FromResult(true));
            RecordDeletedObjects(deletions.Select(x => (x.Before, x.Identity)).ToList(), dependentChanges);
            if (configuration) await RefreshConfigurationLookupsAsync();
            else await LoadObjectsAsync();
            TxtStatus.Text = $"Удалено объектов: {deletions.Count}";
            ShowActionFeedback(TxtStatus.Text);
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Удаление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Dictionary<string, object> SelectIdentity(
        IReadOnlyDictionary<string, object> snapshot, IEnumerable<string> columns) =>
        columns.ToDictionary(column => column, column => snapshot.TryGetValue(column, out var value)
            ? value
            : throw new InvalidOperationException($"В загруженных данных отсутствует ключевой столбец «{column}»."),
            StringComparer.OrdinalIgnoreCase);

    private static void AddOfflineReplacementAssignment(IDictionary<string, object?> updates, string column, object? value)
    {
        if (updates.TryGetValue(column, out var previous) &&
            !OfflineObjectDeletionGuard.ValuesEqual(ProjectKeyCodec.ToNode(previous), value))
            throw new InvalidOperationException($"Для поля «{column}» выбраны разные замены. Удаление отменено.");
        updates[column] = value;
    }

    private string GetConfigurationKeyValue()
    {
        if (_selectedRow == null || string.IsNullOrWhiteSpace(_configurationKeyColumn) || !_selectedRow.Table.Columns.Contains(_configurationKeyColumn)) return "";
        return _selectedRow[_configurationKeyColumn] == DBNull.Value ? "" : Convert.ToString(_selectedRow[_configurationKeyColumn]) ?? "";
    }

    private async Task AddConfigurationValueAsync(DataRow template = null)
    {
        if (!UserAuthorizationService.Require(Permission.ManageObjects)) return;
        var generation = _workspaceGeneration;
        var table = _configurationTableName;
        var initial = template == null ? null : SnapshotRow(template);
        initial?.Remove("Record");
        initial?.Remove(_configurationKeyColumn);
        bool CanAdd() => generation == _workspaceGeneration && table == _configurationTableName;
        if (_offlineProject != null)
        {
            try
            {
                var schema = _offlineProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(_configurationTableName, StringComparison.OrdinalIgnoreCase));
                if (schema == null) throw new InvalidOperationException($"В снимке отсутствует схема {_configurationTableName}.");
                var columns = schema.Columns.Select(c => new ColumnInfo { Name = c.Name, DataType = c.DataType, IsIdentity = c.IsIdentity, IsNullable = c.IsNullable, IsComputed = c.IsComputed || c.IsReadOnly, MaxLength = c.MaxLength, Precision = c.Precision, Scale = c.Scale, HasDefault = c.HasDefault }).ToList();
                var recordColumn = columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
                int nextRecord = 1;
                if (!string.IsNullOrWhiteSpace(recordColumn))
                {
                    var suggestion = new ScopedRecordSuggestion();
                    foreach (var record in _offlineSource.GetRecords(_configurationTableName))
                        suggestion.Add(ProjectClassCatalog.Read(record, recordColumn));
                    nextRecord = suggestion.GetValue();
                }
                var lookups = OfflineLookupOptionsService.Build(_offlineProject, _configurationTableName, columns);
                var dialog = new AddRowWindow(_configurationTableName, columns, nextRecord, recordColumn, lookupOptions: lookups, initialValues: initial)
                { Owner = Window.GetWindow(this) };
                if (!CanAdd()) return;
                dialog.CanApply = CanAdd;
                await dialog.ShowRowEditorAsync();
                if (!CanAdd() || !dialog.Confirmed || dialog.Values == null || dialog.Values.Count == 0) return;
                RequireExplicitOfflineDefaults(columns, dialog.Values.Keys);
                await CreateConfigurationAndJournalAsync(_configurationTableName, dialog.Values);
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
                nextRecord = await db.GetFreeScopedRecordValueAsync(_configurationTableName, recordColumn, null, null, default);
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
            var dialog = new AddRowWindow(_configurationTableName, columns, nextRecord, recordColumn, _connection.ToConnectionString(_selectedDatabase), null, null, null, lookups, initialValues: initial)
            { Owner = Window.GetWindow(this) };
            if (!CanAdd()) return;
            dialog.CanApply = CanAdd;
            await dialog.ShowRowEditorAsync();
            if (!CanAdd() || !dialog.Confirmed || dialog.Values == null || dialog.Values.Count == 0) return;
            await CreateConfigurationAndJournalAsync(_configurationTableName, dialog.Values);
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

    private async Task DeleteConfigurationValueAsync()
    {
        if (_configurationKind is not (ConfigurationKind.Plc or ConfigurationKind.Area) || _selectedRow == null) return;
        if (!UserAuthorizationService.Require(Permission.ManageObjects)) return;
        var mutationGeneration = _workspaceGeneration;
        var selectedRow = _selectedRow;
        var beforeSnapshot = SnapshotRow(selectedRow);
        var valueLabel = GetConfigurationKeyValue();
        var kind = _configurationKind;
        var table = _configurationTableName;
        var keyColumn = _configurationKeyColumn;
        if (Configurator.Infrastructure.ModernDialog.Show(Window.GetWindow(this),
                $"Удалить значение {kind} «{valueLabel}»?", "Удаление значения справочника",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (mutationGeneration != _workspaceGeneration || !ReferenceEquals(selectedRow, _selectedRow)) return;
        if (_offlineProject != null)
        {
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
            var identity = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var primary = await new DatabaseService(connectionString).GetPrimaryKeyColumnsAsync(table);
            var keyColumns = primary.Count > 0 ? primary : new List<string> { keyColumn };
            foreach (var key in keyColumns)
                if (beforeSnapshot.TryGetValue(key, out var value)) identity[key] = value;

            // Include logical target keys (for example Areas.AreaName) used by dependent relations.
            foreach (var relation in schema.Relations.Where(r => r.TargetTable.Equals(table, StringComparison.OrdinalIgnoreCase)))
                foreach (var column in relation.TargetColumns)
                    if (beforeSnapshot.TryGetValue(column, out var value))
                        identity.TryAdd(column, value);

            if (identity.Count == 0) throw new InvalidOperationException("Не удалось определить ключ выбранного значения.");
            var protection = new DeletionProtectionService(connectionString, schema);
            var plan = await protection.BuildPlanAsync(new DeletionRequest { TableName = table, Key = identity, ExpectedValues = beforeSnapshot });
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
            var related = await MainWindow.CaptureReplacementChangesAsync(connectionString, replacements);
            if (mutationGeneration != _workspaceGeneration) return;
            await protection.ExecuteAsync(plan, replacements, (_, _, _) => Task.FromResult(true));
            var description = $"Удалено значение {kind}: «{valueLabel}»"
                + (plan.RequiresReplacement ? $"; заменено зависимых значений: {replacements.Count}" : "");
            RecordMode3Mutation(table, ChangeJournalOperationKind.Delete, description, beforeSnapshot, null,
                identity, null, mutationGeneration,
                relatedChanges: related, journalOperationKind: related.Count == 0 ? ChangeJournalOperationKind.Delete : ChangeJournalOperationKind.Composite);
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
        if (_selectedClass?.PrimaryStorage == null) return;
        var selectedClass = _selectedClass;
        var mutationGeneration = _workspaceGeneration;
        var tableName = selectedClass.PrimaryStorage.TableName;
        var offlineSource = _offlineSource;
        var connectionString = offlineSource == null ? _connection?.ToConnectionString(_selectedDatabase) : null;
        try
        {
            var crud = RequireCrudSource();
            var db = connectionString == null ? null : new DatabaseService(connectionString);
            var columns = await ReadObjectColumnsAsync(tableName, db);
            var recordColumn = columns.FirstOrDefault(c => !c.IsIdentity && !c.IsReadOnly && c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
            var plcColumn = columns.FirstOrDefault(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase))?.Name;
            var keys = offlineSource != null ? offlineSource.GetSchema(tableName)?.KeyColumns ?? new List<string>()
                : await db.GetPrimaryKeyColumnsAsync(tableName);
            var recordScopeClass = ProjectClassCatalog.FindRecordClassColumn(keys);
            var classNumberColumn = recordScopeClass ?? columns.FirstOrDefault(c => new[] { "PLCClassNumber", "PLC_Class_Number", "PLCClasssNumber" }.Contains(c.Name, StringComparer.OrdinalIgnoreCase))?.Name;
            var lookupOptions = await LoadObjectLookupOptionsAsync(selectedClass, columns, db);
            var nameColumn = columns.FirstOrDefault(c => c.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                ?? columns.FirstOrDefault(c => c.Name.Equals("Tag", StringComparison.OrdinalIgnoreCase));
            var initialValues = nameColumn == null ? null : new Dictionary<string, object> { [nameColumn.Name] = selectedClass.Name };

            IEnumerable<string> LocalRecordNumbers(string plc)
            {
                return offlineSource.GetRecords(tableName)
                    .Where(row => (plcColumn == null || Mode3DuplicateValues.SameScopeValue(row.Values[plcColumn]?.ToString(), plc)) &&
                        (recordScopeClass == null || Mode3DuplicateValues.SameScopeValue(row.Values[recordScopeClass]?.ToString(), selectedClass.ClassNumber)))
                    .Select(row => row.Values[recordColumn]?.ToString())
                    .OfType<string>();
            }

            int SuggestOfflineRecord(string plc)
            {
                var suggestion = new ScopedRecordSuggestion();
                foreach (var record in LocalRecordNumbers(plc))
                    suggestion.Add(record);
                return suggestion.GetValue();
            }

            int nextRecord = 0;
            if (recordColumn.Length != 0 && plcColumn == null)
            {
                if (offlineSource != null)
                    nextRecord = SuggestOfflineRecord(null);
                else
                    nextRecord = await db.GetFreeScopedRecordValueAsync(tableName, recordColumn, null, null, default, recordScopeClass, selectedClass.ClassNumber);
            }

            var addressWorkspace = RequireWorkspace();
            var dialog = new AddRowWindow(tableName, columns, nextRecord, recordColumn, connectionString,
                plcColumn, classNumberColumn == null ? null : selectedClass.ClassNumber, classNumberColumn, lookupOptions,
                initialValues: initialValues,
                scopedRecordProvider: offlineSource == null ? null : (plc, _, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(SuggestOfflineRecord(plc));
                },
                recordExistsProvider: offlineSource == null ? null : (plc, _, record, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(LocalRecordNumbers(plc).Any(value => Mode3DuplicateValues.SameScopeValue(value, record)));
                },
                preferFreeScopedRecord: plcColumn != null && recordColumn.Length != 0,
                recordScopeClassColumn: recordScopeClass,
                freeAddressProvider: (plc, cls, ct) => PlcAddressCatalog.SuggestAddressesAsync(addressWorkspace, tableName, plc,
                    cls ?? selectedClass.ClassNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ct),
                msTypes: cls => MsTypeCatalog.ReadAsync(addressWorkspace, tableName, cls))
            { Owner = Window.GetWindow(this) };

            if (mutationGeneration != _workspaceGeneration || !ReferenceEquals(selectedClass, _selectedClass)) return;
            dialog.CanApply = () => mutationGeneration == _workspaceGeneration && ReferenceEquals(selectedClass, _selectedClass);
            await dialog.ShowRowEditorAsync();
            if (!dialog.Confirmed || dialog.Values == null || dialog.Values.Count == 0) return;

            if (offlineSource != null) RequireExplicitOfflineDefaults(columns, dialog.Values.Keys);
            var snapshot = (await ((IProjectBatchCrudDataSource)crud).CreateRecordsAsync(tableName, new[] { dialog.Values })).Single();
            var identity = keys.Count > 0 ? SelectIdentity(snapshot, keys) : snapshot;
            RecordMode3Mutation(tableName, ChangeJournalOperationKind.Insert, "Создан объект", null, snapshot, null, identity, mutationGeneration);
            if (mutationGeneration != _workspaceGeneration) return;
            await LoadObjectsAsync();
            ShowActionFeedback("Объект добавлен");
        }
        catch (Exception ex)
        {
            var error = Configurator.Infrastructure.UserErrorMessage.Format(ex);
            ShowActionFeedback($"Ошибка: {error}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(error, "Новый объект", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void RequireExplicitOfflineDefaults(IEnumerable<ColumnInfo> columns, IEnumerable<string> assigned)
    {
        var missing = columns.Where(c => c.HasDefault && !c.IsReadOnly && !c.IsIdentity &&
            !assigned.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).Select(c => c.Name).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Для локального проекта укажите явные значения полей с SQL DEFAULT: " + string.Join(", ", missing));
    }

    private async Task<List<ColumnInfo>> ReadObjectColumnsAsync(string tableName, DatabaseService db)
    {
        var columns = db != null
            ? await db.GetTableColumnsAsync(tableName)
            : (_offlineSource?.GetSchema(tableName)?.Columns.Select(c => new ColumnInfo
            {
                Name = c.Name, DataType = c.DataType, IsIdentity = c.IsIdentity, IsNullable = c.IsNullable,
                IsComputed = c.IsComputed || c.IsReadOnly, MaxLength = c.MaxLength, Precision = c.Precision, Scale = c.Scale, HasDefault = c.HasDefault
            }).ToList() ?? new List<ColumnInfo>());
        if (columns.Count == 0)
            throw new InvalidOperationException($"Не удалось получить структуру таблицы «{tableName}».");
        return columns;
    }

    private Task<IReadOnlyList<LookupOption>> ReadObjectLookupAsync(ClassDefinition selectedClass, FieldDefinition field, LookupOptionsService onlineLookup)
    {
        var tableName = selectedClass.PrimaryStorage.TableName;
        return onlineLookup == null
            ? Task.FromResult(GetOfflineLookupOptions(field, selectedClass.Name, tableName))
            : onlineLookup.TryGetAsync(selectedClass.Name, tableName, field.Name, default,
                field.LookupTable, field.LookupKeyField, field.LookupDisplayField);
    }

    private async Task<Dictionary<string, List<LookupOption>>> LoadObjectLookupOptionsAsync(
        ClassDefinition selectedClass, IReadOnlyCollection<ColumnInfo> columns, DatabaseService? db)
    {
        var result = new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
        var tableName = selectedClass.PrimaryStorage.TableName;
        var onlineLookup = db == null ? null : new LookupOptionsService(db);
        foreach (var column in columns)
        {
            var field = selectedClass.Fields.FirstOrDefault(f => f.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase))
                ?? new FieldDefinition { Name = column.Name };
            bool candidate = !string.IsNullOrWhiteSpace(field.LookupTable)
                || LookupGenerationRuleSelector.Find(LookupGenerationSettingsRuntime.Current, selectedClass.Name, tableName, field.Name) != null
                || new[] { "Area", "AreaName", "PLC", "Class", "PLCClassNumber", "PLCClasssNumber", "Type", "Unit" }
                    .Contains(field.Name, StringComparer.OrdinalIgnoreCase);
            if (!candidate) continue;
            var options = await ReadObjectLookupAsync(selectedClass, field, onlineLookup);
            if (options.Count > 0 || !string.IsNullOrWhiteSpace(field.LookupTable))
                result[column.Name] = options.ToList();
        }
        return result;
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
        DeleteObjectItem.IsEnabled = false;
        SaveItem.IsEnabled = false;
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
        var apply = new Button { Content = "Применить", IsDefault = true, MinWidth = 108, Height = 32, Padding = new Thickness(12, 0, 12, 0) };
        reset.Click += (_, _) => { CmbPlcFilter.SelectedIndex = 0; CmbAreaFilter.SelectedIndex = 0; ApplyObjectFilters(); plc.SelectedIndex = 0; area.SelectedIndex = 0; };
        apply.Click += (_, _) => { CmbPlcFilter.SelectedItem = plc.SelectedItem; CmbAreaFilter.SelectedItem = area.SelectedItem; ApplyObjectFilters(); };
        buttons.Children.Add(reset); buttons.Children.Add(apply); panel.Children.Add(buttons);
        window.Content = panel; window.Closed += (_, _) => _objectFilterWindow = null; _objectFilterWindow = window; window.Show();
    }


    private enum ConfigurationKind { None, Plc, Area, Class, Separator, Objects }

    private sealed record ConfigurationItem(ConfigurationKind Kind, string Key, string Title, bool IsGroup, bool IsSeparator, ClassDefinition Class)
    {
        public bool IsObjects => Kind == ConfigurationKind.Objects;
        public string GroupTitle => Kind == ConfigurationKind.Class ? "Классы" : "Зоны";
    }

    private sealed class ObjectItem
    {
        public string Name { get; set; } = "";
        public string PlcDisplay { get; set; } = "";
        public string AreaDisplay { get; set; } = "";
        public ProjectRecord AssemblyProgram { get; set; }
        public int RowIndex { get; set; }
    }
}
