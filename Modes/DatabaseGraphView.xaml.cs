#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Configurator.Core.Architecture;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Data.SqlClient;
using Configurator.Infrastructure.Database;
using Configurator.Core.Projects;

namespace Configurator;

public partial class DatabaseGraphView : UserControl
{
    private ConnectionSettings? _connection;
    private string _database = "";
    private ConfiguratorProject? _offlineProject;
    private readonly List<GraphTable> _tables = new();
    private readonly List<GraphRelation> _relations = new();
    private string? _selectedTable;
    private readonly Dictionary<string, Point> _graphPositions = new(StringComparer.OrdinalIgnoreCase);
    private string? _graphLayoutRoot;
    private string? _draggingTable;
    private Border? _draggingNodeVisual;
    private Point _dragOffset;
    private bool _panningGraph;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private double _graphZoom = 1.0;
    private GraphRelation? _selectedGraphRelation;
    private GraphRelationProjectDocument _graphRelationDocument = new();
    private GraphRelationStore? _graphRelationStore;
    private const double GraphBaseWidth = 5000;
    private const double GraphBaseHeight = 8000;
    private const double GraphMinZoom = 0.10;
    private const double GraphMaxZoom = 3.00;
    private bool _overviewMode = true;
    public event Action<string, string, string>? ActionLogged;
    public event Action<GraphRelationRestorePayload>? LogicalRelationDeleted;
    public event Action<bool>? OverviewModeChanged;

    public DatabaseGraphView()
    {
        InitializeComponent();
        GraphCanvas.MouseMove += GraphCanvas_MouseMove;
        GraphCanvas.PreviewMouseMove += GraphCanvas_PreviewMouseMove;
        GraphCanvas.MouseLeftButtonUp += GraphCanvas_MouseLeftButtonUp;
        GraphCanvas.PreviewMouseWheel += GraphCanvas_PreviewMouseWheel;
        if (RulesList != null) RulesList.MouseDoubleClick += RulesList_MouseDoubleClick;
        if (LogicalRulesList != null) LogicalRulesList.MouseDoubleClick += RulesList_MouseDoubleClick;
        GraphCanvas.PreviewMouseRightButtonDown += GraphCanvas_PreviewMouseRightButtonDown;
        GraphCanvas.PreviewMouseRightButtonUp += GraphCanvas_PreviewMouseRightButtonUp;
    }


    public void ClearWorkspace()
    {
        _offlineProject = null;
        _connection = null;
        _database = string.Empty;
        _selectedTable = null;
        _selectedGraphRelation = null;
        _tables.Clear();
        _relations.Clear();
        _graphRelationDocument = new GraphRelationProjectDocument();
        _graphRelationStore = null;
        _graphPositions.Clear();
        _graphLayoutRoot = null;
        GraphTableList.ItemsSource = null;
        RulesList.ItemsSource = null;
        if (LogicalRulesList != null) LogicalRulesList.ItemsSource = null;
        GraphCanvas.Children.Clear();
        if (TxtGraphStatus != null) TxtGraphStatus.Text = "Проект не выбран";
    }

    /// <summary>Switches between a quiet architecture overview and the relationship editing workspace.</summary>
    public void SetOverviewMode(bool overview)
    {
        _overviewMode = overview;
        if (RulesHost != null) RulesHost.Visibility = Visibility.Collapsed;
        if (GraphTableList != null) GraphTableList.Visibility = Visibility.Visible;
        if (GraphScrollViewer != null) GraphScrollViewer.Visibility = Visibility.Visible;
        if (GraphRelationActions != null) GraphRelationActions.Visibility = Visibility.Collapsed;
        if (TxtGraphStatus != null) TxtGraphStatus.Visibility = Visibility.Visible;
        RenderGraph(focusSelected: false);
        OverviewModeChanged?.Invoke(true);
    }

    public void SetRulesMode()
    {
        _overviewMode = false;
        if (RulesHost != null) RulesHost.Visibility = Visibility.Visible;
        if (GraphTableList != null) GraphTableList.Visibility = Visibility.Collapsed;
        if (GraphScrollViewer != null) GraphScrollViewer.Visibility = Visibility.Collapsed;
        if (GraphRelationActions != null) GraphRelationActions.Visibility = Visibility.Collapsed;
        if (TxtGraphStatus != null) TxtGraphStatus.Visibility = Visibility.Collapsed;
        RefreshRulesList();
        OverviewModeChanged?.Invoke(false);
    }

    private void RefreshRulesList()
    {
        if (RulesList == null || LogicalRulesList == null) return;
        _selectedGraphRelation = null;
        UpdateGraphRelationButtons();
        RulesList.ItemsSource = _relations.Where(r => r.Kind.Equals("FK", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Target, StringComparer.OrdinalIgnoreCase).ToList();
        LogicalRulesList.ItemsSource = _relations.Where(r => !r.Kind.Equals("FK", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Target, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView list || list.SelectedItem is not GraphRelation relation) return;
        var other = ReferenceEquals(list, RulesList) ? LogicalRulesList : RulesList;
        if (other != null) other.SelectedItem = null;
        _selectedGraphRelation = relation;
        UpdateGraphRelationButtons();
    }

    private void RulesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView list || list.SelectedItem is not GraphRelation relation) return;
        SetOverviewMode(true);
        var focusTable = _tables.FirstOrDefault(t => t.FullName.Equals(relation.Target, StringComparison.OrdinalIgnoreCase))
                         ?? _tables.FirstOrDefault(t => t.FullName.Equals(relation.Source, StringComparison.OrdinalIgnoreCase));
        if (focusTable == null) return;
        SelectTable(focusTable.FullName);
        FocusTableOnGraph(focusTable.FullName);
        e.Handled = true;
    }

    private void FocusTableOnGraph(string tableName)
    {
        if (GraphScrollViewer == null || GraphScale == null || !_graphPositions.TryGetValue(tableName, out var point)) return;
        GraphScrollViewer.UpdateLayout();
        GraphScrollViewer.ScrollToHorizontalOffset(Math.Max(0, point.X * _graphZoom - GraphScrollViewer.ViewportWidth / 2));
        GraphScrollViewer.ScrollToVerticalOffset(Math.Max(0, point.Y * _graphZoom - GraphScrollViewer.ViewportHeight / 2));
    }

