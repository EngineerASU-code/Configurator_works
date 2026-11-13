using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Configurator.Application.Architecture;
using Configurator.Core.Architecture;
using Configurator.Infrastructure.Database;
using Configurator.Core.Projects;

namespace Configurator;

public partial class TemplateArchitectureView : UserControl
{
    private ClassArchitectureService _architecture = new();
    private ClassArchitectureReader _reader;
    private ConnectionSettings _connection;
    private ClassDefinition _selectedClass;
    private string _selectedDatabase = "";
    private string CurrentProjectIdentity => _connection == null ? "" : ProjectArchitectureResolver.GetProjectIdentity(_connection.Server, _selectedDatabase);
    private CancellationTokenSource _loadCts;
    private bool _availabilityGuard;
    private FieldDefinition _selectedField;
    private readonly HashSet<FieldDefinition> _selectedFields = new();
    private readonly Dictionary<string, bool> _groupExpandedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DisabledObjectArchiveService _archive = new DisabledObjectArchiveService();
    private ClassRegistryService _classRegistry;
    private Dictionary<int, bool> _projectAvailability = new();
    private ConfiguratorProject _offlineProject;
    private IProjectDataSource _offlineSource;

    public TemplateArchitectureView()
    {
        InitializeComponent();
        UserAuthorizationService.Changed += (_, _) => ApplySubmodeVisibility();
        Loaded += (_, _) => ApplySubmodeVisibility();
        DatabaseGraphControl.ActionLogged += (description, source, details) => LogMode2Action(description, source, details);
        DatabaseGraphControl.LogicalRelationDeleted += payload => LogicalRelationDeleted?.Invoke(payload);
        DatabaseGraphControl.OverviewModeChanged += overview =>
        {
            BtnGraphMode.Style = (Style)FindResource(overview ? "BtnPrimary" : "BtnSecondary");
            BtnRelationsMode.Style = (Style)FindResource(overview ? "BtnSecondary" : "BtnPrimary");
            if (overview)
            {
                GraphModeHost.Visibility = Visibility.Visible;
                FieldsModeHost.Visibility = Visibility.Collapsed;
            }
        };
        RefreshClassList();
    }

    public void ClearWorkspace()
    {
        _loadCts?.Cancel();
        _offlineProject = null;
        _offlineSource = null;
        _reader = null;
        _connection = null;
        _selectedDatabase = string.Empty;
        _selectedClass = null;
        _selectedField = null;
        _selectedFields.Clear();
        _projectAvailability.Clear();
        ClassList.ItemsSource = null;
        GroupHost.Children.Clear();
        ComplexStructureHost.Children.Clear();
        ClearEditor();
        DatabaseGraphControl.ClearWorkspace();
        TxtProjectDatabase.Text = "Проект не выбран";
        AddClassButton.IsEnabled = false;
        ChkAvailable.IsEnabled = false;
    }

    public void ConfigureOffline(ConfiguratorProject project)
    {
        _offlineProject = project ?? throw new ArgumentNullException(nameof(project));
        Configurator.Core.Projects.SqlProviderGuard.SetOfflineActive(true);
        _offlineSource = new OfflineProjectDataSource(project);
        _connection = null;
        _selectedDatabase = project.Manifest.Name;
        _architecture = new ClassArchitectureService(new Configurator.Infrastructure.Architecture.TemplateLibraryStore(project));
        EnsureOfflineClassesMapToSnapshotTables(project);
        _reader = new ClassArchitectureReader(_offlineSource);
        _classRegistry = null;
        TxtProjectDatabase.Text = _selectedDatabase;
        AddClassButton.IsEnabled = UserAuthorizationService.Has(Permission.ManageClasses) || UserAuthorizationService.Has(Permission.Mode2Manage);
        TxtClassNumber.IsEnabled = true;
        ChkAvailable.IsEnabled = true;
        ConfigureGraph();
        DatabaseGraphControl.ConfigureOffline(project);
        GraphModeHost.Visibility = Visibility.Visible;
        FieldsModeHost.Visibility = Visibility.Collapsed;
        DatabaseGraphControl.SetOverviewMode(true);
        BtnGraphMode.Style = (Style)FindResource("BtnPrimary");
        BtnRelationsMode.Style = (Style)FindResource("BtnSecondary");
        BtnFieldsMode.Style = (Style)FindResource("BtnSecondary");
        Keyboard.ClearFocus();
        RefreshClassList();
        _ = InitializeProjectDataAsync();
    }

