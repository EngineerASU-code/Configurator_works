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
    private FieldDefinition _selectedField;
    private readonly HashSet<FieldDefinition> _selectedFields = new();
    private readonly Dictionary<string, bool> _groupExpandedStates = new(StringComparer.OrdinalIgnoreCase);
    private ClassRegistryService _classRegistry;
    private Dictionary<int, bool> _projectAvailability = new();
    private ConfiguratorProject _offlineProject;
    private IProjectDataSource _offlineSource;

    public TemplateArchitectureView()
    {
        InitializeComponent();
        UserAuthorizationService.Changed += (_, _) => ApplySubmodeVisibility();
        Loaded += (_, _) => ApplySubmodeVisibility();
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
        TxtClassNumber.IsEnabled = true;
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
                    if (primary != null) { primary.TableName = resolved; }
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
                }
                if (schema != null && (primary.KeyColumns == null || !primary.KeyColumns.SequenceEqual(schema.KeyColumns, StringComparer.OrdinalIgnoreCase)))
                {
                    primary.KeyColumns = schema.KeyColumns.ToList();
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
            _architecture.Upsert(new ClassDefinition
            {
                Id = "offline-table-" + schema.TableName, Name = leaf, Category = "Таблица БД", Available = true,
                Description = "Локальное представление таблицы снимка",
                StorageMappings = new List<StorageMapping> { new StorageMapping { Role = "primary", TableName = schema.TableName, KeyColumns = schema.KeyColumns.ToList() } },
                Fields = schema.Columns.Select(c => new FieldDefinition { Name = c.Name, DataType = c.DataType, Size = Math.Max(0, c.MaxLength), Nullable = c.IsNullable, Description = c.IsIdentity ? "IDENTITY" : c.IsComputed ? "COMPUTED" : "" }).ToList()
            });
            mappedTables.Add(schema.TableName);
        }
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
        ClassList.SelectedIndex = -1;
        GroupHost.Children.Clear();
        TxtClassName.Text = "Выберите класс";
        TxtClassMeta.Text = "";
        TxtStorageInfo.Text = "";
        TxtClassNumber.Text = "";
        ChkAvailable.IsChecked = false;

        _connection = connection;
        _classRegistry = null;
        _selectedDatabase = selectedDatabase ?? "";
        TxtClassNumber.IsEnabled = !string.IsNullOrWhiteSpace(_selectedDatabase);
        TxtProjectDatabase.Text = string.IsNullOrWhiteSpace(_selectedDatabase) ? "не выбран" : _selectedDatabase;
        _reader = _connection != null && !string.IsNullOrWhiteSpace(_selectedDatabase)
            ? new ClassArchitectureReader(_connection.ToConnectionString(_selectedDatabase)) : null;
        if (string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            ClassList.ItemsSource = Array.Empty<ClassDefinition>();
            ClearEditor();
            TxtClassNumber.IsEnabled = false;
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
            TxtClassNumber.IsEnabled = false;
            TxtStatus.Text = "Выберите проект, чтобы редактировать структуру шаблонов.";
        }
        else
        {
            TxtClassNumber.IsEnabled = true;
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
            catch (Exception ex) { TxtStatus.Text = "Не удалось загрузить локальную архитектуру: " + Configurator.Infrastructure.UserErrorMessage.Format(ex); }
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
            TxtStatus.Text = $"Не удалось загрузить структуру проекта: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
        }
    }

    private void ConfigureGraph()
    {
        if (DatabaseGraphControl == null) return;
        if (_offlineProject != null) DatabaseGraphControl.ConfigureOffline(_offlineProject);
        else DatabaseGraphControl.Configure(_connection, _selectedDatabase);
    }

    public event EventHandler ProjectSelectionRequested;

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
            TxtClassNumber.IsEnabled = false;
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
        ChkAvailable.IsChecked = IsAvailabilityRelevant(selected) ? GetProjectAvailability(selected) : true;
        TxtClassNumber.IsReadOnly = true;

        bool complex = IsComplexClass(selected);
        ComplexNotice.Visibility = complex ? Visibility.Visible : Visibility.Collapsed;
        FieldsScroll.Visibility = Visibility.Visible;
        TxtComplexNotice.Text = complex
            ? "Структура определяется фактическими таблицами текущей БД. Корневая таблица и все Prog_/Matrix_ дочерние таблицы отображаются отдельно; связи не создаются автоматически."
            : "";
        var primary = selected.PrimaryStorage;
        TxtStorageInfo.Text = primary == null ? "Хранилище: не задано" : $"Хранилище: {primary.TableName}";
        TxtClassMeta.Text = selected.IsSystem ? "Системный класс" : "Пользовательский класс";
        await LoadFieldsAsync(selected);
        await LoadComplexStructureAsync(selected);
        RebuildGroups();
        TxtStatus.Text = $"Класс {selected.Name} · № {selected.ClassNumber}";
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

    private void FieldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        foreach (var item in e.RemovedItems.OfType<FieldDefinition>()) _selectedFields.Remove(item);
        foreach (var item in e.AddedItems.OfType<FieldDefinition>()) _selectedFields.Add(item);
        _selectedField = _selectedFields.FirstOrDefault();
    }

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