    public void SelectTable(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || GraphTableList == null) return;
        var match = _tables.FirstOrDefault(t => t.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)
                                             || t.Name.Equals(fullName.Contains('.') ? fullName[(fullName.IndexOf('.') + 1)..] : fullName, StringComparison.OrdinalIgnoreCase));
        if (match == null) return;
        GraphTableList.SelectedItem = match;
        _selectedTable = match.FullName;
        RenderGraph();
    }

    public void Configure(ConnectionSettings connection, string database)
    {
        _offlineProject = null;
        _graphRelationStore = null;
        var nextDatabase = database ?? string.Empty;
        bool databaseChanged = !string.Equals(_database, nextDatabase, StringComparison.OrdinalIgnoreCase);

        if (databaseChanged)
        {
            _graphPositions.Clear();
            _graphLayoutRoot = null;
            _selectedTable = null;
            _selectedGraphRelation = null;
            SetGraphZoom(1.0, null);
        }

        _connection = connection;
        _database = nextDatabase;
        // Reconfiguring the same project (mode switch, timer, or refresh) must not
        // throw the user back to the first/Actions table. Keep the last graph.
        if (!databaseChanged && !string.IsNullOrWhiteSpace(_selectedTable))
            _selectedGraphRelation = null;

        _ = LoadGraphAsync();
    }

    public void ConfigureOffline(ConfiguratorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        bool sameProject = ReferenceEquals(_offlineProject, project);
        _offlineProject = project;
        _connection = null;
        _database = project.Manifest.Name;
        if (!sameProject)
        {
            _graphPositions.Clear();
            _graphLayoutRoot = null;
            _selectedTable = null;
            _selectedGraphRelation = null;
            _graphRelationStore = new GraphRelationStore(project);
            _ = LoadGraphAsync();
        }
        else if (_tables.Count == 0)
        {
            _graphRelationStore ??= new GraphRelationStore(project);
            _ = LoadGraphAsync();
        }
    }

    public async Task RefreshAsync() => await LoadGraphAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadGraphAsync();

    private async Task LoadGraphAsync()
    {
        if (_offlineProject != null)
        {
            LoadOfflineGraph();
            return;
        }
        if (_connection == null || string.IsNullOrWhiteSpace(_database))
        {
            ClearGraph("Выберите БД проекта для построения графа.");
            return;
        }

        try
        {
            TxtGraphStatus.Text = $"Читаем структуру БД «{_database}»...";
            var cs = _connection.ToConnectionString(_database);
            if (!await _connection.TestConnectionAsync(_database))
            {
                ClearGraph($"Связь с БД «{_database}» потеряна.");
                return;
            }

            var loaded = await Task.Run(() => ReadSchema(cs));
            _tables.Clear();
            _tables.AddRange(loaded.tables);
            _relations.Clear();
            _relations.AddRange(loaded.relations);
            LoadConfiguredGraphRelations();
            // Preserve manually arranged positions when the same project is refreshed.
            // The dictionary is cleared only when Configure switches to another DB.

            StartCompactGraphSync();

            int physical = _relations.Count(x => x.Kind.Equals("FK", StringComparison.OrdinalIgnoreCase));
            int logical = _relations.Count - physical;
            TxtGraphStatus.Text = $"Таблиц: {_tables.Count} · связей: {_relations.Count} · PK/FK: {physical} · логических: {logical}";
            RefreshRulesList();
        }
        catch (Exception ex)
        {
            ClearGraph($"Не удалось построить граф: {ex.Message}");
        }
    }

    private void LoadOfflineGraph()
    {
        try
        {
            _tables.Clear();
            _relations.Clear();
            if (_offlineProject is null)
            {
                TxtGraphStatus.Text = "Offline-проект не загружен.";
                return;
            }
            foreach (var schema in _offlineProject.TableSchemas)
            {
                var table = new GraphTable { FullName = schema.TableName, Name = schema.TableName.Contains('.') ? schema.TableName[(schema.TableName.IndexOf('.') + 1)..] : schema.TableName };
                table.Columns.AddRange(schema.Columns.Select(c => c.Name));
                table.PrimaryKeyColumns.AddRange(schema.KeyColumns);
                _tables.Add(table);
            }
            // Physical FK relations were captured during Online snapshot creation. Read only
            // the embedded model here; Offline must never re-analyze or contact SQL Server.
            if (_offlineProject.Architecture["physicalSchema"] is System.Text.Json.Nodes.JsonNode physicalNode)
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(physicalNode.ToJsonString());
                    if (document.RootElement.TryGetProperty("Relations", out var relationArray) || document.RootElement.TryGetProperty("relations", out relationArray))
                    {
                        foreach (var relation in relationArray.EnumerateArray())
                        {
                            string GetText(string upper, string lower) => relation.TryGetProperty(upper, out var v) || relation.TryGetProperty(lower, out v) ? v.ToString() : string.Empty;
                            var kind = GetText("Kind", "kind");
                            // Analyzer relation kinds include physical FK plus inferred/logical matches.
                            var isPhysical = kind.Equals("0", StringComparison.OrdinalIgnoreCase) || kind.Equals("PhysicalForeignKey", StringComparison.OrdinalIgnoreCase);
                            var source = GetText("SourceTable", "sourceTable");
                            var target = GetText("TargetTable", "targetTable");
                            string? ResolveTable(string name)
                            {
                                var exact = _tables.FirstOrDefault(t => t.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
                                if (exact != null) return exact.FullName;
                                var leaf = name.Split('.').Last().Trim('[', ']');
                                var matches = _tables.Where(t => t.FullName.Split('.').Last().Trim('[', ']').Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
                                return matches.Count == 1 ? matches[0].FullName : null;
                            }
                            source = ResolveTable(source) ?? string.Empty;
                            target = ResolveTable(target) ?? string.Empty;
                            if (source.Length == 0 || target.Length == 0) continue;
                            List<string> ReadColumns(string upper, string lower)
                            {
                                if (!(relation.TryGetProperty(upper, out var arr) || relation.TryGetProperty(lower, out arr)) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return new List<string>();
                                return arr.EnumerateArray().Select(x => x.ToString()).ToList();
                            }
                            var sourceColumns = ReadColumns("SourceColumns", "sourceColumns");
                            var targetColumns = ReadColumns("TargetColumns", "targetColumns");
                            var relationKind = isPhysical ? "FK" : "LOGICAL";
                            if (_relations.Any(r => r.Kind.Equals(relationKind, StringComparison.OrdinalIgnoreCase) && r.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                                r.Target.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                                r.SourceColumns.SequenceEqual(sourceColumns, StringComparer.OrdinalIgnoreCase) &&
                                r.TargetColumns.SequenceEqual(targetColumns, StringComparer.OrdinalIgnoreCase))) continue;
                            _relations.Add(new GraphRelation
                            {
                                Source = source, Target = target, Kind = relationKind, IsManual = !isPhysical,
                                Rule = GetText("Comment", "comment") is { Length: > 0 } comment ? comment : GetText("RuleId", "ruleId"),
                                SourceColumns = sourceColumns, TargetColumns = targetColumns
                            });
                        }
                    }
                }
                catch { /* Keep older/partial snapshots usable; never regenerate metadata via SQL in Offline. */ }
            }
            _graphRelationStore = new GraphRelationStore(_offlineProject);
            LoadConfiguredGraphRelations();
            StartCompactGraphSync();
            TxtGraphStatus.Text = $"Offline · таблиц: {_tables.Count} · логических связей: {_relations.Count}";
            RefreshRulesList();
        }
        catch (Exception ex) { ClearGraph($"Не удалось прочитать локальную архитектуру: {ex.Message}"); }
    }

    private void ClearGraph(string message)
    {
        _tables.Clear();
        _relations.Clear();
        _graphRelationDocument = new GraphRelationProjectDocument();
        _selectedGraphRelation = null;
        _selectedTable = null;
        GraphCanvas.Children.Clear();
        GraphTableList.SelectedItem = null;
        TxtGraphStatus.Text = message;
        RefreshRulesList();
        UpdateGraphRelationButtons();
    }

    private static (List<GraphTable> tables, List<GraphRelation> relations) ReadSchema(string cs)
    {
        var tables = new Dictionary<string, GraphTable>(StringComparer.OrdinalIgnoreCase);
        var relations = new Dictionary<string, GraphRelation>(StringComparer.OrdinalIgnoreCase);
        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(cs);
        Configurator.Core.Projects.SqlProviderGuard.OpenConnection(connection);

        using (var command = new SqlCommand(@"
SELECT t.TABLE_SCHEMA, t.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE,
       CASE WHEN EXISTS (
           SELECT 1 FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
           WHERE k.TABLE_SCHEMA=t.TABLE_SCHEMA AND k.TABLE_NAME=t.TABLE_NAME
             AND k.COLUMN_NAME=c.COLUMN_NAME
             AND OBJECTPROPERTY(OBJECT_ID(k.CONSTRAINT_SCHEMA + '.' + QUOTENAME(k.CONSTRAINT_NAME)), 'IsPrimaryKey') = 1
       ) THEN 1 ELSE 0 END AS IsPrimary
FROM INFORMATION_SCHEMA.TABLES t
JOIN INFORMATION_SCHEMA.COLUMNS c ON c.TABLE_SCHEMA=t.TABLE_SCHEMA AND c.TABLE_NAME=t.TABLE_NAME
WHERE t.TABLE_TYPE='BASE TABLE'
ORDER BY t.TABLE_NAME, c.ORDINAL_POSITION;", connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = Convert.ToString(reader["TABLE_SCHEMA"]) ?? "dbo";
                var name = Convert.ToString(reader["TABLE_NAME"]) ?? "";
                var key = schema + "." + name;
                if (!tables.TryGetValue(key, out var table))
                {
                    table = new GraphTable { FullName = key, Name = name };
                    tables[key] = table;
                }
                var column = Convert.ToString(reader["COLUMN_NAME"]) ?? "";
                table.Columns.Add(column);
                if (Convert.ToInt32(reader["IsPrimary"]) == 1) table.PrimaryKeyColumns.Add(column);
            }
        }

        using (var command = new SqlCommand(@"
SELECT sch1.name AS SourceSchema, tab1.name AS SourceTable, col1.name AS SourceColumn,
       sch2.name AS TargetSchema, tab2.name AS TargetTable, col2.name AS TargetColumn, fk.name AS ForeignKeyName
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
JOIN sys.tables tab1 ON tab1.object_id=fkc.parent_object_id
JOIN sys.schemas sch1 ON sch1.schema_id=tab1.schema_id
JOIN sys.columns col1 ON col1.object_id=fkc.parent_object_id AND col1.column_id=fkc.parent_column_id
JOIN sys.tables tab2 ON tab2.object_id=fkc.referenced_object_id
JOIN sys.schemas sch2 ON sch2.schema_id=tab2.schema_id
JOIN sys.columns col2 ON col2.object_id=fkc.referenced_object_id AND col2.column_id=fkc.referenced_column_id
WHERE tab1.is_ms_shipped=0 AND tab2.is_ms_shipped=0
ORDER BY SourceTable, ForeignKeyName, fkc.constraint_column_id;", connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var source = Convert.ToString(reader["SourceSchema"]) + "." + Convert.ToString(reader["SourceTable"]);
                var target = Convert.ToString(reader["TargetSchema"]) + "." + Convert.ToString(reader["TargetTable"]);
                var fk = Convert.ToString(reader["ForeignKeyName"]) ?? "FK";
                var id = "FK|" + fk + "|" + source + "|" + target;
                if (!relations.TryGetValue(id, out var relation))
                {
                    relation = new GraphRelation { Id = id, Source = source, Target = target, Kind = "FK", Rule = "Физическое ограничение БД: значения поля источника должны существовать в связанном поле таблицы назначения (" + fk + ").", IsManual = false };
                    relations[id] = relation;
                }
                relation.SourceColumns.Add(Convert.ToString(reader["SourceColumn"]) ?? "");
                relation.TargetColumns.Add(Convert.ToString(reader["TargetColumn"]) ?? "");
            }
        }

        AddKnownLogicalRelations(tables.Values.ToList(), relations);
        return (tables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(), relations.Values.ToList());
    }

    // FK/PK are only one source of truth. Many historical project databases lost
    // constraints during import/export, so these rules deliberately describe the
    // logical dependencies observed in the project family without creating SQL FKs.
    private static void AddKnownLogicalRelations(IReadOnlyList<GraphTable> tables, IDictionary<string, GraphRelation> relations)
    {
        AddClassLinks(tables, relations);
        AddCommonEntityLinks(tables, relations);
        AddPlcTypeLink(tables, relations);
        AddTypeLookups(tables, relations);
        AddProgramLinks(tables, relations);
    }

    private static void AddClassLinks(IReadOnlyList<GraphTable> tables, IDictionary<string, GraphRelation> relations)
    {
        var classes = Find(tables, "Classes");
        if (classes == null) return;
        foreach (var name in new[] { "Actions", "Events" })
        {
            var source = Find(tables, name);
            var targetColumn = FirstExisting(classes, "Record", "ID", "Id");
            if (source != null && Has(source, "Class") && targetColumn is not null)
                AddRelation(relations, source, classes, new[] { "Class" }, new[] { targetColumn }, "LOGICAL", "Class lookup");
        }
    }

    private static void AddCommonEntityLinks(IReadOnlyList<GraphTable> tables, IDictionary<string, GraphRelation> relations)
    {
        foreach (var source in tables)
        {
            if (source.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase) || source.Name.Equals("Classes", StringComparison.OrdinalIgnoreCase)) continue;
            AddSemanticLookup(tables, relations, source, "PLC", new[] { "PLC" }, new[] { "Record", "ID", "Id", "PLC" }, "PLC lookup");
            AddSemanticLookup(tables, relations, source, "Area", new[] { "Area" }, new[] { "Record", "ID", "Id", "AreaName" }, "Area lookup");
            AddSemanticLookup(tables, relations, source, "Unit", new[] { "Unit" }, new[] { "Record", "ID", "Id", "Name" }, "Unit lookup");
            AddSemanticLookup(tables, relations, source, "PLCClasssNumber", new[] { "PLCClasssNumber", "PLC_ClasssNumber", "PLCClassNumber", "PLC_ClassNumber" }, new[] { "Record", "ID", "Id", "PLCClasssNumber", "PLCClassNumber" }, "PLC class number lookup");
        }
    }

    private static void AddPlcTypeLink(IReadOnlyList<GraphTable> tables, IDictionary<string, GraphRelation> relations)
    {
        var plc = Find(tables, "PLC");
        if (plc == null) return;
        var target = FindAny(tables, "PLCTypes", "PLCType", "PlcTypes");
        if (target == null) return;
        var sourceColumn = FirstExisting(plc, "Type", "PLCType", "Type_Record");
        var targetColumn = FirstExisting(target, "Record", "ID", "Id", "Type");
        if (sourceColumn != null && targetColumn != null)
            AddRelation(relations, plc, target, new[] { sourceColumn }, new[] { targetColumn }, "LOGICAL", "PLC type lookup");
    }

    private static void AddTypeLookups(IReadOnlyList<GraphTable> tables, IDictionary<string, GraphRelation> relations)
    {
        foreach (var source in tables)
        {
            if (!Has(source, "Type")) continue;
            var candidates = new[]
            {
                source.Name + "_Type", source.Name + "_Types",
                source.Name + "Type", source.Name + "Types",
                source.Name.TrimEnd('s') + "_Type", source.Name.TrimEnd('s') + "_Types",
                source.Name.TrimEnd('s') + "Type", source.Name.TrimEnd('s') + "Types"
            };
            var target = candidates.Select(x => Find(tables, x)).FirstOrDefault(x => x != null);
            if (target == null || ReferenceEquals(source, target)) continue;
            var targetColumn = FirstExisting(target, "Record", "ID", "Id", "Type", "Name");
            if (targetColumn != null)
                AddRelation(relations, source, target, new[] { "Type" }, new[] { targetColumn }, "LOGICAL", "Type lookup");
        }
    }

    private static void AddProgramLinks(IReadOnlyList<GraphTable> tables, IDictionary<string, GraphRelation> relations)
    {
        AddChild(tables, relations, "Prog_Recipe", "Recipe_Record", "Prog", "Record", "Program recipe");
        AddChild(tables, relations, "Prog_Recipes", "Recipe_Record", "Prog_Recipe", "Record", "Program recipe values");
        AddChild(tables, relations, "Prog_QueueSelections", "Queue_Record", "Prog_Queue", "Record", "Program queue");
        AddChild(tables, relations, "Prog_QueueSelections", "Status_Record", "Statuses", "Record", "Program status");
        AddChild(tables, relations, "Prog_QueueSelections", "Matrix_Record", "Matrix_List", "Record", "Program matrix");
        AddChild(tables, relations, "Prog_QueueSelections", "Seq_Record", "Prog_Seq", "Record", "Program sequence");
    }

    private static void AddChild(IReadOnlyList<GraphTable> tables, IDictionary<string, GraphRelation> relations, string sourceName, string sourceColumn, string targetName, string targetColumn, string rule)
    {
        var source = Find(tables, sourceName);
        var target = Find(tables, targetName);
        if (source != null && target != null && Has(source, sourceColumn) && Has(target, targetColumn))
            AddRelation(relations, source, target, new[] { sourceColumn }, new[] { targetColumn }, "LOGICAL", rule);
    }

    private static void AddSemanticLookup(IReadOnlyList<GraphTable> tables, IDictionary<string, GraphRelation> relations, GraphTable source, string semantic, string[] sourceColumns, string[] targetColumns, string rule)
    {
        var target = semantic.Equals("PLC", StringComparison.OrdinalIgnoreCase) ? FindAny(tables, "PLC")
            : semantic.Equals("Area", StringComparison.OrdinalIgnoreCase) ? FindAny(tables, "Areas", "Area")
            : semantic.Equals("Unit", StringComparison.OrdinalIgnoreCase) ? FindAny(tables, "Units", "Unit")
            : FindAny(tables, "PLCClasssNumbers", "PLCClasssNumber", "PLCClassNumbers", "PLCClassNumber");
        if (target == null || ReferenceEquals(source, target)) return;
        var src = sourceColumns.FirstOrDefault(x => Has(source, x));
        var dst = targetColumns.FirstOrDefault(x => Has(target, x));
        if (src != null && dst != null) AddRelation(relations, source, target, new[] { src }, new[] { dst }, "LOGICAL", rule);
    }

    private static GraphTable? Find(IEnumerable<GraphTable> tables, string name) => tables.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static GraphTable? FindAny(IEnumerable<GraphTable> tables, params string[] names) => names.Select(n => Find(tables, n)).FirstOrDefault(x => x != null);
    private static bool Has(GraphTable table, string column) => table?.Columns.Any(x => x.Equals(column, StringComparison.OrdinalIgnoreCase)) == true;
    private static bool HasAny(GraphTable table, params string[] columns) => columns.Any(x => Has(table, x));
    private static string? FirstExisting(GraphTable table, params string[] columns) => columns.FirstOrDefault(x => Has(table, x));

    private static void AddRelation(IDictionary<string, GraphRelation> relations, GraphTable source, GraphTable target, IEnumerable<string> sourceColumns, IEnumerable<string> targetColumns, string kind, string rule)
    {
        var src = sourceColumns.ToList();
        var dst = targetColumns.ToList();
        var id = kind + "|" + source.FullName + "|" + target.FullName + "|" + string.Join(",", src) + "|" + string.Join(",", dst);
        if (relations.ContainsKey(id)) return;
        relations[id] = new GraphRelation { Id = id, Source = source.FullName, Target = target.FullName, Kind = kind, Rule = BuildRelationComment(source, target, src, dst, rule), SourceColumns = src, TargetColumns = dst, IsManual = false };
    }

    private static string BuildRelationComment(GraphTable source, GraphTable target, IReadOnlyList<string> sourceColumns, IReadOnlyList<string> targetColumns, string rule)
    {
        var sourceField = string.Join(" + ", sourceColumns);
        var targetField = string.Join(" + ", targetColumns);
        var sourceName = source?.FullName ?? "источник";
        var targetName = target?.FullName ?? "справочник";
        return $"Значение {sourceName}.{sourceField} выбирается из {targetName}.{targetField}.";
    }

    private static string ShortTableName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        var value = fullName.Trim();
        int dot = value.LastIndexOf('.');
        return dot >= 0 && dot < value.Length - 1 ? value[(dot + 1)..] : value;
    }

    private void RenderGraph(bool focusSelected = true)
    {
        if (GraphCanvas == null) return;
        string? retainedRelationId = _selectedGraphRelation?.Id;
        GraphCanvas.Children.Clear();
        _selectedGraphRelation = null;
        UpdateGraphRelationButtons();
        if (_tables.Count == 0 || string.IsNullOrWhiteSpace(_selectedTable)) return;

        const double nodeWidth = 210;
        const double nodeHeight = 80;
        var selected = _tables.FirstOrDefault(t => t.FullName.Equals(_selectedTable, StringComparison.OrdinalIgnoreCase));
        if (selected == null) return;
        // A new focus table gets a fresh compact neighborhood. Keeping coordinates
        // from earlier tables made unrelated neighborhoods spread across the canvas
        // and forced the fit zoom down until the graph became unreadable.
        if (!string.Equals(_graphLayoutRoot, selected.FullName, StringComparison.OrdinalIgnoreCase))
        {
            _graphPositions.Clear();
            _graphLayoutRoot = selected.FullName;
        }
        if (!string.IsNullOrWhiteSpace(retainedRelationId))
            _selectedGraphRelation = _relations.FirstOrDefault(relation => relation.Id.Equals(retainedRelationId, StringComparison.Ordinal)
                && (relation.Source.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase)
                    || relation.Target.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase)));

        bool showIncoming = ShowIncomingRelations?.IsChecked != false;
        bool showOutgoing = ShowOutgoingRelations?.IsChecked != false;
        // Filters affect the visible neighborhood, not only the relation strokes.
        // First choose the relations that should remain, then derive visible node blocks
        // from those relations. The selected node is always retained as the graph anchor.
        var allVisibleRelations = _relations.Where(r =>
            r.Source.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase) ||
            r.Target.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase))
            .Where(r =>
            {
                bool incoming = r.Target.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase);
                bool outgoing = r.Source.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase);
                if (incoming && !outgoing) return showIncoming;
                if (outgoing && !incoming) return showOutgoing;
                return showIncoming || showOutgoing;
            }).ToList();
        var visibleRelations = allVisibleRelations;
        var visibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selected.FullName };
        foreach (var relation in visibleRelations)
        {
            visibleNames.Add(relation.Source);
            visibleNames.Add(relation.Target);
        }
        var visibleTables = _tables.Where(t => visibleNames.Contains(t.FullName)).ToList();
        EnsureSelectedGraphPositions(selected, visibleTables);
        var routeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var relation in visibleRelations)
        {
            var a = _graphPositions[relation.Source];
            var b = _graphPositions[relation.Target];
            bool selectedRelation = ReferenceEquals(_selectedGraphRelation, relation);
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            // Prefer the side facing the target. If the blocks are mostly aligned vertically,
            // use top/bottom; otherwise use left/right. Parallel links get their own compact lanes.
            bool horizontal = Math.Abs(dx) >= Math.Abs(dy) * 0.85;
            string laneKey = horizontal ? "H|" + Math.Min(a.Y,b.Y) + "|" + Math.Max(a.Y,b.Y) : "V|" + Math.Min(a.X,b.X) + "|" + Math.Max(a.X,b.X);
            routeCounts.TryGetValue(laneKey, out int laneIndex);
            routeCounts[laneKey] = laneIndex + 1;
            double laneOffset = (laneIndex == 0 ? 0 : ((laneIndex + 1) / 2) * 30 * (laneIndex % 2 == 1 ? 1 : -1));
            Point startPoint, endPoint;
            PointCollection route;
            if (horizontal)
            {
                double signX = dx >= 0 ? 1 : -1;
                startPoint = SnapPoint(new Point(a.X + signX * nodeWidth / 2, a.Y + laneOffset));
                endPoint = SnapPoint(new Point(b.X - signX * nodeWidth / 2, b.Y + laneOffset));
                double midX = SnapToGrid((startPoint.X + endPoint.X) / 2 + laneOffset);
                route = new PointCollection { startPoint, SnapPoint(new Point(midX, startPoint.Y)), SnapPoint(new Point(midX, endPoint.Y)), endPoint };
            }
            else
            {
                double signY = dy >= 0 ? 1 : -1;
                startPoint = SnapPoint(new Point(a.X + laneOffset, a.Y + signY * nodeHeight / 2));
                endPoint = SnapPoint(new Point(b.X + laneOffset, b.Y - signY * nodeHeight / 2));
                double midY = SnapToGrid((startPoint.Y + endPoint.Y) / 2 + laneOffset);
                route = new PointCollection { startPoint, SnapPoint(new Point(startPoint.X, midY)), SnapPoint(new Point(endPoint.X, midY)), endPoint };
            }
            for (int i = route.Count - 1; i > 0; i--)
                if (route[i] == route[i - 1]) route.RemoveAt(i);
            var line = new Polyline
            {
                Points = route,
                StrokeThickness = selectedRelation ? 3.2 : (relation.Source.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase) || relation.Target.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase) ? 2.5 : 1.2),
                Opacity = selectedRelation ? 1 : 0.82,
                Cursor = Cursors.Hand, Tag = relation, Fill = Brushes.Transparent, IsHitTestVisible = true,
                ToolTip = BuildRelationSummary(relation)
            };
            line.SetResourceReference(Shape.StrokeProperty, relation.Kind.Equals("FK", StringComparison.OrdinalIgnoreCase) ? "BrushAccent" : "BrushTextSecondary");
            if (!relation.Kind.Equals("FK", StringComparison.OrdinalIgnoreCase)) line.StrokeDashArray = new DoubleCollection { 6, 4 };
            line.MouseLeftButtonDown += GraphRelation_MouseLeftButtonDown;
            GraphCanvas.Children.Add(line);
            AddRelationArrow(relation, endPoint, route.Count >= 2 ? route[route.Count - 2] : startPoint, selectedRelation);

            if (!_overviewMode || selectedRelation || allVisibleRelations.Count <= 4)
            {
                var label = new Border { Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(4), IsHitTestVisible = true,
                    Cursor = Cursors.Hand, Tag = relation, ToolTip = BuildRelationSummary(relation) + "\n\nНажмите, чтобы выделить связь" };
                label.SetResourceReference(Border.BackgroundProperty, "BrushBase");
                label.SetResourceReference(Border.BorderBrushProperty, selectedRelation ? "BrushAccent" : "BrushBorder");
                label.BorderThickness = new Thickness(selectedRelation ? 2 : 1);
                var labelText = new TextBlock { Text = _overviewMode ? BuildCompactRelationLabel(relation) : BuildRelationSummary(relation), FontSize = _overviewMode ? 9 : 12,
                    FontWeight = selectedRelation ? FontWeights.SemiBold : FontWeights.Normal, MaxWidth = _overviewMode ? 210 : 620, TextWrapping = TextWrapping.Wrap };
                labelText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary"); label.Child = labelText;
                label.MouseLeftButtonDown += GraphRelationLabel_MouseLeftButtonDown; GraphCanvas.Children.Add(label);
                Dispatcher.BeginInvoke(new Action(() => { var size = label.RenderSize; Canvas.SetLeft(label, (startPoint.X + endPoint.X) / 2 - size.Width / 2); Canvas.SetTop(label, (startPoint.Y + endPoint.Y) / 2 - size.Height - 4); }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        foreach (var table in visibleTables)
        {
            var point = _graphPositions[table.FullName];
            bool isSelected = table.FullName.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase);
            var border = new Border
            {
                Width = nodeWidth,
                Height = nodeHeight,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8),
                Cursor = Cursors.SizeAll,
                ToolTip = BuildTableToolTip(table),
                Tag = table.FullName
            };
            border.SetResourceReference(Border.BackgroundProperty, isSelected ? "BrushSelected" : "BrushPanel");
            border.SetResourceReference(Border.BorderBrushProperty, isSelected ? "BrushAccent" : "BrushBorder");
            border.BorderThickness = new Thickness(isSelected ? 2 : 1);
            var stack = new StackPanel();
            var name = new TextBlock { Text = table.Name, FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");
            stack.Children.Add(name);
            var key = new TextBlock { Text = table.PrimaryKeyColumns.Count == 0 ? "PK не объявлен" : "PK: " + string.Join(", ", table.PrimaryKeyColumns), FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis };
            key.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            stack.Children.Add(key);
            var cols = new TextBlock { Text = $"полей: {table.Columns.Count}", FontSize = 9 };
            cols.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            stack.Children.Add(cols);
            border.Child = stack;
            border.MouseLeftButtonDown += GraphNode_MouseLeftButtonDown;
            Canvas.SetLeft(border, point.X - nodeWidth / 2);
            Canvas.SetTop(border, point.Y - nodeHeight / 2);
            GraphCanvas.Children.Add(border);
        }

        int relationCount = visibleRelations.Count;
        int hiddenRelationCount = allVisibleRelations.Count - relationCount;
        TxtGraphScope.Text = $"Выбрана: {selected.Name} · показано таблиц: {visibleTables.Count} · связей: {relationCount}";
        if (GraphDensityStatus != null)
        {
            GraphDensityStatus.Text = hiddenRelationCount > 0 ? $"Показано {relationCount} из {allVisibleRelations.Count}" : "";
            GraphDensityStatus.Visibility = hiddenRelationCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateGraphRelationButtons();
        if (focusSelected) FitGraphToVisibleNodes(visibleTables);
    }

    private static bool IsCoreGraphTable(string fullName)
        => new[] { "PLC", "Areas", "Classes" }.Contains(ShortTableName(fullName), StringComparer.OrdinalIgnoreCase);

    private static double SnapToGrid(double value) => Math.Round(value / 10.0) * 10.0;
    private static Point SnapPoint(Point point) => new(SnapToGrid(point.X), SnapToGrid(point.Y));

    private static Point GetNodeSidePoint(Point source, Point target, double nodeWidth, double nodeHeight, bool isSource)
    {
        // Ports are placed on the top/bottom edge; the route builder selects which edge per relation.
        var origin = isSource ? source : target;
        bool useTop = target.Y <= source.Y;
        double edgeY = origin.Y + (useTop ? -nodeHeight / 2 : nodeHeight / 2);
        return SnapPoint(new Point(origin.X, edgeY));
    }

    private void AddRelationArrow(GraphRelation relation, Point tip, Point from, bool selectedRelation)
    {
        var dx = tip.X - from.X;
        var dy = tip.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return;
        var ux = dx / length;
        var uy = dy / length;
        var px = -uy;
        var py = ux;
        const double arrowLength = 13;
        const double arrowWidth = 6.5;
        var basePoint = new Point(tip.X - ux * arrowLength, tip.Y - uy * arrowLength);
        var left = new Point(basePoint.X + px * arrowWidth, basePoint.Y + py * arrowWidth);
        var right = new Point(basePoint.X - px * arrowWidth, basePoint.Y - py * arrowWidth);
        var polygon = new Polygon
        {
            Points = new PointCollection { tip, left, right },
            Tag = relation,
            Cursor = relation.Kind.Equals("LOGICAL", StringComparison.OrdinalIgnoreCase) ? Cursors.Hand : Cursors.Arrow,
            IsHitTestVisible = false
        };
        polygon.SetResourceReference(Shape.FillProperty, relation.Kind.Equals("FK", StringComparison.OrdinalIgnoreCase) ? "BrushAccent" : "BrushTextSecondary");
        polygon.Opacity = selectedRelation ? 1 : 0.9;
        GraphCanvas.Children.Add(polygon);
    }

    private void FitGraphToVisibleNodes(IReadOnlyList<GraphTable> visibleTables)
    {
        if (GraphScrollViewer == null || visibleTables == null || visibleTables.Count == 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            GraphScrollViewer.UpdateLayout();
            double viewportWidth = GraphScrollViewer.ViewportWidth;
            double viewportHeight = GraphScrollViewer.ViewportHeight;
            if (viewportWidth <= 0 || viewportHeight <= 0) return;

            const double nodeWidth = 210;
            const double nodeHeight = 80;
            var points = visibleTables.Where(t => _graphPositions.ContainsKey(t.FullName))
                .Select(t => _graphPositions[t.FullName]).ToList();
            if (points.Count == 0) return;
            double minX = points.Min(p => p.X) - nodeWidth / 2 - 100;
            double maxX = points.Max(p => p.X) + nodeWidth / 2 + 100;
            double minY = points.Min(p => p.Y) - nodeHeight / 2 - 140;
            double maxY = points.Max(p => p.Y) + nodeHeight / 2 + 140;
            double contentWidth = Math.Max(1, maxX - minX);
            double contentHeight = Math.Max(1, maxY - minY);
            double fitZoom = Math.Min((viewportWidth - 24) / contentWidth, (viewportHeight - 24) / contentHeight);
            fitZoom = Math.Max(GraphMinZoom, Math.Min(1.0, fitZoom));
            _graphZoom = fitZoom;
            GraphScale.ScaleX = _graphZoom;
            GraphScale.ScaleY = _graphZoom;
            GraphSurface.Width = GraphBaseWidth * _graphZoom;
            GraphSurface.Height = GraphBaseHeight * _graphZoom;
            GraphCanvas.Width = GraphBaseWidth;
            GraphCanvas.Height = GraphBaseHeight;
            GraphScrollViewer.UpdateLayout();
            double centerX = (minX + maxX) / 2 * _graphZoom;
            double centerY = (minY + maxY) / 2 * _graphZoom;
            GraphScrollViewer.ScrollToHorizontalOffset(Math.Max(0, Math.Min(GraphScrollViewer.ScrollableWidth, centerX - viewportWidth / 2)));
            GraphScrollViewer.ScrollToVerticalOffset(Math.Max(0, Math.Min(GraphScrollViewer.ScrollableHeight, centerY - viewportHeight / 2)));
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FocusSelectedTable(GraphTable selected)
    {
        if (selected == null || GraphScrollViewer == null || !_graphPositions.TryGetValue(selected.FullName, out var point)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var viewportWidth = GraphScrollViewer.ViewportWidth;
            var viewportHeight = GraphScrollViewer.ViewportHeight;
            if (viewportWidth <= 0 || viewportHeight <= 0) return;
            var targetX = point.X * _graphZoom - viewportWidth / 2;
            var targetY = point.Y * _graphZoom - viewportHeight / 2;
            GraphScrollViewer.ScrollToHorizontalOffset(Math.Max(0, Math.Min(GraphScrollViewer.ScrollableWidth, targetX)));
            GraphScrollViewer.ScrollToVerticalOffset(Math.Max(0, Math.Min(GraphScrollViewer.ScrollableHeight, targetY)));
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RelationFilter_Changed(object sender, RoutedEventArgs e)
    {
        // Keep the state explicit in text as well as color; this remains clear for
        // users who cannot distinguish the green/neutral colors.
        if (ShowIncomingRelations != null)
            ShowIncomingRelations.Content = ShowIncomingRelations.IsChecked == true ? "↓ Входящие · ВКЛ" : "↓ Входящие · ВЫКЛ";
        if (ShowOutgoingRelations != null)
            ShowOutgoingRelations.Content = ShowOutgoingRelations.IsChecked == true ? "↑ Исходящие · ВКЛ" : "↑ Исходящие · ВЫКЛ";
        if (GraphCanvas == null || _tables.Count == 0) return;
        RenderGraph(focusSelected: false);
    }

    private void EnsureSelectedGraphPositions(GraphTable selected, IReadOnlyList<GraphTable> visibleTables)
    {
        const double centerX = GraphBaseWidth / 2;
        const double centerY = GraphBaseHeight / 2;
        // Rebuild the visible neighborhood as a compact radial layout whenever the selected
        // table changes. Existing manual positions are retained during ordinary redraws.
        if (!_graphPositions.ContainsKey(selected.FullName)) _graphPositions[selected.FullName] = SnapPoint(new Point(centerX, centerY));
        var center = _graphPositions[selected.FullName];
        var neighbors = visibleTables.Where(t => !t.FullName.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase))
            .Select(t => new { Table = t, Incoming = _relations.Any(r => r.Source.Equals(t.FullName, StringComparison.OrdinalIgnoreCase) && r.Target.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase)),
                Outgoing = _relations.Any(r => r.Source.Equals(selected.FullName, StringComparison.OrdinalIgnoreCase) && r.Target.Equals(t.FullName, StringComparison.OrdinalIgnoreCase)) })
            .OrderByDescending(x => x.Incoming != x.Outgoing).ThenBy(x => x.Table.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int left=0,right=0,top=0,bottom=0;
        int crowdingLevel = neighbors.Count > 12 ? 2 : neighbors.Count > 6 ? 1 : 0;
        double horizontalGap = crowdingLevel == 2 ? 520 : crowdingLevel == 1 ? 430 : 360;
        double verticalGap = crowdingLevel == 2 ? 205 : crowdingLevel == 1 ? 175 : 150;
        foreach (var item in neighbors)
        {
            if (_graphPositions.ContainsKey(item.Table.FullName)) continue;
            // Direction of the dependency influences the quadrant; spread nodes to avoid crowded ports.
            int index; Point point;
            if (item.Incoming && !item.Outgoing) { index=left++; point=new Point(center.X-horizontalGap, center.Y+(index%2==0?1:-1)*((index/2)+1)*verticalGap); }
            else if (item.Outgoing && !item.Incoming) { index=right++; point=new Point(center.X+horizontalGap, center.Y+(index%2==0?1:-1)*((index/2)+1)*verticalGap); }
            else if ((top++ % 2)==0) { index=top/2; point=new Point(center.X+(index%2==0?1:-1)*(index+1)*(crowdingLevel == 2 ? 340 : 280), center.Y-(crowdingLevel == 2 ? 310 : 260)); }
            else { index=bottom++; point=new Point(center.X+(index%2==0?1:-1)*(index+1)*(crowdingLevel == 2 ? 340 : 280), center.Y+(crowdingLevel == 2 ? 310 : 260)); }
            _graphPositions[item.Table.FullName] = SnapPoint(point);
        }
    }

    private void GraphRelationLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border label && label.Tag is GraphRelation relation)
        {
            _selectedGraphRelation = relation;
            RenderGraph(focusSelected: false);
            e.Handled = true;
        }
    }

    private void GraphRelation_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Shape shape && shape.Tag is GraphRelation relation)
        {
            _selectedGraphRelation = relation;
            RenderGraph(focusSelected: false);
            e.Handled = true;
        }
    }

    private void RenderGraphKeepingRelationSelection(GraphRelation relation)
    {
        RenderGraph();
        _selectedGraphRelation = _relations.FirstOrDefault(x => x.Id == relation.Id);
        UpdateGraphRelationButtons();
    }

    private void LoadConfiguredGraphRelations()
    {
        if (_offlineProject == null && (_connection == null || string.IsNullOrWhiteSpace(_database))) return;
        _graphRelationStore ??= _offlineProject != null ? new GraphRelationStore(_offlineProject) : new GraphRelationStore(_connection?.Server ?? string.Empty, _database);
        _graphRelationDocument = _graphRelationStore.Load();
        // The package's dedicated GraphRelations document is authoritative. Do not restore
        // legacy Architecture["graphRelations"] when the current document is intentionally empty:
        // that resurrected deleted relations whenever the user switched Mode 2 subtabs.

        string? ResolveGraphTable(string name)
        {
            var exact = _tables.FirstOrDefault(t => t.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact.FullName;
            var leaf = (name ?? string.Empty).Split('.').Last().Trim('[', ']');
            var matches = _tables.Where(t => t.FullName.Split('.').Last().Trim('[', ']').Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count == 1 ? matches[0].FullName : null;
        }
        foreach (var manual in _graphRelationDocument.ManualRelations ?? new List<GraphRelationDefinition>())
        {
            var resolvedSource = ResolveGraphTable(manual.Source);
            var resolvedTarget = ResolveGraphTable(manual.Target);
            if (resolvedSource == null || resolvedTarget == null) continue;
            if (_relations.Any(r => r.Kind.Equals("LOGICAL", StringComparison.OrdinalIgnoreCase) &&
                                    r.Source.Equals(resolvedSource, StringComparison.OrdinalIgnoreCase) &&
                                    r.Target.Equals(resolvedTarget, StringComparison.OrdinalIgnoreCase) &&
                                    r.SourceColumns.SequenceEqual(new[] { manual.SourceColumn }, StringComparer.OrdinalIgnoreCase) &&
                                    r.TargetColumns.SequenceEqual(new[] { manual.TargetColumn }, StringComparer.OrdinalIgnoreCase))) continue;
            _relations.Add(new GraphRelation
            {
                Source = resolvedSource, Target = resolvedTarget, Kind = "LOGICAL", Rule = manual.Rule,
                SourceColumns = new List<string> { manual.SourceColumn }, TargetColumns = new List<string> { manual.TargetColumn }, IsManual = true
            });
        }

        var suppressed = new HashSet<string>(_graphRelationDocument.SuppressedAutomaticRelations ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        _relations.RemoveAll(r => r.Kind.Equals("LOGICAL", StringComparison.OrdinalIgnoreCase) && !r.IsManual && suppressed.Contains(r.Id));
    }

    private void AddManualGraphRelation()
    {
        if (_offlineProject == null && (_connection == null || string.IsNullOrWhiteSpace(_database))) return;
        if (_tables.Count == 0)
        {
            Configurator.Infrastructure.ModernDialog.Show("Сначала загрузите список таблиц проекта.", "Связь", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var items = _tables.Select(t => new GraphRelationEditorWindow.GraphTableEditorItem
        {
            FullName = t.FullName, Name = t.Name, Columns = t.Columns.ToList()
        }).ToList();
        var dialog = new GraphRelationEditorWindow(items, _selectedTable) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result == null) return;
        var def = dialog.Result;
        bool exists = _relations.Any(r => r.Source.Equals(def.Source, StringComparison.OrdinalIgnoreCase) && r.Target.Equals(def.Target, StringComparison.OrdinalIgnoreCase) &&
                                          r.SourceColumns.SequenceEqual(new[] { def.SourceColumn }, StringComparer.OrdinalIgnoreCase) && r.TargetColumns.SequenceEqual(new[] { def.TargetColumn }, StringComparer.OrdinalIgnoreCase));
        if (exists)
        {
            Configurator.Infrastructure.ModernDialog.Show("Такая связь уже существует.", "Связь", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        _graphRelationDocument.ManualRelations ??= new List<GraphRelationDefinition>();
        _graphRelationDocument.ManualRelations.Add(def);
        _graphRelationStore ??= _offlineProject != null ? new GraphRelationStore(_offlineProject) : new GraphRelationStore(_connection?.Server ?? string.Empty, _database);
        _graphRelationStore.Save(_graphRelationDocument);
        LoadConfiguredGraphRelations();
        RenderGraph();
        RefreshRulesList();
        TxtGraphStatus.Text = $"Добавлена логическая связь: {def.Source} → {def.Target}";
        ActionLogged?.Invoke("Добавлена логическая связь", def.Source, $"{def.Source}.{def.SourceColumn} → {def.Target}.{def.TargetColumn}; правило={def.Rule}");
    }

    private void RemoveSelectedGraphRelation()
    {
        if (_selectedGraphRelation == null || !_selectedGraphRelation.Kind.Equals("LOGICAL", StringComparison.OrdinalIgnoreCase)) return;
        var relation = _selectedGraphRelation;
        var text = $"Разорвать логическую связь «{relation.Source} → {relation.Target}»?\n\n{relation.SourceColumns.FirstOrDefault()} → {relation.TargetColumns.FirstOrDefault()}";
        if (Configurator.Infrastructure.ModernDialog.Show(text, "Удаление связи", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _graphRelationStore ??= _offlineProject != null ? new GraphRelationStore(_offlineProject) : new GraphRelationStore(_connection?.Server ?? string.Empty, _database);
        _graphRelationDocument.ManualRelations ??= new List<GraphRelationDefinition>();
        _graphRelationDocument.SuppressedAutomaticRelations ??= new List<string>();

        if (relation.IsManual)
        {
            _graphRelationDocument.ManualRelations.RemoveAll(x => x.Source.Equals(relation.Source, StringComparison.OrdinalIgnoreCase) && x.Target.Equals(relation.Target, StringComparison.OrdinalIgnoreCase) &&
                x.SourceColumn.Equals(relation.SourceColumns.FirstOrDefault() ?? "", StringComparison.OrdinalIgnoreCase) && x.TargetColumn.Equals(relation.TargetColumns.FirstOrDefault() ?? "", StringComparison.OrdinalIgnoreCase));
        }
        else if (!_graphRelationDocument.SuppressedAutomaticRelations.Contains(relation.Id, StringComparer.OrdinalIgnoreCase))
        {
            _graphRelationDocument.SuppressedAutomaticRelations.Add(relation.Id);
        }
        _graphRelationStore.Save(_graphRelationDocument);
        var restorePayload = new GraphRelationRestorePayload
        {
            Relation = new GraphRelationDefinition
            {
                Source = relation.Source,
                Target = relation.Target,
                SourceColumn = relation.SourceColumns.FirstOrDefault() ?? "",
                TargetColumn = relation.TargetColumns.FirstOrDefault() ?? "",
                Rule = relation.Rule ?? ""
            },
            WasManual = relation.IsManual,
            SuppressedRelationId = relation.IsManual ? "" : relation.Id
        };
        LogicalRelationDeleted?.Invoke(restorePayload);
        _relations.Remove(relation);
        _selectedGraphRelation = null;
        RenderGraph();
        RefreshRulesList();
        TxtGraphStatus.Text = "Логическая связь удалена из графа.";
        ActionLogged?.Invoke("Удалена логическая связь", relation.Source, $"{relation.Source}.{relation.SourceColumns.FirstOrDefault()} → {relation.Target}.{relation.TargetColumns.FirstOrDefault()}; правило={relation.Rule}");
    }

    private void GraphNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tableName) return;
        _draggingTable = tableName;
        _draggingNodeVisual = element as Border;
        var pointer = e.GetPosition(GraphCanvas);
        var current = _graphPositions.TryGetValue(tableName, out var p) ? p : pointer;
        _dragOffset = new Point(pointer.X - current.X, pointer.Y - current.Y);
        GraphCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingTable == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = e.GetPosition(GraphCanvas);
        double x = SnapToGrid(Math.Max(140, Math.Min(GraphBaseWidth - 140, pointer.X - _dragOffset.X)));
        double y = SnapToGrid(Math.Max(70, Math.Min(GraphBaseHeight - 70, pointer.Y - _dragOffset.Y)));
        var next = new Point(x, y);
        _graphPositions[_draggingTable] = next;
        // Update the existing node in place during drag. Rebuilding the entire
        // Canvas on every mouse move caused visible flashing and label flicker.
        if (_draggingNodeVisual != null)
        {
            Canvas.SetLeft(_draggingNodeVisual, next.X - _draggingNodeVisual.Width / 2);
            Canvas.SetTop(_draggingNodeVisual, next.Y - _draggingNodeVisual.Height / 2);
        }
        UpdateDraggedRelationGeometry(_draggingTable);
    }

    // Move only the visuals attached to the dragged node. Rebuilding the whole
    // canvas on each mouse move causes flicker; this updates existing polylines,
    // arrowheads and labels in place. A final RenderGraph on mouse-up restores
    // optimal lanes for parallel relations.
    private void UpdateDraggedRelationGeometry(string tableName)
    {
        if (GraphCanvas == null || string.IsNullOrWhiteSpace(tableName)) return;
        const double nodeWidth = 210;
        const double nodeHeight = 80;
        foreach (var line in GraphCanvas.Children.OfType<Polyline>().Where(x => x.Tag is GraphRelation r &&
                     (r.Source.Equals(tableName, StringComparison.OrdinalIgnoreCase) || r.Target.Equals(tableName, StringComparison.OrdinalIgnoreCase))))
        {
            var relation = (GraphRelation)line.Tag;
            if (!_graphPositions.TryGetValue(relation.Source, out var a) || !_graphPositions.TryGetValue(relation.Target, out var b)) continue;
            double dx = b.X - a.X, dy = b.Y - a.Y;
            bool horizontal = Math.Abs(dx) >= Math.Abs(dy) * 0.85;
            Point start, end;
            PointCollection route;
            if (horizontal)
            {
                double sign = dx >= 0 ? 1 : -1;
                start = SnapPoint(new Point(a.X + sign * nodeWidth / 2, a.Y));
                end = SnapPoint(new Point(b.X - sign * nodeWidth / 2, b.Y));
                double midX = SnapToGrid((start.X + end.X) / 2);
                route = new PointCollection { start, SnapPoint(new Point(midX, start.Y)), SnapPoint(new Point(midX, end.Y)), end };
            }
            else
            {
                double sign = dy >= 0 ? 1 : -1;
                start = SnapPoint(new Point(a.X, a.Y + sign * nodeHeight / 2));
                end = SnapPoint(new Point(b.X, b.Y - sign * nodeHeight / 2));
                double midY = SnapToGrid((start.Y + end.Y) / 2);
                route = new PointCollection { start, SnapPoint(new Point(start.X, midY)), SnapPoint(new Point(end.X, midY)), end };
            }
            for (int i = route.Count - 1; i > 0; i--) if (route[i] == route[i - 1]) route.RemoveAt(i);
            line.Points = route;
            var arrow = GraphCanvas.Children.OfType<Polygon>().FirstOrDefault(x => ReferenceEquals(x.Tag, relation));
            if (arrow != null) SetArrowPoints(arrow, end, route.Count >= 2 ? route[route.Count - 2] : start);
            foreach (var label in GraphCanvas.Children.OfType<Border>().Where(x => ReferenceEquals(x.Tag, relation)))
            {
                label.UpdateLayout();
                Canvas.SetLeft(label, (start.X + end.X) / 2 - label.ActualWidth / 2);
                Canvas.SetTop(label, (start.Y + end.Y) / 2 - label.ActualHeight - 4);
            }
        }
    }

    private static void SetArrowPoints(Polygon polygon, Point tip, Point from)
    {
        double dx = tip.X - from.X, dy = tip.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return;
        double ux = dx / length, uy = dy / length, px = -uy, py = ux;
        const double arrowLength = 13, arrowWidth = 6.5;
        var basePoint = new Point(tip.X - ux * arrowLength, tip.Y - uy * arrowLength);
        polygon.Points = new PointCollection { tip, new Point(basePoint.X + px * arrowWidth, basePoint.Y + py * arrowWidth), new Point(basePoint.X - px * arrowWidth, basePoint.Y - py * arrowWidth) };
    }

    private void GraphCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (GraphScrollViewer == null || GraphScale == null || GraphSurface == null) return;

        // Zoom around the main hub table (the node with the most connections),
        // so repeated wheel actions keep the user focused on the central object.
        var hub = _tables
            .Select(t => new { Table = t, Degree = _relations.Count(r => r.Source.Equals(t.FullName, StringComparison.OrdinalIgnoreCase) || r.Target.Equals(t.FullName, StringComparison.OrdinalIgnoreCase)) })
            .OrderByDescending(x => x.Degree)
            .ThenBy(x => x.Table.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        Point focus = e.GetPosition(GraphScrollViewer);
        if (hub != null && _graphPositions.TryGetValue(hub.Table.FullName, out var hubPoint))
            focus = new Point(hubPoint.X * _graphZoom - GraphScrollViewer.HorizontalOffset, hubPoint.Y * _graphZoom - GraphScrollViewer.VerticalOffset);
        double factor = e.Delta > 0 ? 1.10 : 1.0 / 1.10;
        SetGraphZoom(_graphZoom * factor, focus);
        e.Handled = true;
    }

    private void GraphCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GraphScrollViewer == null) return;
        _panningGraph = true;
        _panStartPoint = e.GetPosition(GraphScrollViewer);
        _panStartHorizontalOffset = GraphScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = GraphScrollViewer.VerticalOffset;
        GraphCanvas.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Hand;
        e.Handled = true;
    }

    private void GraphCanvas_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panningGraph) return;
        _panningGraph = false;
        GraphCanvas.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        e.Handled = true;
    }

    private void GraphCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panningGraph || GraphScrollViewer == null || e.RightButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(GraphScrollViewer);
        GraphScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - (point.X - _panStartPoint.X));
        GraphScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - (point.Y - _panStartPoint.Y));
        e.Handled = true;
    }

    private void SetGraphZoom(double zoom, Point? viewportPoint)
    {
        double next = Math.Max(GraphMinZoom, Math.Min(GraphMaxZoom, zoom));
        if (Math.Abs(next - _graphZoom) < 0.0001 && viewportPoint == null) return;

        var oldZoom = _graphZoom;
        if (viewportPoint.HasValue && GraphScrollViewer != null && Math.Abs(oldZoom) > 0.0001)
        {
            // Keep the point under the mouse stationary while changing scale.
            var p = viewportPoint.Value;
            double worldX = (GraphScrollViewer.HorizontalOffset + p.X) / oldZoom;
            double worldY = (GraphScrollViewer.VerticalOffset + p.Y) / oldZoom;

            _graphZoom = next;
            GraphScale.ScaleX = _graphZoom;
            GraphScale.ScaleY = _graphZoom;
            GraphSurface.Width = GraphBaseWidth * _graphZoom;
            GraphSurface.Height = GraphBaseHeight * _graphZoom;
            GraphCanvas.Width = GraphBaseWidth;
            GraphCanvas.Height = GraphBaseHeight;
            GraphScrollViewer.UpdateLayout();
            GraphScrollViewer.ScrollToHorizontalOffset(Math.Max(0, worldX * _graphZoom - p.X));
            GraphScrollViewer.ScrollToVerticalOffset(Math.Max(0, worldY * _graphZoom - p.Y));
            return;
        }

        _graphZoom = next;
        GraphScale.ScaleX = _graphZoom;
        GraphScale.ScaleY = _graphZoom;
        GraphSurface.Width = GraphBaseWidth * _graphZoom;
        GraphSurface.Height = GraphBaseHeight * _graphZoom;
        GraphCanvas.Width = GraphBaseWidth;
        GraphCanvas.Height = GraphBaseHeight;
    }

    private void GraphCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingTable == null) return;
        _draggingTable = null;
        _draggingNodeVisual = null;
        GraphCanvas.ReleaseMouseCapture();
        // Re-route connections once after the drag, not on every pointer event.
        RenderGraph(focusSelected: false);
        e.Handled = true;
    }

    private string BuildCompactRelationLabel(GraphRelation relation)
    {
        string sourceColumn = string.Join(",", relation.SourceColumns);
        string targetColumn = string.Join(",", relation.TargetColumns);
        return $"{ShortTableName(relation.Source)} · {sourceColumn} → {ShortTableName(relation.Target)} · {targetColumn}";
    }

    private string BuildRelationSummary(GraphRelation relation)
    {
        string sourceColumns = string.Join(", ", relation.SourceColumns);
        string targetColumns = string.Join(", ", relation.TargetColumns);
        string kind = relation.Kind.Equals("FK", StringComparison.OrdinalIgnoreCase)
            ? "Связь задана в базе данных"
            : "Связь настроена в проекте";
        return $"{kind}\nПоле «{sourceColumns}» таблицы «{ShortTableName(relation.Source)}» использует значение из поля «{targetColumns}» таблицы «{ShortTableName(relation.Target)}».";
    }

    private static string BuildTableToolTip(GraphTable table) => $"{table.FullName}\n{(table.PrimaryKeyColumns.Count == 0 ? "PK: не объявлен" : "PK: " + string.Join(", ", table.PrimaryKeyColumns))}\n\nПоля:\n{string.Join(", ", table.Columns)}";

    private sealed class GraphTable
    {
        public string FullName { get; init; } = "";
        public string Name { get; init; } = "";
        public List<string> Columns { get; } = new();
        public List<string> PrimaryKeyColumns { get; } = new();
        public bool Has(string column) => Columns.Any(x => x.Equals(column, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class GraphRelation
    {
        public string Id { get; init; } = "";
        public string Source { get; init; } = "";
        public string Target { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Rule { get; init; } = "";
        public List<string> SourceColumns { get; init; } = new();
        public List<string> TargetColumns { get; init; } = new();
        public bool IsManual { get; init; }
        public string SourceColumnDisplay => string.Join(", ", SourceColumns);
        public string TargetColumnDisplay => string.Join(", ", TargetColumns);
        public string KindDisplay => Kind.Equals("FK", StringComparison.OrdinalIgnoreCase) ? "SQL FK" : "Логическая";
        public string GroupDisplay => Kind.Equals("FK", StringComparison.OrdinalIgnoreCase) ? "SQL СВЯЗИ" : "ЛОГИЧЕСКИЕ СВЯЗИ";
    }
}
