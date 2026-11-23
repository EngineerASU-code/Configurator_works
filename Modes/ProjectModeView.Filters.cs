using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Configurator;

public partial class ProjectModeView
{
    private async Task PopulateGlobalFilterListsAsync()
    {
        var selectedPlcKey = GetSelectedFilterKey(CmbPlcFilter);
        var selectedAreaKey = GetSelectedFilterKey(CmbAreaFilter);

        try
        {
            List<FilterOption> plcLookup;
            List<FilterOption> areaLookup;
            if (_offlineProject != null)
            {
                plcLookup = await LoadOfflineLookupOptionsAsync(
                    new[] { "dbo.PLCs", "dbo.PLC", "PLCs", "PLC" },
                    new[] { "Record" },
                    new[] { "NameL2", "Name_L2", "NameL1", "Name_L1", "Name", "Title", "Description", "PLCName" });
                areaLookup = await LoadOfflineLookupOptionsAsync(
                    new[] { "dbo.Areas", "dbo.Area", "Areas", "Area" },
                    new[] { "AreaName" },
                    new[] { "AreaNameL2", "AreaNameL1", "NameL2", "Name_L2", "NameL1", "Name_L1", "AreaName", "Name", "DescriptionL2", "DescriptionL1", "Description", "Title" },
                    includeRecordInTitle: true);
            }
            else
            {
                if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))
                {
                    InitializeEmptyFilterLists();
                    return;
                }

                var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
                plcLookup = await LoadLookupOptionsAsync(db,
                    new[] { "dbo.PLCs", "dbo.PLC", "PLCs", "PLC" },
                    new[] { "Record" },
                    new[] { "NameL2", "Name_L2", "NameL1", "Name_L1", "Name", "Title", "Description", "PLCName" });
                areaLookup = await LoadLookupOptionsAsync(db,
                    new[] { "dbo.Areas", "dbo.Area", "Areas", "Area" },
                    new[] { "AreaName" },
                    new[] { "AreaNameL2", "AreaNameL1", "NameL2", "Name_L2", "NameL1", "Name_L1", "AreaName", "Name", "DescriptionL2", "DescriptionL1", "Description", "Title" },
                    includeRecordInTitle: true);
            }

