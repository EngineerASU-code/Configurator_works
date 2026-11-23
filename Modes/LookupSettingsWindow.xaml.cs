using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;

namespace Configurator;

public partial class LookupSettingsWindow : Window
{
    private readonly ConnectionSettings _connection;
    private readonly List<string> _databases;
    private readonly IReadOnlyList<LookupDefinition> _definitions = SystemLookupCatalog.All;
    private static readonly HashSet<string> HiddenLookupTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbo.MatrixDeviceListTypes",
        "dbo.ClassesType",
        "dbo.ProgStepParamHMITypes",
        "dbo.ProgStepParamTypes",
        "dbo.ProgTypes",
        "dbo.ProTypes"
    };
    private LookupDefinition _selectedDefinition;
    private DatabaseService _db;
    private List<ColumnInfo> _columns = new();
    private List<string> _keys = new();
    private DataTable _data;

    public LookupSettingsWindow(ConnectionSettings connection, IEnumerable<string> databases, string selectedDatabase)
    {
        InitializeComponent();
        _connection = connection;
        _databases = (databases ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        CmbDatabase.ItemsSource = _databases;
        if (_databases.Count > 0)
        {
            int index = _databases.FindIndex(x => x.Equals(selectedDatabase, StringComparison.OrdinalIgnoreCase));
            CmbDatabase.SelectedIndex = index >= 0 ? index : 0;
        }
        BuildLookupList();
        SetButtons(false);
    }

    private async void CmbDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbDatabase.SelectedItem is not string dbName) return;
        if (!await _connection.TestConnectionAsync(dbName))
        {
            Configurator.Infrastructure.ModernDialog.Show($"Связь с БД «{dbName}» потеряна. Эта БД недоступна для получения значений.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _db = new DatabaseService(_connection.ToConnectionString(dbName));
        await RefreshAvailableLookupsAsync();
    }

    private async Task RefreshAvailableLookupsAsync()
    {
        if (_db == null) return;

        var knownByTable = _definitions
            .Where(x => !string.IsNullOrWhiteSpace(x.LookupTable))
            .ToDictionary(x => x.LookupTable, StringComparer.OrdinalIgnoreCase);

        var available = new List<LookupDefinition>();
        try
        {
            // Текстовый список = любая реальная таблица dbo, оканчивающаяся на
            // Type/Types. Каталог системных классов только добавляет понятную
            // привязку к классу, но не ограничивает список настроек.
            var tables = await _db.GetTypeLookupTablesAsync();
            foreach (var table in tables)
            {
                if (HiddenLookupTables.Contains(table))
                    continue;

                if (knownByTable.TryGetValue(table, out var known))
                {
                    available.Add(known);
                    continue;
                }

                string shortName = table.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)
                    ? table[4..]
                    : table;
                string className = shortName.EndsWith("Types", StringComparison.OrdinalIgnoreCase)
                    ? shortName[..^5]
                    : shortName.EndsWith("Type", StringComparison.OrdinalIgnoreCase)
                        ? shortName[..^4]
                        : shortName;

                available.Add(new LookupDefinition
                {
                    ClassId = $"lookup:{table}",
                    ClassName = className,
                    TargetTable = "",
                    TargetField = "Type",
                    LookupTable = table,
                    LookupKeyField = "Type"
                });
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Не удалось получить список Type/Types: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
        }

        available = available
            .GroupBy(x => x.LookupTable, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.LookupTable, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LookupList.ItemsSource = available;
        if (available.Count > 0) LookupList.SelectedIndex = 0;
        else
        {
            _selectedDefinition = null;
            TxtLookupTitle.Text = "Нет доступных списков";
            TxtLookupMeta.Text = "В выбранной БД не найдены таблицы dbo.*Type / dbo.*Types.";
            DataGrid.ItemsSource = null;
            SetButtons(false);
        }
    }

    private void BuildLookupList()
    {
        LookupList.DisplayMemberPath = nameof(LookupDefinition.DisplayName);
    }

    private async void LookupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LookupList.SelectedItem is not LookupDefinition definition || _db == null) return;
        _selectedDefinition = definition;
        TxtLookupTitle.Text = $"{definition.ClassName} · {definition.TargetField}";
        TxtLookupMeta.Text = string.IsNullOrWhiteSpace(definition.TargetTable)
            ? $"Текстовый список: {definition.LookupTable}"
            : $"{definition.TargetTable} · {definition.TargetField}  ←  {definition.LookupTable}";
        await LoadSelectedLookupAsync();
    }

    private async Task LoadSelectedLookupAsync()
    {
        try
        {
            _columns = await _db.GetTableColumnsAsync(_selectedDefinition.LookupTable);
            _keys = await _db.GetPrimaryKeyColumnsAsync(_selectedDefinition.LookupTable);
            var order = _keys.Count > 0 ? _keys : _columns.Take(1).Select(x => x.Name).ToList();
            _data = await _db.GetTableDataPageAsync(_selectedDefinition.LookupTable, 1, 1000, order, null);
            DataGrid.ItemsSource = _data.DefaultView;
            SetButtons(true);
            TxtStatus.Text = $"Записей: {_data.Rows.Count}";
        }
        catch (Exception ex)
        {
            DataGrid.ItemsSource = null;
            SetButtons(false);
            TxtStatus.Text = Configurator.Infrastructure.UserErrorMessage.Format(ex);
        }
    }

    private void SetButtons(bool enabled)
    {
        BtnAdd.IsEnabled = enabled;
        BtnEdit.IsEnabled = enabled && DataGrid.SelectedItem != null;
        BtnDelete.IsEnabled = enabled && DataGrid.SelectedItem != null && _keys.Count > 0;
        BtnSaveProjectList.IsEnabled = enabled && _selectedDefinition != null;
    }

    private async void BtnSaveProjectList_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDefinition is null || _db is null || CmbDatabase.SelectedItem is not string database) return;
        try
        {
            string keyColumn = _columns.FirstOrDefault(column => column.Name.Equals(_selectedDefinition.LookupKeyField, StringComparison.OrdinalIgnoreCase))?.Name
                ?? _keys.FirstOrDefault() ?? _columns.FirstOrDefault()?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(keyColumn)) throw new InvalidOperationException("У списка нет ключевого столбца.");
            string displayColumn = _columns.FirstOrDefault(column => new[] { "Name", "Title", "Text", "Description" }
                .Contains(column.Name, StringComparer.OrdinalIgnoreCase))?.Name ?? keyColumn;
            var rows = await _db.GetAllTableRowsAsync(_selectedDefinition.LookupTable);
            var list = new ProjectTextList
            {
                Name = _selectedDefinition.ClassName,
                SourceTable = _selectedDefinition.LookupTable,
                KeyColumn = keyColumn,
                DisplayColumn = displayColumn,
                ImportedUtc = DateTimeOffset.UtcNow,
                Items = rows.Rows.Cast<DataRow>().Select(row => new ProjectTextListItem
                {
                    Key = Convert.ToString(row[keyColumn], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    Text = Convert.ToString(row[displayColumn], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                }).ToList()
            };
            if (list.Items.Any(item => string.IsNullOrWhiteSpace(item.Key)) ||
                list.Items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != list.Items.Count)
                throw new InvalidOperationException("В выбранном списке есть пустые или повторяющиеся ключи; его нельзя безопасно сохранить для Compare.");
            var store = new ProjectCompareSettingsStore(_connection.Server, database);
            var settings = await store.LoadAsync();
            string listKey = $"{list.SourceTable.Trim()}|{list.Name.Trim()}";
            var lists = settings.TextLists.Where(item => !$"{item.SourceTable.Trim()}|{item.Name.Trim()}"
                .Equals(listKey, StringComparison.OrdinalIgnoreCase)).Append(list).ToList();
            await store.SaveAsync(settings with { TextLists = lists });
            TxtStatus.Text = $"Список «{list.Name}» сохранён в проектные настройки для Compare · записей: {list.Items.Count}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Не удалось сохранить список для Compare: " + Configurator.Infrastructure.UserErrorMessage.Format(ex);
        }
    }

    private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => SetButtons(_selectedDefinition != null);

    private async void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDefinition == null || _db == null) return;
        string recordColumn = _columns.FirstOrDefault(c => c.Name.Equals(_selectedDefinition.LookupKeyField, StringComparison.OrdinalIgnoreCase))?.Name
            ?? _keys.FirstOrDefault() ?? _columns.FirstOrDefault()?.Name ?? "";
        var window = new AddRowWindow(_selectedDefinition.LookupTable, _columns, 0, recordColumn, _connection.ToConnectionString(CmbDatabase.SelectedItem?.ToString() ?? "")) { Owner = this };
        window.ShowDialog();
        if (!window.Confirmed || window.Values == null || window.Values.Count == 0) return;
        try
        {
            await _db.InsertRowAsync(_selectedDefinition.LookupTable, window.Values);
            await LoadSelectedLookupAsync();
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Сохранение списка", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDefinition == null || _db == null || DataGrid.SelectedItem is not DataRowView row) return;
        var current = _columns.ToDictionary(c => c.Name, c => row.Row.Table.Columns.Contains(c.Name) && row[c.Name] != DBNull.Value ? Convert.ToString(row[c.Name]) : "", StringComparer.OrdinalIgnoreCase);
        string key = _keys.FirstOrDefault() ?? _columns.FirstOrDefault()?.Name ?? "";
        var window = new EditRowWindow(_selectedDefinition.LookupTable, _columns, current, key) { Owner = this };
        window.ShowDialog();
        if (!window.Confirmed || window.Values == null) return;
        var identity = _keys.ToDictionary(k => k, k => row[k], StringComparer.OrdinalIgnoreCase);
        try
        {
            await _db.UpdateRowByValuesAsync(_selectedDefinition.LookupTable, identity, window.Values);
            await LoadSelectedLookupAsync();
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Сохранение списка", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDefinition == null || _db == null || DataGrid.SelectedItem is not DataRowView row || _keys.Count == 0) return;
        if (Configurator.Infrastructure.ModernDialog.Show("Удалить выбранную запись справочника?", "Текстовый список", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var identity = _keys.ToDictionary(k => k, k => row[k], StringComparer.OrdinalIgnoreCase);
        try
        {
            var connectionString = _connection.ToConnectionString(CmbDatabase.SelectedItem as string);
            var schema = await new DatabaseArchitectureAnalyzer(connectionString).AnalyzeAsync();
            foreach (var relation in schema.Relations.Where(r => r.TargetTable.Equals(_selectedDefinition.LookupTable, StringComparison.OrdinalIgnoreCase)))
                foreach (var column in relation.TargetColumns)
                    if (DataGrid.Columns.Any(c => c.Header?.ToString().Equals(column, StringComparison.OrdinalIgnoreCase) == true) && !identity.ContainsKey(column))
                        identity[column] = row[column];

            var protection = new DeletionProtectionService(connectionString, schema);
            var plan = await protection.BuildPlanAsync(new DeletionRequest { TableName = _selectedDefinition.LookupTable, Key = identity });
            IReadOnlyList<ReferenceReplacement> replacements = Array.Empty<ReferenceReplacement>();
            if (plan.RequiresReplacement)
            {
                var options = new List<DependencyReplacementWindow.DependencyOption>();
                foreach (var hit in plan.Dependencies)
                {
                    var candidates = await protection.GetReplacementCandidatesAsync(hit, identity);
                    if (candidates.Count == 0)
                        throw new InvalidOperationException($"Для {hit.SourceTable}.{hit.SourceColumnDescription} нет допустимого значения для замены.");
                    var current = hit.Relation.TargetColumns.FirstOrDefault(c => identity.ContainsKey(c));
                    options.Add(new DependencyReplacementWindow.DependencyOption
                    {
                        Hit = hit,
                        CurrentValue = current == null ? "" : identity[current],
                        Candidates = candidates
                    });
                }
                var dialog = new DependencyReplacementWindow(options) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                replacements = dialog.Replacements;
            }
            await protection.ExecuteAsync(plan, replacements, (_, _, _) => Task.FromResult(true));
            await LoadSelectedLookupAsync();
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Удаление", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (CmbDatabase.SelectedItem is not string dbName || string.IsNullOrWhiteSpace(dbName)) return;
        try
        {
            if (!await _connection.TestConnectionAsync(dbName))
            {
                Configurator.Infrastructure.ModernDialog.Show($"Связь с БД «{dbName}» потеряна. Выберите активную БД.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _db = new DatabaseService(_connection.ToConnectionString(dbName));
            await RefreshAvailableLookupsAsync();
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Обновление списков", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