    private void EnsureOfflineClassesMapToSnapshotTables(ConfiguratorProject project)
    {
        var schemas = project.TableSchemas.Where(x => !string.IsNullOrWhiteSpace(x.TableName)).ToList();
        var classes = _architecture.Classes.ToList();
        var changed = false;
        var systemTableCandidates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
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
        foreach (var cls in classes)
        {
            cls.StorageMappings ??= new List<StorageMapping>();
            var mapped = cls.PrimaryStorage?.TableName;
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                var exact = schemas.FirstOrDefault(t => t.TableName.Equals(mapped, StringComparison.OrdinalIgnoreCase));
                var leaf = mapped.Split('.').Last().Trim('[', ']');
                var matches = schemas.Where(t => t.TableName.Split('.').Last().Trim('[', ']').Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
                var resolved = exact?.TableName ?? (matches.Count == 1 ? matches[0].TableName : null);
                if (resolved != null && !string.Equals(resolved, mapped, StringComparison.OrdinalIgnoreCase))
                {
                    var primary = cls.StorageMappings.FirstOrDefault(x => string.Equals(x?.Role, "primary", StringComparison.OrdinalIgnoreCase));
                    if (primary != null) { primary.TableName = resolved; changed = true; }
                }
                else if (resolved == null)
                {
                    // Prefer the same domain mapping used by Mode 3 (Valve -> dbo.Valves,
                    // DSV -> dbo.Valves2S, etc.), then fall back to an unambiguous table name.
                    var fallbackSystemTable = ResolveSystemTable(cls);
                    var byClassName = schemas.Where(t => t.TableName.Split('.').Last().Trim('[', ']')
                        .Equals(cls.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    var fallback = fallbackSystemTable ?? (byClassName.Count == 1 ? byClassName[0].TableName : null);
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        var schema = schemas.FirstOrDefault(t => t.TableName.Equals(fallback, StringComparison.OrdinalIgnoreCase));
                        var primary = cls.StorageMappings.FirstOrDefault(x => string.Equals(x?.Role, "primary", StringComparison.OrdinalIgnoreCase));
                        if (primary != null)
                        {
                            primary.TableName = fallback;
                            if (schema != null) primary.KeyColumns = schema.KeyColumns.ToList();
                            changed = true;
                        }
                    }
                }
                continue;
            }
            var systemTable = ResolveSystemTable(cls);
            var classMatches = schemas.Where(t => t.TableName.Split('.').Last().Trim('[', ']').Equals(cls.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var resolvedTable = systemTable ?? (classMatches.Count == 1 ? classMatches[0].TableName : null);
            if (!string.IsNullOrWhiteSpace(resolvedTable))
            {
                var schema = schemas.FirstOrDefault(t => t.TableName.Equals(resolvedTable, StringComparison.OrdinalIgnoreCase));
                var primary = cls.StorageMappings.FirstOrDefault(x => string.Equals(x?.Role, "primary", StringComparison.OrdinalIgnoreCase));
                if (primary == null)
                {
                    primary = new StorageMapping { Role = "primary" };
                    cls.StorageMappings.Add(primary);
                }
                if (!string.Equals(primary.TableName, resolvedTable, StringComparison.OrdinalIgnoreCase))
                {
                    primary.TableName = resolvedTable;
                    changed = true;
                }
                if (schema != null && (primary.KeyColumns == null || !primary.KeyColumns.SequenceEqual(schema.KeyColumns, StringComparer.OrdinalIgnoreCase)))
                {
                    primary.KeyColumns = schema.KeyColumns.ToList();
                    changed = true;
                }
            }
        }
        var mappedTables = new HashSet<string>(classes.Select(c => c.PrimaryStorage?.TableName).OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        foreach (var schema in schemas)
        {
            if (mappedTables.Contains(schema.TableName)) continue;
            var leaf = schema.TableName.Split('.').Last().Trim('[', ']');
            if (classes.Any(c => c.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.PrimaryStorage?.TableName, schema.TableName, StringComparison.OrdinalIgnoreCase))) continue;
            classes.Add(new ClassDefinition
            {
                Id = "offline-table-" + schema.TableName, Name = leaf, Category = "Таблица БД", Available = true,
                Description = "Локальное представление таблицы снимка",
                StorageMappings = new List<StorageMapping> { new StorageMapping { Role = "primary", TableName = schema.TableName, KeyColumns = schema.KeyColumns.ToList() } },
                Fields = schema.Columns.Select(c => new FieldDefinition { Name = c.Name, DataType = c.DataType, Size = Math.Max(0, c.MaxLength), Nullable = c.IsNullable, Description = c.IsIdentity ? "IDENTITY" : c.IsComputed ? "COMPUTED" : "" }).ToList()
            });
            mappedTables.Add(schema.TableName);
            changed = true;
        }
        if (changed) _architecture.Save();
    }

    public void Configure(ConnectionSettings connection, IEnumerable<string> databases, string selectedDatabase)
    {
        _offlineProject = null;
        _offlineSource = null;
        Configurator.Core.Projects.SqlProviderGuard.SetOfflineActive(false);
        _architecture = new ClassArchitectureService();
        _selectedClass = null;
        _selectedField = null;
        _selectedFields.Clear();
        _groupExpandedStates.Clear();
        _loadCts?.Cancel();
        _loadCts = null;
        _availabilityGuard = false;
        ClassList.SelectedIndex = -1;
        GroupHost.Children.Clear();
        TxtClassName.Text = "Выберите класс";
        TxtClassMeta.Text = "";
        TxtStorageInfo.Text = "";
        TxtClassNumber.Text = "";
        ChkAvailable.IsChecked = false;
        BtnRestoreArchived.Visibility = Visibility.Collapsed;
        BtnDeleteClass.IsEnabled = false;
        BtnDeleteField.IsEnabled = false;
        BtnDetachParent.IsEnabled = false;
        _connection = connection;
        _classRegistry = null;
        _selectedDatabase = selectedDatabase ?? "";
        AddClassButton.IsEnabled = (UserAuthorizationService.Has(Permission.ManageClasses) || UserAuthorizationService.Has(Permission.Mode2Manage)) && !string.IsNullOrWhiteSpace(_selectedDatabase);
        TxtClassNumber.IsEnabled = !string.IsNullOrWhiteSpace(_selectedDatabase);
        ChkAvailable.IsEnabled = !string.IsNullOrWhiteSpace(_selectedDatabase);
        TxtProjectDatabase.Text = string.IsNullOrWhiteSpace(_selectedDatabase) ? "не выбран" : _selectedDatabase;
        _reader = _connection != null && !string.IsNullOrWhiteSpace(_selectedDatabase)
            ? new ClassArchitectureReader(_connection.ToConnectionString(_selectedDatabase)) : null;
        if (string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            ClassList.ItemsSource = Array.Empty<ClassDefinition>();
            ClearEditor();
            BtnAddField.IsEnabled = BtnDeleteField.IsEnabled = BtnSaveClass.IsEnabled = BtnDeleteClass.IsEnabled = BtnDetachParent.IsEnabled = false;
            AddClassButton.IsEnabled = false;
            TxtClassNumber.IsEnabled = false;
            ChkAvailable.IsEnabled = false;
            BtnRestoreArchived.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Выберите проект, чтобы редактировать структуру шаблонов.";
        }
        ConfigureGraph();
        GraphModeHost.Visibility = Visibility.Visible;
        FieldsModeHost.Visibility = Visibility.Collapsed;
        BtnGraphMode.Style = (Style)FindResource("BtnPrimary");
        BtnRelationsMode.Style = (Style)FindResource("BtnSecondary");
        BtnFieldsMode.Style = (Style)FindResource("BtnSecondary");
        DatabaseGraphControl.SetOverviewMode(true);
        if (!string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            RefreshClassList();
            _ = InitializeProjectDataAsync();
        }
        else
        {
            ClassList.ItemsSource = Array.Empty<ClassDefinition>();
            ClearEditor();
            GroupHost.Children.Clear();
            BtnAddField.IsEnabled = BtnDeleteField.IsEnabled = BtnSaveClass.IsEnabled = BtnDeleteClass.IsEnabled = BtnDetachParent.IsEnabled = false;
            TxtStatus.Text = "Проект недоступен. Проверьте подключение к БД и выберите проект повторно.";
        }
    }

    public void SetProjectDatabase(string database)
    {
        _classRegistry = null;
        _selectedDatabase = database ?? "";
        TxtProjectDatabase.Text = string.IsNullOrWhiteSpace(_selectedDatabase) ? "не выбран" : _selectedDatabase;
        _reader = _connection != null && !string.IsNullOrWhiteSpace(_selectedDatabase)
            ? new ClassArchitectureReader(_connection.ToConnectionString(_selectedDatabase)) : null;
        if (string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            ClassList.ItemsSource = Array.Empty<ClassDefinition>();
            ClearEditor();
            BtnAddField.IsEnabled = BtnDeleteField.IsEnabled = BtnSaveClass.IsEnabled = BtnDeleteClass.IsEnabled = BtnDetachParent.IsEnabled = false;
            AddClassButton.IsEnabled = false;
            TxtClassNumber.IsEnabled = false;
            ChkAvailable.IsEnabled = false;
            BtnRestoreArchived.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Выберите проект, чтобы редактировать структуру шаблонов.";
        }
        else
        {
            AddClassButton.IsEnabled = UserAuthorizationService.Has(Permission.ManageClasses) || UserAuthorizationService.Has(Permission.Mode2Manage);
            TxtClassNumber.IsEnabled = true;
            ChkAvailable.IsEnabled = true;
            RefreshClassList();
        }
        ConfigureGraph();
        GraphModeHost.Visibility = Visibility.Visible;
        FieldsModeHost.Visibility = Visibility.Collapsed;
        BtnGraphMode.Style = (Style)FindResource("BtnPrimary");
        BtnRelationsMode.Style = (Style)FindResource("BtnSecondary");
        BtnFieldsMode.Style = (Style)FindResource("BtnSecondary");
        DatabaseGraphControl.SetOverviewMode(true);
        if (!string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            RefreshClassList();
            _ = InitializeProjectDataAsync();
        }
        else
        {
            ClassList.ItemsSource = Array.Empty<ClassDefinition>();
            ClearEditor();
            GroupHost.Children.Clear();
            BtnAddField.IsEnabled = BtnDeleteField.IsEnabled = BtnSaveClass.IsEnabled = BtnDeleteClass.IsEnabled = BtnDetachParent.IsEnabled = false;
            TxtStatus.Text = "Проект недоступен. Проверьте подключение к БД и выберите проект повторно.";
        }
    }

    private async Task InitializeProjectDataAsync()
    {
        if (_offlineProject != null)
        {
            try
            {
                await DatabaseGraphControl.RefreshAsync();
                await RefreshProjectAvailabilityAsync();
                RefreshClassList();
                if (ClassList.SelectedItem == null && _architecture.Classes.FirstOrDefault() is ClassDefinition first) ClassList.SelectedItem = first;
                if (ClassList.SelectedItem is ClassDefinition localSelected) await RenderSelectedClassAsync(localSelected);
            }
            catch (Exception ex) { TxtStatus.Text = "Не удалось загрузить локальную архитектуру: " + ex.Message; }
            return;
        }
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        try
        {
            // Resolve class-to-table mappings before the class list or fields are rendered.
            // The first project selection must work without requiring a second click.
            if (!string.Equals(_resolvedProjectDatabaseV42, CurrentProjectIdentity, StringComparison.OrdinalIgnoreCase))
            {
                var projectIdentity = CurrentProjectIdentity;
                var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
                await new ProjectArchitectureResolver().ResolveAsync(_architecture, db, isCurrentProject: () =>
                    string.Equals(projectIdentity, CurrentProjectIdentity, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(projectIdentity, CurrentProjectIdentity, StringComparison.OrdinalIgnoreCase)) return;
                _resolvedProjectDatabaseV42 = projectIdentity;
            }
            // Load the physical schema and persisted logical graph relations before rendering
            // project-specific storage/field information. Do not rely on a previous graph state.
            if (DatabaseGraphControl != null)
                await DatabaseGraphControl.RefreshAsync();
            await RefreshProjectAvailabilityAsync();
            RefreshClassList();
            if (ClassList.SelectedItem == null)
            {
                var firstStoredClass = _architecture.Classes.FirstOrDefault(c => c.Available && c.PrimaryStorage != null && !string.IsNullOrWhiteSpace(c.PrimaryStorage.TableName));
                if (firstStoredClass != null) ClassList.SelectedItem = firstStoredClass;
            }
            if (ClassList.SelectedItem is ClassDefinition selected)
                await RenderSelectedClassAsync(selected);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Не удалось загрузить структуру проекта: {ex.Message}";
        }
    }

    private void ConfigureGraph()
    {
        if (DatabaseGraphControl == null) return;
        if (_offlineProject != null) DatabaseGraphControl.ConfigureOffline(_offlineProject);
        else DatabaseGraphControl.Configure(_connection, _selectedDatabase);
    }

    public event EventHandler ProjectSelectionRequested;
    public event EventHandler DatabaseStructureChanged;
    public event Action<string, string, string> ActionLogged;
    public event Action<GraphRelationRestorePayload> LogicalRelationDeleted;

    private void LogMode2Action(string description, string source = "", string details = "")
        => ActionLogged?.Invoke(description, source, details);
    private void SelectProject_Click(object sender, RoutedEventArgs e) => ProjectSelectionRequested?.Invoke(this, EventArgs.Empty);

    private void ApplySubmodeVisibility()
    {
        bool graph = UserAuthorizationService.Has(Permission.Mode2GraphShow);
        bool relations = UserAuthorizationService.Has(Permission.Mode2RelationsShow);
        bool fields = UserAuthorizationService.Has(Permission.Mode2FieldsShow);
        BtnGraphMode.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
        BtnRelationsMode.Visibility = relations ? Visibility.Visible : Visibility.Collapsed;
        BtnFieldsMode.Visibility = fields ? Visibility.Visible : Visibility.Collapsed;
        int count = (graph ? 1 : 0) + (relations ? 1 : 0) + (fields ? 1 : 0);
        Grid.SetColumn(BtnGraphMode, 0); Grid.SetColumn(BtnRelationsMode, graph ? 1 : 0); Grid.SetColumn(BtnFieldsMode, (graph ? 1 : 0) + (relations ? 1 : 0));
        if (BtnGraphMode.Parent is Grid modesGrid)
        {
            for (int i = 0; i < 3; i++)
            {
                if (modesGrid.ColumnDefinitions.Count > i) modesGrid.ColumnDefinitions[i].Width = i < count ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            }
        }
        if (!graph && relations) RelationsMode_Click(this, new RoutedEventArgs());
        else if (!graph && !relations && fields) FieldsMode_Click(this, new RoutedEventArgs());
        else if (graph && GraphModeHost.Visibility != Visibility.Visible) GraphMode_Click(this, new RoutedEventArgs());
        else if (!graph && !relations && !fields)
        {
            GraphModeHost.Visibility = Visibility.Collapsed;
            FieldsModeHost.Visibility = Visibility.Collapsed;
        }
    }

    private void GraphMode_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Has(Permission.Mode2GraphShow)) return;
        GraphModeHost.Visibility = Visibility.Visible;
        FieldsModeHost.Visibility = Visibility.Collapsed;
        BtnGraphMode.Style = (Style)FindResource("BtnPrimary");
        BtnRelationsMode.Style = (Style)FindResource("BtnSecondary");
        BtnFieldsMode.Style = (Style)FindResource("BtnSecondary");
        DatabaseGraphControl.SetOverviewMode(true);
        Keyboard.ClearFocus();
    }

    private void RelationsMode_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Has(Permission.Mode2RelationsShow)) return;
        GraphModeHost.Visibility = Visibility.Visible;
        FieldsModeHost.Visibility = Visibility.Collapsed;
        BtnGraphMode.Style = (Style)FindResource("BtnSecondary");
        BtnRelationsMode.Style = (Style)FindResource("BtnPrimary");
        BtnFieldsMode.Style = (Style)FindResource("BtnSecondary");
        DatabaseGraphControl.SetRulesMode();
        Keyboard.ClearFocus();
    }

