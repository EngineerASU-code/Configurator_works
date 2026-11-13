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
        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            InitializeEmptyFilterLists();
            return;
        }

        try
        {
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));

            // Filter choices come from the reference tables themselves, keyed by Record.
            var plcLookup = await LoadLookupOptionsAsync(db,
                new[] { "dbo.PLCs", "dbo.PLC", "PLCs", "PLC" },
                new[] { "Record" },
                new[] { "NameL2", "Name_L2", "NameL1", "Name_L1", "Name", "Title", "Description", "PLCName" });
            var areaLookup = await LoadLookupOptionsAsync(db,
                new[] { "dbo.Areas", "Areas" },
                new[] { "Record" },
                new[] { "AreaNameL2" });

            var plcOptions = new List<FilterOption> { new("*", "Все PLC") };
            plcOptions.AddRange(plcLookup.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase));
            var areaOptions = new List<FilterOption> { new("*", "Все Area") };
            areaOptions.AddRange(areaLookup.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase));

            _filterSelectionGuard = true;
            CmbPlcFilter.ItemsSource = plcOptions;
            CmbPlcFilter.SelectedIndex = 0;
            CmbAreaFilter.ItemsSource = areaOptions;
            CmbAreaFilter.SelectedIndex = 0;
            _filterSelectionGuard = false;
            ApplyObjectFilters();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Фильтры: {ex.Message}";
            InitializeEmptyFilterLists();
        }
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

    private static async Task<List<FilterOption>> LoadLookupOptionsAsync(DatabaseService db, IEnumerable<string> tableCandidates, IEnumerable<string> keyCandidates, IEnumerable<string> titleCandidates)
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
                var select = new List<string> { key };
                if (!string.IsNullOrWhiteSpace(title) && !select.Contains(title, StringComparer.OrdinalIgnoreCase)) select.Add(title);
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
        FilterIcon?.SetResourceReference(Shape.FillProperty, active ? "BrushAccent" : "BrushTextSecondary");
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
