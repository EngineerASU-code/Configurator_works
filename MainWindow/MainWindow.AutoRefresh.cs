using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Configurator;

public partial class MainWindow
{
    private DispatcherTimer _databaseAutoRefreshTimer;
    private DateTime _lastSchemaAutoRefresh = DateTime.MinValue;
    private bool _autoRefreshInProgress;
    private DateTime _lastConnectionCheck = DateTime.MinValue;

    private static readonly bool _autoRefreshHandlerRegistered = RegisterAutoRefreshHandler();

    private static bool RegisterAutoRefreshHandler()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_AutoRefreshLoaded));
        return true;
    }

    private static void MainWindow_AutoRefreshLoaded(object sender, RoutedEventArgs e)
    {
        var window = (MainWindow)sender;
        window.StartDatabaseAutoRefresh();
    }

    private void StartDatabaseAutoRefresh()
    {
        if (_databaseAutoRefreshTimer != null) return;

        _databaseAutoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _databaseAutoRefreshTimer.Tick += async (s, e) => await AutoRefreshDatabaseViewAsync();
        _databaseAutoRefreshTimer.Start();
        _leftPanel.SetLoadingStatus("Автообновление включено", false);
    }

    private async System.Threading.Tasks.Task AutoRefreshDatabaseViewAsync()
    {
        if (_connection == null || _connection.ConnectedDatabases.Count == 0) return;
        if (_autoRefreshInProgress) return;
        _autoRefreshInProgress = true;
        try
        {
            // Probe connectivity even while an editor has keyboard focus. A dead connection
            // must disable the database node before the next user click can start a query.
            if ((DateTime.UtcNow - _lastConnectionCheck).TotalSeconds >= 5)
            {
                _lastConnectionCheck = DateTime.UtcNow;
                var names = _connection.ConnectedDatabases.ToList();
                var checks = names.Select(async dbName => (dbName, connected: await _connection.TestConnectionAsync(dbName))).ToArray();
                var results = await Task.WhenAll(checks);
                foreach (var result in results)
                {
                    if (result.connected) MarkDatabaseAvailable(result.dbName);
                    else MarkDatabaseUnavailable(result.dbName, null);
                }
            }
            if (EditorModeView.Visibility != Visibility.Visible) return;
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
            // Avoid racing the user's paging/filter queries with background refresh.
            // Filtered result sets can be expensive and are refreshed on explicit navigation.
            if (_activeVm != null && !string.IsNullOrWhiteSpace(_activeDbName) && _activeVm.Filter?.IsActive != true)
                await RefreshActiveTableIfChangedAsync();

            if ((DateTime.UtcNow - _lastSchemaAutoRefresh).TotalSeconds >= 15)
            {
                _lastSchemaAutoRefresh = DateTime.UtcNow;
                var expanded = _leftPanel.ServerTree.Items.OfType<System.Windows.Controls.TreeViewItem>()
                    .Where(x => x.IsExpanded && x.Tag is string)
                    .Select(x => x.Tag.ToString())
                    .ToList();

                foreach (var dbName in expanded)
                {
                    var node = _leftPanel.ServerTree.Items.OfType<System.Windows.Controls.TreeViewItem>()
                        .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), dbName, StringComparison.OrdinalIgnoreCase));
                    if (node == null) continue;
                    await RefreshDatabaseTreeNodeIfChangedAsync(dbName, node);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AutoRefreshDatabaseView] {ex.Message}");
            if (ex is Microsoft.Data.SqlClient.SqlException sqlEx && (sqlEx.Number == 208 || sqlEx.Number == 207))
            {
                var changedSchemaDatabase = _activeDbName;
                ClearActiveTable();
                _loadedTreeNodes.Remove(changedSchemaDatabase);
                var node = _leftPanel.ServerTree.Items.OfType<System.Windows.Controls.TreeViewItem>()
                    .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), changedSchemaDatabase, StringComparison.OrdinalIgnoreCase));
                if (node != null) { node.Items.Clear(); node.Items.Add(new System.Windows.Controls.TreeViewItem { Header = "Схема изменилась · раскройте для обновления", IsEnabled = false }); }
            }
            else if (ex is Microsoft.Data.SqlClient.SqlException sqlFailure && IsConnectionFailure(sqlFailure))
                MarkDatabaseUnavailable(_activeDbName, ex);
        }
        finally { _autoRefreshInProgress = false; }
    }

    protected void StopDatabaseAutoRefresh()
    {
        _databaseAutoRefreshTimer?.Stop();
        _databaseAutoRefreshTimer = null;
    }

    private async Task RefreshActiveTableIfChangedAsync()
    {
        var vm = _activeVm;
        if (vm == null || string.IsNullOrWhiteSpace(vm.CurrentTable)) return;
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
        var ct = cts.Token;
        var oldCount = vm.TotalCount;
        var current = _rightPanel.DataTableGrid?.ItemsSource as DataView;
        int currentPage = vm.CurrentPage;
        int loadGeneration = _loadGeneration;
        var freshCount = await vm.GetFreshTotalCountAsync(ct);
        if (!ReferenceEquals(vm, _activeVm) || vm.CurrentPage != currentPage || _loadGeneration != loadGeneration) return;
        if (current == null || vm.CurrentPageData == null)
        {
            await LoadDataAsync();
            return;
        }

        var freshPage = await vm.LoadPageAsync(ct);
        if (!ReferenceEquals(vm, _activeVm) || vm.CurrentPage != currentPage || _loadGeneration != loadGeneration) return;
        if (freshCount == oldCount && TablesEqual(current.Table, freshPage)) return;

        Dictionary<string, object> identity = null;
        if (_rightPanel.DataTableGrid.SelectedItem is DataRowView selectedRow)
            identity = vm.BuildIdentityValues(selectedRow);
        int index = _rightPanel.DataTableGrid.SelectedIndex;
        BindPageData(freshPage, index, identity, resetScroll: false);
        UpdateSelectionState();
        UpdatePagination();
    }

    private static bool TablesEqual(DataTable a, DataTable b)
    {
        if (a == null || b == null || a.Columns.Count != b.Columns.Count || a.Rows.Count != b.Rows.Count) return false;
        for (int c = 0; c < a.Columns.Count; c++)
            if (!string.Equals(a.Columns[c].ColumnName, b.Columns[c].ColumnName, StringComparison.OrdinalIgnoreCase)) return false;
        for (int r = 0; r < a.Rows.Count; r++)
            for (int c = 0; c < a.Columns.Count; c++)
            {
                var av = a.Rows[r][c]; var bv = b.Rows[r][c];
                if (av == DBNull.Value) av = null; if (bv == DBNull.Value) bv = null;
                if (!string.Equals(Convert.ToString(av), Convert.ToString(bv), StringComparison.Ordinal)) return false;
            }
        return true;
    }

    private async Task RefreshDatabaseTreeNodeIfChangedAsync(string dbName, System.Windows.Controls.TreeViewItem dbItem)
    {
        if (!_vms.TryGetValue(VmCacheKey(dbName), out var vm)) return;
        try
        {
            var tables = await vm.GetTableListAsync();
            var incoming = tables.Rows.Cast<DataRow>()
                .Select(r => $"{Convert.ToString(r[0])}.{Convert.ToString(r[1])}")
                .Where(x => !x.EndsWith(".", StringComparison.Ordinal))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            // A table can be dropped externally while the current result is filtered (in
            // which case row polling is intentionally paused). Check the schema list itself
            // so the stale editor is cleared on this schema poll as well.
            if (_activeVm != null && string.Equals(_activeDbName, dbName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_activeVm.CurrentTable)
                && !incoming.Contains(_activeVm.CurrentTable, StringComparer.OrdinalIgnoreCase))
            {
                var removedTable = _activeVm.CurrentTable;
                ClearActiveTable();
                if (StatusText != null) StatusText.Text = $"Таблица {removedTable} удалена или переименована вне приложения.";
            }

            var existing = dbItem.Items.OfType<System.Windows.Controls.TreeViewItem>()
                .Select(x => (x.Tag as TableNodeInfo) == null ? x.Header?.ToString() ?? "" : $"{((TableNodeInfo)x.Tag).Schema}.{((TableNodeInfo)x.Tag).TableName}")
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("❌") && !x.Equals("Загрузка...", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (incoming.SequenceEqual(existing, StringComparer.OrdinalIgnoreCase)) return;
            _loadedTreeNodes.Remove(dbName);
            OnDatabaseExpanded(dbName, dbItem);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TreeChangeCheck] {ex.Message}");
            MarkDatabaseUnavailable(dbName, ex);
        }
    }

    private static bool IsConnectionFailure(Microsoft.Data.SqlClient.SqlException error)
    {
        if (error == null) return false;
        return error.Errors.Cast<Microsoft.Data.SqlClient.SqlError>().Any(e =>
            e.Number is -2 or 2 or 53 or 64 or 233 or 4060 or 10053 or 10054 or 10060 or 11001 or 18456);
    }

    private void MarkDatabaseUnavailable(string dbName, Exception error)
    {
        if (string.IsNullOrWhiteSpace(dbName)) return;
        bool newlyFailed = _failedDatabases.Add(dbName);
        var node = _leftPanel.ServerTree.Items.OfType<System.Windows.Controls.TreeViewItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), dbName, StringComparison.OrdinalIgnoreCase));
        if (node != null)
        {
            node.IsExpanded = false;
            node.IsEnabled = false;
            node.Opacity = 0.48;
            node.ToolTip = "Связь с БД потеряна. Дождитесь автоматической проверки соединения.";
            node.Items.Clear();
            node.Header = CreateTreeHeader("❌  " + dbName, "Нет связи с БД", true);
        }
        _loadedTreeNodes.Remove(dbName);
        if (string.Equals(_activeDbName, dbName, StringComparison.OrdinalIgnoreCase))
        {
            _dataLoadCts?.Cancel();
            ClearActiveTable();
            _activeVm = null;
            _rightPanel.TableTitle.Text = "База данных недоступна";
            _rightPanel.FieldsEmptyHint.Text = "Связь с БД потеряна. Восстановите подключение; дерево обновится автоматически.";
            _rightPanel.FieldsEmptyHint.Visibility = Visibility.Visible;
        }
        if (newlyFailed && string.Equals(_selectedDbName, dbName, StringComparison.OrdinalIgnoreCase))
        {
            // Keep MainWindow's project selection for recovery, but clear both project
            // editors so stale classes/fields cannot be edited while SQL is unavailable.
            _templateEditor?.Configure(_connection, GetActiveDatabases(), string.Empty);
            _projectEditor?.Configure(_connection, GetActiveDatabases(), string.Empty);
        }
        _leftPanel.SetLoadingStatus($"Подключено: {_connection.ConnectedDatabases.Count - _failedDatabases.Count} · недоступно: {_failedDatabases.Count}", false);
    }

    private void MarkDatabaseAvailable(string dbName)
    {
        if (string.IsNullOrWhiteSpace(dbName) || !_failedDatabases.Remove(dbName)) return;
        var node = _leftPanel.ServerTree.Items.OfType<System.Windows.Controls.TreeViewItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), dbName, StringComparison.OrdinalIgnoreCase));
        if (node != null)
        {
            node.IsEnabled = true;
            node.Opacity = 1.0;
            node.ToolTip = "База проекта";
            node.Header = CreateTreeHeader("▣  " + dbName, "База данных", true);
            node.Items.Clear();
            node.Items.Add(new System.Windows.Controls.TreeViewItem { Header = "Раскройте для загрузки таблиц…", IsEnabled = false });
            node.IsExpanded = false;
        }
        _loadedTreeNodes.Remove(dbName);
        // A TableViewModel may retain a dead connection/pool state from the outage.
        // Drop the cached VM so the next expand creates a fresh model and reloads tables.
        _vms.Remove(VmCacheKey(dbName));
        if (string.Equals(_selectedDbName, dbName, StringComparison.OrdinalIgnoreCase))
        {
            _templateEditor?.Configure(_connection, GetActiveDatabases(), dbName);
            _projectEditor?.Configure(_connection, GetActiveDatabases(), dbName);
        }
        _leftPanel.SetLoadingStatus($"Подключено: {_connection.ConnectedDatabases.Count - _failedDatabases.Count} · недоступно: {_failedDatabases.Count}", false);
    }
}