    private void FieldsMode_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Has(Permission.Mode2FieldsShow)) return;
        if (string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            ClassList.ItemsSource = Array.Empty<ClassDefinition>();
            ClearEditor();
            BtnAddField.IsEnabled = BtnDeleteField.IsEnabled = BtnSaveClass.IsEnabled = BtnDeleteClass.IsEnabled = BtnDetachParent.IsEnabled = false;
            AddClassButton.IsEnabled = false;
            TxtClassNumber.IsEnabled = false;
            ChkAvailable.IsEnabled = false;
            BtnRestoreArchived.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Выберите проект, чтобы редактировать структуру шаблонов.";
        }
        GraphModeHost.Visibility = Visibility.Collapsed;
        FieldsModeHost.Visibility = Visibility.Visible;
        BtnGraphMode.Style = (Style)FindResource("BtnSecondary");
        BtnRelationsMode.Style = (Style)FindResource("BtnSecondary");
        BtnFieldsMode.Style = (Style)FindResource("BtnPrimary");
    }


    private void NestedScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var source = sender as DependencyObject;
        var scroll = FindDescendant<ScrollViewer>(source);
        if (scroll == null) return;

        // The outer tab/grid can otherwise consume the wheel event while the
        // nested ListBox/field editor is under the pointer.
        double next = scroll.VerticalOffset - e.Delta;
        scroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(scroll.ScrollableHeight, next)));
        e.Handled = true;
    }

    private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) return null;
        if (root is T matchRoot) return matchRoot;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void ExpandGroups_Click(object sender, RoutedEventArgs e)
        => SetGroupsExpanded(true);

    private void CollapseGroups_Click(object sender, RoutedEventArgs e)
        => SetGroupsExpanded(false);

    private void SetGroupsExpanded(bool expanded)
    {
        foreach (var child in GroupHost.Children)
        {
            if (child is Expander expander)
            {
                expander.IsExpanded = expanded;
                if (expander.Header is string header) _groupExpandedStates[header] = expanded;
            }
        }

        foreach (var child in ComplexStructureHost.Children)
        {
            if (child is Expander expander)
            {
                expander.IsExpanded = expanded;
                if (expander.Header is string header) _groupExpandedStates[header] = expanded;
            }
        }
    }

    private void RefreshClassList()
    {
        ClassList.ItemsSource = null;
        ClassList.ItemsSource = string.IsNullOrWhiteSpace(_selectedDatabase)
            ? Array.Empty<ClassDefinition>()
            : _architecture.Classes;
    }

    private async void ClassList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        if (ClassList.SelectedItem is not ClassDefinition selected) return;
        _selectedClass = selected;
        await RenderSelectedClassAsync(selected);
    }

    private async Task RenderSelectedClassAsync(ClassDefinition selected)
    {
        TxtClassName.Text = selected.Name;
        TxtClassNumber.Text = selected.ClassNumber > 0 ? selected.ClassNumber.ToString() : "";
        _availabilityGuard = true;
        ChkAvailable.IsChecked = IsAvailabilityRelevant(selected) ? GetProjectAvailability(selected) : true;
        _availabilityGuard = false;
        bool canEditArchitecture = UserAuthorizationService.IsAdministrator || UserAuthorizationService.Has(Permission.Mode2Manage) || UserAuthorizationService.Has(Permission.ManageClasses);
        TxtClassNumber.IsReadOnly = !canEditArchitecture || selected.IsSystem;
        BtnDeleteClass.IsEnabled = canEditArchitecture && !selected.IsSystem && UserAuthorizationService.Has(Permission.ManageClasses);
        BtnDeleteField.IsEnabled = false;
        BtnDetachParent.IsEnabled = canEditArchitecture && !selected.IsSystem && !string.IsNullOrWhiteSpace(selected.BaseClassId);
        BtnSaveClass.IsEnabled = canEditArchitecture && (UserAuthorizationService.Has(Permission.ManageClasses) || UserAuthorizationService.Has(Permission.Mode2Manage));
        ChkAvailable.IsEnabled = canEditArchitecture;
        bool complex = IsComplexClass(selected);
        ComplexNotice.Visibility = complex ? Visibility.Visible : Visibility.Collapsed;
        FieldsScroll.Visibility = Visibility.Visible;
        BtnAddField.IsEnabled = canEditArchitecture && !complex && UserAuthorizationService.Has(Permission.ManageClasses);
        BtnDeleteField.IsEnabled = false;
        TxtComplexNotice.Text = complex
            ? "Структура определяется фактическими таблицами текущей БД. Корневая таблица и все Prog_/Matrix_ дочерние таблицы отображаются отдельно; связи не создаются автоматически."
            : "";
        var primary = selected.PrimaryStorage;
        TxtStorageInfo.Text = primary == null ? "Хранилище: не задано" : $"Хранилище: {primary.TableName}";
        TxtClassMeta.Text = selected.IsSystem ? "Системный класс" : "Пользовательский класс";
        BtnRestoreArchived.Visibility = IsAvailabilityRelevant(selected) && UserAuthorizationService.Has(Permission.ManageClasses) && _connection != null && _archive.Exists(_connection.Server, _selectedDatabase, selected.Name) ? Visibility.Visible : Visibility.Collapsed;
        await LoadFieldsAsync(selected);
        await LoadComplexStructureAsync(selected);
        RebuildGroups();
        TxtStatus.Text = $"Класс {selected.Name} · № {selected.ClassNumber}";
    }

    private async void Available_Checked(object sender, RoutedEventArgs e)
    {
        if (_availabilityGuard || _selectedClass == null || !IsAvailabilityRelevant(_selectedClass)) return;
        if (!EnsureAdmin())
        {
            _availabilityGuard = true; ChkAvailable.IsChecked = _selectedClass.Available; _availabilityGuard = false; return;
        }
        _selectedClass.Available = true;
        await SyncClassRegistryAsync(_selectedClass);
        _projectAvailability[_selectedClass.ClassNumber] = true;
        TxtStatus.Text = "Класс включён для использования в проекте";
        RefreshClassList();
    }

    private async void Available_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_offlineProject != null && _selectedClass != null)
        {
            _selectedClass.Available = false;
            _architecture.Upsert(_selectedClass); _architecture.Save();
            RefreshClassList();
            TxtStatus.Text = "Класс отключён только в локальном проекте; SQL не используется";
            return;
        }
        if (_availabilityGuard || _selectedClass == null || !IsAvailabilityRelevant(_selectedClass)) return;
        if (!EnsureAdmin())
        {
            _availabilityGuard = true; ChkAvailable.IsChecked = _selectedClass.Available; _availabilityGuard = false; return;
        }
        var classDef = _selectedClass;
        var table = classDef.PrimaryStorage?.TableName;
        var answer = Configurator.Infrastructure.ModernDialog.Show($"Класс «{classDef.Name}» будет помечен как «Не использовать».\n\nОчистить таблицу {table ?? "(хранилище не задано)"} от объектов?\n\nДанные сначала будут сохранены в архив.", "Отключение класса", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            _availabilityGuard = true; ChkAvailable.IsChecked = true; _availabilityGuard = false; return;
        }
        try
        {
            if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase) || _reader == null) throw new InvalidOperationException("Сначала выберите БД проекта.");
            if (string.IsNullOrWhiteSpace(table)) throw new InvalidOperationException("Для класса не задано SQL-хранилище.");
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var rows = await db.GetAllTableRowsAsync(table);
            await _archive.ArchiveAsync(_connection.Server, _selectedDatabase, classDef.Name, table, rows);
            // The archive must be readable and contain every row before anything is deleted.
            var archived = await _archive.GetArchivedRowsAsync(_connection.Server, _selectedDatabase, classDef.Name);
            if (archived.Count < rows.Rows.Count)
                throw new InvalidOperationException($"Архив содержит {archived.Count} из {rows.Rows.Count} строк. Таблица не очищена.");
            int removed = await db.ClearTableAsync(table, rows.Rows.Count);
            classDef.Available = false;
            await SyncClassRegistryAsync(classDef);
            _projectAvailability[classDef.ClassNumber] = false;
            BtnRestoreArchived.Visibility = Visibility.Visible;
            TxtStatus.Text = $"Класс отключён · архивировано: {rows.Rows.Count}, очищено: {removed}";
            RefreshClassList();
        }
        catch (Exception ex)
        {
            _availabilityGuard = true; ChkAvailable.IsChecked = true; _availabilityGuard = false;
            Configurator.Infrastructure.ModernDialog.Show($"Не удалось отключить класс: {ex.Message}", "Отключение класса", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RestoreArchived_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClass?.PrimaryStorage == null || _connection == null || string.IsNullOrWhiteSpace(_selectedDatabase) || !EnsureAdmin()) return;
        try
        {
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            int restored = await _archive.RestoreAsync(_connection.Server, _selectedDatabase, _selectedClass.Name, _selectedClass.PrimaryStorage.TableName, db);
            _selectedClass.Available = true;
            await SyncClassRegistryAsync(_selectedClass);
            _projectAvailability[_selectedClass.ClassNumber] = true;
            ChkAvailable.IsChecked = true;
            BtnRestoreArchived.Visibility = Visibility.Collapsed;
            TxtStatus.Text = $"Класс включён · восстановлено объектов: {restored}";
            if (_archive.LastRestoreErrors.Count > 0)
                Configurator.Infrastructure.ModernDialog.Show(
                    "Часть объектов не восстановлена и осталась в архиве:\n\n" + string.Join("\n", _archive.LastRestoreErrors),
                    "Восстановление", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshClassList();
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show($"Не удалось восстановить объекты: {ex.Message}", "Восстановление", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private bool GetProjectAvailability(ClassDefinition definition) => definition != null && (_projectAvailability.TryGetValue(definition.ClassNumber, out bool value) ? value : definition.Available);

    private async Task RefreshProjectAvailabilityAsync()
    {
        _projectAvailability.Clear();
        if (_offlineProject != null)
        {
            foreach (var cls in _architecture.Classes) _projectAvailability[cls.ClassNumber] = cls.Available;
            RefreshClassList();
            return;
        }
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) { RefreshClassList(); return; }
        foreach (var cls in _architecture.Classes) cls.Available = true;
        try
        {
            _classRegistry = new ClassRegistryService(_connection.ToConnectionString(_selectedDatabase));
            _projectAvailability = await _classRegistry.GetAvailabilityAsync();
            foreach (var cls in _architecture.Classes) cls.Available = _projectAvailability.TryGetValue(cls.ClassNumber, out bool available) ? available : true;
        }
        catch { }
        RefreshClassList();
    }

    private static bool IsAvailabilityRelevant(ClassDefinition c) => !c.Name.Equals("Program", StringComparison.OrdinalIgnoreCase) && !c.Name.Equals("Status", StringComparison.OrdinalIgnoreCase) && !c.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase) && !c.Name.Equals("Step", StringComparison.OrdinalIgnoreCase);

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_offlineProject != null) { await InitializeProjectDataAsync(); return; }
        if (string.IsNullOrWhiteSpace(_selectedDatabase) || _connection == null) return;
        if (!await _connection.TestConnectionAsync(_selectedDatabase))
        {
            Configurator.Infrastructure.ModernDialog.Show($"Связь с БД «{_selectedDatabase}» потеряна. Обновление невозможно.", "Шаблоны проекта", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        await DatabaseGraphControl.RefreshAsync();
        if (_selectedClass != null) await RenderSelectedClassAsync(_selectedClass);
    }

    private async void AddField_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable()) return;
        if (_offlineProject == null && (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase) || !await _connection.TestConnectionAsync(_selectedDatabase)))
        {
            Configure(_connection, Array.Empty<string>(), string.Empty);
            TxtStatus.Text = "Связь с БД потеряна. Добавление полей заблокировано до восстановления подключения.";
            return;
        }
        string targetGroup = !string.IsNullOrWhiteSpace(_selectedField?.Group)
            ? _selectedField.Group
            : FieldGroupCatalog.Resolve(_selectedClass.Name, "NewField");
        var newField = new FieldDefinition { Name = "NewField", DataType = "string", Group = targetGroup };
        _selectedClass.Fields.Add(newField);
        _selectedField = newField;
        _selectedFields.Clear();
        _selectedFields.Add(newField);
        RebuildGroups();
        LogMode2Action($"Добавлено поле шаблона · {_selectedClass.Name}.{newField.Name}", _selectedClass.PrimaryStorage?.TableName, $"Группа={targetGroup}; состояние=черновик");
    }

    private void FieldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        foreach (var item in e.RemovedItems.OfType<FieldDefinition>()) _selectedFields.Remove(item);
        foreach (var item in e.AddedItems.OfType<FieldDefinition>()) _selectedFields.Add(item);
        _selectedField = _selectedFields.FirstOrDefault();
        BtnDeleteField.IsEnabled = _selectedFields.Count > 0 && _selectedClass != null && !IsComplexClass(_selectedClass);
    }

    private async void DeleteField_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable() || _selectedClass == null || _selectedFields.Count == 0) return;
        if (_offlineProject == null && (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase) || !await _connection.TestConnectionAsync(_selectedDatabase)))
        {
            Configure(_connection, Array.Empty<string>(), string.Empty);
            TxtStatus.Text = "Связь с БД потеряна. Изменение полей заблокировано до восстановления подключения.";
            return;
        }
        var inherited = _selectedFields.Where(x => x.IsInherited).ToList();
        var removable = _selectedFields.Where(x => !x.IsInherited).ToList();
        if (inherited.Count > 0 && removable.Count == 0)
        {
            Configurator.Infrastructure.ModernDialog.Show("Выбранные поля унаследованы и не могут быть удалены в дочернем классе. Сначала сделайте поля своими или измените их в родительском классе.", "Шаблоны проекта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (inherited.Count > 0)
            Configurator.Infrastructure.ModernDialog.Show($"Из {inherited.Count + removable.Count} выбранных полей {inherited.Count} унаследованных и останутся в классе. Будут удалены только собственные поля.", "Удаление полей", MessageBoxButton.OK, MessageBoxImage.Information);

        foreach (var field in removable) _selectedClass.Fields.Remove(field);
        LogMode2Action($"Удалены поля шаблона · {_selectedClass.Name}", _selectedClass.PrimaryStorage?.TableName, string.Join(", ", removable.Select(f => f.Name)));
        _selectedFields.Clear();
        _selectedField = null;
        BtnDeleteField.IsEnabled = false;
        RebuildGroups();
    }

    private void DetachParent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClass == null || _selectedClass.IsSystem || string.IsNullOrWhiteSpace(_selectedClass.BaseClassId)) return;

        var effective = _architecture.GetEffectiveFields(_selectedClass)
            .Select(CloneFieldForOwnClass)
            .ToList();

        var parent = _architecture.Find(_selectedClass.BaseClassId);
        var answer = Configurator.Infrastructure.ModernDialog.Show(
            $"У класса «{_selectedClass.Name}» есть родитель «{parent?.Name ?? "неизвестный класс"}».\n\n" +
            "Унаследованные поля будут скопированы в этот класс как собственные, а связь с родителем будет удалена. " +
            "После этого поля можно свободно изменять и удалять.\n\nПродолжить?",
            "Сделать поля своими",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _selectedClass.Fields = effective;
        _selectedClass.BaseClassId = "";
        BtnDetachParent.IsEnabled = false;
        BtnDeleteField.IsEnabled = false;
        RebuildGroups();
        TxtClassMeta.Text = _selectedClass.IsSystem ? "Системный класс" : "Пользовательский класс · без родителя";
        TxtStatus.Text = "Родитель удалён · поля теперь собственные. Нажмите «Сохранить».";
    }

    private static FieldDefinition CloneFieldForOwnClass(FieldDefinition source) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = source.Name,
        DataType = source.DataType,
        Size = source.Size,
        Offset = source.Offset,
        BitOffset = source.BitOffset,
        BitLength = source.BitLength,
        Nullable = source.Nullable,
        DefaultValue = source.DefaultValue,
        Description = source.Description,
        Group = source.Group,
        IsInherited = false
    };

    private async void SaveClass_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClass == null || !EnsureEditable()) return;
        if (_offlineProject == null && (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase) || !await _connection.TestConnectionAsync(_selectedDatabase)))
        {
            Configure(_connection, Array.Empty<string>(), string.Empty);
            TxtStatus.Text = "Связь с БД потеряна. Сохранение структуры заблокировано до восстановления подключения.";
            return;
        }
        if (!_selectedClass.IsSystem)
        {
            if (!int.TryParse(TxtClassNumber.Text.Trim(), out int number) || number <= 0)
            {
                Configurator.Infrastructure.ModernDialog.Show("Номер класса должен быть положительным целым числом.", "Шаблоны проекта", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_architecture.Classes.Any(x => x.Id != _selectedClass.Id && x.ClassNumber == number))
            {
                Configurator.Infrastructure.ModernDialog.Show($"Номер класса {number} уже используется.", "Шаблоны проекта", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _selectedClass.ClassNumber = number;
        }
        else
        {
            TxtClassNumber.Text = _selectedClass.ClassNumber.ToString();
        }

        try
        {
            if (_offlineProject != null)
            {
                _architecture.Upsert(_selectedClass);
                _architecture.Save();
                var localFields = (_selectedClass.IsSystem ? _selectedClass.Fields : _architecture.GetEffectiveFields(_selectedClass)).Where(f => !string.IsNullOrWhiteSpace(f.Name)).GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                if (_selectedClass.PrimaryStorage != null && !string.IsNullOrWhiteSpace(_selectedClass.PrimaryStorage.TableName))
                {
                    var existing = _offlineProject.TableSchemas.FirstOrDefault(x => x.TableName.Equals(_selectedClass.PrimaryStorage.TableName, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        existing = new ProjectTableSchema { TableName = _selectedClass.PrimaryStorage.TableName };
                        _offlineProject.TableSchemas.Add(existing);
                        if (!_offlineProject.Mappings.Any(x => x.TableName.Equals(existing.TableName, StringComparison.OrdinalIgnoreCase)))
                            _offlineProject.Mappings.Add(new ProjectMapping { EntityName = existing.TableName, TableName = existing.TableName, KeyColumn = localFields.FirstOrDefault(f => f.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name ?? string.Empty, ReadEnabled = true, WriteEnabled = false });
                    }
                    existing.Columns = localFields.Select(f => new ProjectColumnSchema { Name = f.Name, DataType = f.DataType, MaxLength = f.Size, IsNullable = f.Nullable }).ToList();
                    existing.KeyColumns = localFields.Where(f => f.Name.Equals("Record", StringComparison.OrdinalIgnoreCase)).Select(f => f.Name).ToList();
                    existing.HasStableKey = existing.KeyColumns.Count > 0;
                    _offlineProject.Manifest.ModifiedUtc = DateTimeOffset.UtcNow;
                }
                RefreshClassList();
                TxtStatus.Text = "Класс сохранён в локальный проект · SQL не используется";
                return;
            }
            if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))
                throw new InvalidOperationException("Сначала выберите БД проекта.");
            if (_selectedClass.PrimaryStorage == null || string.IsNullOrWhiteSpace(_selectedClass.PrimaryStorage.TableName))
                throw new InvalidOperationException("Для класса не задана SQL-таблица.");

            var effectiveFields = (_selectedClass.IsSystem
                ? _selectedClass.Fields
                : _architecture.GetEffectiveFields(_selectedClass))
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (effectiveFields.Count == 0)
                throw new InvalidOperationException("У класса нет полей. Добавьте хотя бы одно поле перед сохранением.");

            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            await EnsureClassStorageAsync(db, _selectedClass.PrimaryStorage.TableName, effectiveFields);

            _architecture.Upsert(_selectedClass);
            _architecture.Save();
            await SyncClassRegistryAsync(_selectedClass);
            SyncFieldLookupRelations(_selectedClass);

            BtnDetachParent.IsEnabled = !string.IsNullOrWhiteSpace(_selectedClass.BaseClassId) && !_selectedClass.IsSystem;
            RefreshClassList();
            TxtStatus.Text = $"Класс сохранён · SQL-таблица {_selectedClass.PrimaryStorage.TableName} синхронизирована";
            LogMode2Action($"Сохранена структура шаблона · {_selectedClass.Name}", _selectedClass.PrimaryStorage.TableName,
                string.Join("; ", effectiveFields.Select(f => $"{f.Name}:{f.DataType}, size={f.Size}, nullable={f.Nullable}, group={f.Group}")));

            string savedTableName = _selectedClass.PrimaryStorage.TableName;
            DatabaseStructureChanged?.Invoke(this, EventArgs.Empty);
            // Refresh the visual graph after the current WPF input/edit transaction has
            // returned to the dispatcher. A graph redraw must not abort a successful SQL save.
            // Schedule and observe the asynchronous refresh task explicitly; avoid CS4014
            // while keeping graph refresh outside the current save/input transaction.
            _ = Dispatcher.InvokeAsync(
                () => RefreshGraphAfterSaveAsync(savedTableName),
                System.Windows.Threading.DispatcherPriority.Background).Task.Unwrap();
        }
        catch (Exception ex)
        {
            LogMode2Action($"Ошибка сохранения структуры шаблона · {_selectedClass?.Name}", _selectedClass?.PrimaryStorage?.TableName, ex.ToString());
            Configurator.Infrastructure.ModernDialog.Show($"Не удалось сохранить класс и его SQL-структуру: {ex.Message}", "Шаблоны проекта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }



    private async Task RefreshGraphAfterSaveAsync(string savedTableName)
    {
        await Task.Yield();
        try
        {
            if (DatabaseGraphControl == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
            await DatabaseGraphControl.RefreshAsync();
            DatabaseGraphControl.SelectTable(savedTableName);
        }
        catch (Exception graphError)
        {
            LogMode2Action("Ошибка обновления графа после сохранения", savedTableName, graphError.ToString());
            TxtStatus.Text = $"Структура сохранена, но граф не обновлён: {graphError.Message}";
        }
    }

    private void SyncFieldLookupRelations(ClassDefinition definition)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase) || definition?.PrimaryStorage == null) return;
        try
        {
            var store = new GraphRelationStore(_connection?.Server ?? string.Empty, _selectedDatabase);
            var document = store.Load();
            string sourceTable = definition.PrimaryStorage.TableName;
            document.ManualRelations.RemoveAll(r =>
                r.Rule?.StartsWith("field-metadata:", StringComparison.OrdinalIgnoreCase) == true &&
                r.Source.Equals(sourceTable, StringComparison.OrdinalIgnoreCase));

            foreach (var field in definition.Fields.Where(f => !string.IsNullOrWhiteSpace(f.LookupTable) && !string.IsNullOrWhiteSpace(f.LookupKeyField)))
            {
                document.ManualRelations.Add(new GraphRelationDefinition
                {
                    Source = sourceTable,
                    Target = field.LookupTable,
                    SourceColumn = field.Name,
                    TargetColumn = field.LookupKeyField,
                    Rule = $"field-metadata:{definition.Name}:{field.Name}"
                });
            }
            store.Save(document);
        }
        catch { }
    }

    private async Task EnsureClassStorageAsync(DatabaseService db, string tableName, IReadOnlyList<FieldDefinition> fields)
    {
        var tables = await db.GetTableListAsync();
        var parts = tableName.Contains('.') ? tableName.Split('.', 2) : new[] { "dbo", tableName };
        string schema = parts[0];
        string name = parts.Length > 1 ? parts[1] : parts[0];
        bool exists = tables.Rows.Cast<DataRow>().Any(r =>
            string.Equals(Convert.ToString(r[0]), schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Convert.ToString(r[1]), name, StringComparison.OrdinalIgnoreCase));

        var columns = fields.Select(ToTemplateColumn).ToList();
        if (!exists)
            await db.CreateTemplateTableAsync(tableName, columns);
        else
            await db.SyncTemplateColumnsAsync(tableName, columns);
    }

    private async Task SyncClassRegistryAsync(ClassDefinition definition)
    {
        if (_offlineProject != null) { _architecture.Upsert(definition); _architecture.Save(); return; }
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase) || definition == null) return;
        _classRegistry = new ClassRegistryService(_connection.ToConnectionString(_selectedDatabase));
        await _classRegistry.UpsertAsync(definition);
    }

    private async void DeleteClass_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClass == null || _selectedClass.IsSystem) return;
        if (!EnsureAdmin()) return;

        var deletedClass = _selectedClass;
        var table = deletedClass.PrimaryStorage?.TableName;
        var answer = Configurator.Infrastructure.ModernDialog.Show(
            $"Удалить пользовательский класс «{deletedClass.Name}»?\n\n" +
            "Класс будет удалён из библиотеки и dbo.Classes." +
            (string.IsNullOrWhiteSpace(table) ? "" : $"\n\nSQL-таблица: {table}\n\nДа — удалить также SQL-таблицу класса.\nНет — оставить SQL-таблицу с данными."),
            "Удаление класса",
            string.IsNullOrWhiteSpace(table) ? MessageBoxButton.YesNo : MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel) return;
        bool dropStorageTable = !string.IsNullOrWhiteSpace(table) && answer == MessageBoxResult.Yes;

        try
        {
            if (_offlineProject == null && dropStorageTable && _connection != null && !string.IsNullOrWhiteSpace(_selectedDatabase))
            {
                var connectionString = _connection.ToConnectionString(_selectedDatabase);
                var schema = await new DatabaseArchitectureAnalyzer(connectionString).AnalyzeAsync();
                var dependencies = schema.Relations.Where(r => r.TargetTable.Equals(table, StringComparison.OrdinalIgnoreCase)).ToList();
                if (dependencies.Count > 0)
                {
                    var details = string.Join("\n", dependencies.Select(r => $"• {r.SourceTable}: {string.Join(", ", r.SourceColumns)} → {string.Join(", ", r.TargetColumns)}"));
                    Configurator.Infrastructure.ModernDialog.Show(
                        $"Таблица {table} имеет зависимости, которые нельзя молча оставить сиротами.\n\n{details}\n\nСначала обработайте значения/ссылки через удаление конкретных записей. Таблица и класс не удалены.",
                        "Есть зависимости", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var db = new DatabaseService(connectionString);
                await db.DropTableAsync(table);
            }

            // Remove the database registry row only after SQL deletion has passed
            // dependency protection.
            await SyncClassRegistryRemoveAsync(deletedClass);

            _architecture.DeleteUserClass(deletedClass.Id);
            _architecture.Save();
            _selectedClass = null;
            RefreshClassList();
            ClearEditor();
            TxtStatus.Text = _offlineProject != null
                ? $"Класс «{deletedClass.Name}» удалён из локального проекта"
                : dropStorageTable ? $"Класс «{deletedClass.Name}» и SQL-таблица удалены" : $"Класс «{deletedClass.Name}» удалён, SQL-таблица сохранена";
            LogMode2Action($"Удалён класс шаблона · {deletedClass.Name}", table ?? "", dropStorageTable ? "Удалена SQL-таблица" : "SQL-таблица сохранена");

            // Mode 1 must immediately reflect a class/table removed from Mode 2.
            // Do not wait for the periodic schema poll: the user just changed the schema.
            if (dropStorageTable)
                DatabaseStructureChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show($"Удаление класса не завершено: {ex.Message}", "Удаление класса", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddClass_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdmin()) return;
        var dialog = new NewClassWindow(_architecture.Classes) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result == null) return;
        try
        {
            if (_offlineProject == null && _connection != null && !string.IsNullOrWhiteSpace(_selectedDatabase))
            {
                _classRegistry ??= new ClassRegistryService(_connection.ToConnectionString(_selectedDatabase));
                int dbNext = await _classRegistry.GetNextClassNumberAsync();
                if (dialog.Result.ClassNumber < dbNext) dialog.Result.ClassNumber = dbNext;
                await _classRegistry.UpsertAsync(dialog.Result);
            }
            _architecture.Upsert(dialog.Result); _architecture.Save(); RefreshClassList(); ClassList.SelectedItem = _architecture.Find(dialog.Result.Id);
            TxtStatus.Text = $"Создан класс {dialog.Result.Name} · № {dialog.Result.ClassNumber}";
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show($"Не удалось зарегистрировать класс в dbo.Classes: {ex.Message}", "Новый класс", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task SyncClassRegistryRemoveAsync(ClassDefinition definition)
    {
        if (_offlineProject != null) return;
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase) || definition == null) return;
        _classRegistry = new ClassRegistryService(_connection.ToConnectionString(_selectedDatabase));
        await _classRegistry.RemoveAsync(definition);
    }

    private bool EnsureAdmin() => UserAuthorizationService.Has(Permission.ManageClasses) || UserAuthorizationService.Require(Permission.Mode2Manage);
    private bool EnsureEditable() => !string.IsNullOrWhiteSpace(_selectedDatabase) && _selectedClass != null && (!_selectedClass.IsSystem || EnsureAdmin());

    private void ClearEditor()
    {
        GroupHost.Children.Clear();
        ComplexStructureHost.Children.Clear();
        ComplexStructureHost.Visibility = Visibility.Collapsed;
        ComplexNotice.Visibility = Visibility.Collapsed;
        FieldsScroll.Visibility = Visibility.Visible;
        TxtClassName.Text = "Выберите класс"; TxtClassMeta.Text = ""; TxtClassNumber.Text = ""; TxtStorageInfo.Text = "";
    }

    private void CancelLoad() { _loadCts?.Cancel(); _loadCts = null; }
}