            SetGlobalFilterLists(plcLookup, areaLookup, selectedPlcKey, selectedAreaKey);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Фильтры: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
            InitializeEmptyFilterLists();
        }
    }

    private async Task<List<FilterOption>> LoadOfflineLookupOptionsAsync(
        IEnumerable<string> tableCandidates,
        IEnumerable<string> keyCandidates,
        IEnumerable<string> titleCandidates,
        bool includeRecordInTitle = false)
    {
        var tables = _offlineProject?.TableSchemas ?? new List<Core.Projects.ProjectTableSchema>();
        var tableName = tables.FirstOrDefault(t => tableCandidates.Any(c => t.TableName.Equals(c, StringComparison.OrdinalIgnoreCase)))?.TableName
            ?? _offlineProject?.LocalRecords.Select(r => r.TableName).FirstOrDefault(t => tableCandidates.Any(c => t.Equals(c, StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(tableName) || _offlineReader == null) return new List<FilterOption>();

        var data = await _offlineReader.ReadRowsAsync(tableName, 10000);
        var names = data.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        string key = keyCandidates.Select(c => names.FirstOrDefault(n => n.Equals(c, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (string.IsNullOrWhiteSpace(key)) return new List<FilterOption>();
        string title = titleCandidates.Select(c => names.FirstOrDefault(n => n.Equals(c, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        string record = includeRecordInTitle ? names.FirstOrDefault(n => n.Equals("Record", StringComparison.OrdinalIgnoreCase)) : null;

        var result = new List<FilterOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in data.Rows)
        {
            if (row[key] == DBNull.Value) continue;
            string value = Convert.ToString(row[key]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) continue;
            string label = !string.IsNullOrWhiteSpace(title) && row[title] != DBNull.Value ? Convert.ToString(row[title]) ?? string.Empty : string.Empty;
            string display = string.IsNullOrWhiteSpace(label) ? value : label;
            if (!string.IsNullOrWhiteSpace(record) && row[record] != DBNull.Value)
            {
                string recordValue = Convert.ToString(row[record]) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(recordValue)) display = string.IsNullOrWhiteSpace(label) ? recordValue : $"{recordValue}. {label}";
            }
            result.Add(new FilterOption(value, display));
        }
        return result.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void SetGlobalFilterLists(IReadOnlyList<FilterOption> plcLookup, IReadOnlyList<FilterOption> areaLookup, string selectedPlcKey, string selectedAreaKey)
    {
        var plcOptions = new List<FilterOption> { new("*", "Все PLC") };
        plcOptions.AddRange(plcLookup.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase));
        var areaOptions = new List<FilterOption> { new("*", "Все Area") };
        areaOptions.AddRange(areaLookup.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase));

        _filterSelectionGuard = true;
        CmbPlcFilter.ItemsSource = plcOptions;
        CmbPlcFilter.SelectedItem = plcOptions.FirstOrDefault(x => x.Key.Equals(selectedPlcKey, StringComparison.OrdinalIgnoreCase)) ?? plcOptions[0];
        CmbAreaFilter.ItemsSource = areaOptions;
        CmbAreaFilter.SelectedItem = areaOptions.FirstOrDefault(x => x.Key.Equals(selectedAreaKey, StringComparison.OrdinalIgnoreCase)) ?? areaOptions[0];
        _filterSelectionGuard = false;
        ApplyObjectFilters();
    }

    private async Task RefreshConfigurationLookupsAsync(string preferredConfigurationKey = null)
    {
        _onlineLookupOptionsService = null;
        _onlineLookupDatabaseKey = string.Empty;

        await LoadConfigurationValuesAsync(_configurationKind, preferredConfigurationKey);
        await PopulateGlobalFilterListsAsync();
        RebuildObjectFilters();
    }

    private void InitializeEmptyFilterLists()
    {
        _filterSelectionGuard = true;
        CmbPlcFilter.ItemsSource = new[] { new FilterOption("*", "Все PLC") };
        CmbPlcFilter.SelectedIndex = 0;
        CmbAreaFilter.ItemsSource = new[] { new FilterOption("*", "Все Area") };
        CmbAreaFilter.SelectedIndex = 0;
        _filterSelectionGuard = false;
        ApplyObjectFilters();
    }

    private static async Task<List<FilterOption>> LoadLookupOptionsAsync(DatabaseService db, IEnumerable<string> tableCandidates, IEnumerable<string> keyCandidates, IEnumerable<string> titleCandidates, bool includeRecordInTitle = false)
    {
        foreach (var table in tableCandidates)
        {
            try
            {
                var columns = await db.GetTableColumnsAsync(table);
                if (columns.Count == 0) continue;
                var names = columns.Select(x => x.Name).ToList();
                var key = keyCandidates.Select(candidate => names.FirstOrDefault(n => n.Equals(candidate, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                if (string.IsNullOrWhiteSpace(key)) continue;
                var title = titleCandidates.Select(candidate => names.FirstOrDefault(n => n.Equals(candidate, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                var record = includeRecordInTitle ? names.FirstOrDefault(n => n.Equals("Record", StringComparison.OrdinalIgnoreCase)) : null;
                var select = new List<string> { key };
                if (!string.IsNullOrWhiteSpace(title) && !select.Contains(title, StringComparer.OrdinalIgnoreCase)) select.Add(title);
                if (!string.IsNullOrWhiteSpace(record) && !select.Contains(record, StringComparer.OrdinalIgnoreCase)) select.Add(record);
                var data = await db.GetTableDataPageAsync(table, 1, 10000, select, null);
                var result = new List<FilterOption>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DataRow row in data.Rows)
                {
                    if (!data.Columns.Contains(key) || row[key] == DBNull.Value) continue;
                    var id = Convert.ToString(row[key]) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                    var display = id;
                    if (!string.IsNullOrWhiteSpace(title) && data.Columns.Contains(title) && row[title] != DBNull.Value)
                    {
                        var text = Convert.ToString(row[title]);
                        if (!string.IsNullOrWhiteSpace(text)) display = text;
                    }
                    if (!string.IsNullOrWhiteSpace(record) && data.Columns.Contains(record) && row[record] != DBNull.Value)
                    {
                        var recordValue = Convert.ToString(row[record]);
                        if (!string.IsNullOrWhiteSpace(recordValue)) display = string.Equals(display, id, StringComparison.OrdinalIgnoreCase) ? recordValue : $"{recordValue}. {display}";
                    }
                    result.Add(new FilterOption(id, display));
                }
                if (result.Count > 0) return result.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { }
        }
        return new List<FilterOption>();
    }

    private void RebuildObjectFilters() => ApplyObjectFilters();

    private static string GetSelectedFilterKey(ComboBox combo)
        => combo?.SelectedItem is FilterOption option ? option.Key : "*";

    private void ApplyObjectFilters()
    {
        if (_configurationKind != ConfigurationKind.Class) return;

        var plcKey = GetSelectedFilterKey(CmbPlcFilter);
        var areaKey = GetSelectedFilterKey(CmbAreaFilter);
        var result = BuildObjectNames(_objects, _selectedClass).Where(item =>
        {
            if (item.RowIndex < 0 || item.RowIndex >= _objects.Rows.Count) return false;
            var row = _objects.Rows[item.RowIndex];
            string plc = _objects.Columns.Contains("PLC") ? Convert.ToString(row["PLC"]) ?? "" : "";
            string area = _objects.Columns.Contains("Area") ? Convert.ToString(row["Area"]) ?? "" : "";
            return (plcKey == "*" || string.Equals(plcKey, plc, StringComparison.OrdinalIgnoreCase))
                && (areaKey == "*" || string.Equals(areaKey, area, StringComparison.OrdinalIgnoreCase));
        }).ToList();
        ObjectList.ItemsSource = result;
        if (_configurationKind == ConfigurationKind.Class && TxtObjectCount != null)
            TxtObjectCount.Text = plcKey == "*" && areaKey == "*"
                ? $"{result.Count} объектов"
                : $"{result.Count} из {_objects.Rows.Count} объектов";
        UpdateObjectFilterIndicator(plcKey, areaKey);
    }

    private void UpdateObjectFilterIndicator(string plcKey = null, string areaKey = null)
    {
        plcKey ??= GetSelectedFilterKey(CmbPlcFilter);
        areaKey ??= GetSelectedFilterKey(CmbAreaFilter);
        bool plcActive = _configurationKind == ConfigurationKind.Class && plcKey != "*";
        bool areaActive = _configurationKind == ConfigurationKind.Class && areaKey != "*";
        bool active = plcActive || areaActive;
        if (FilterActiveBadge != null) FilterActiveBadge.Visibility = active ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        FilterIcon?.SetResourceReference(Shape.StrokeProperty, active ? "BrushAccent" : "BrushText");
        if (BtnObjectFilter != null)
        {
            BtnObjectFilter.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, active ? "BrushSelected" : "BrushPanel");
            BtnObjectFilter.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, active ? "BrushAccent" : "BrushBorder");
        }
        if (BtnObjectFilter != null)
        {
            var activeFilters = new List<string>();
            if (plcActive && CmbPlcFilter.SelectedItem is FilterOption plc) activeFilters.Add($"PLC: {plc.Title}");
            if (areaActive && CmbAreaFilter.SelectedItem is FilterOption area) activeFilters.Add($"Area: {area.Title}");
            BtnObjectFilter.ToolTip = active ? $"Фильтр активен · {string.Join(" · ", activeFilters)}" : "Фильтр по PLC и Area";
        }
    }

    private void ObjectFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_filterSelectionGuard) return;
        if (_objects != null) ApplyObjectFilters();
    }

    private sealed record FilterOption(string Key, string Title)
    {
        public override string ToString() => Title;
    }
}
