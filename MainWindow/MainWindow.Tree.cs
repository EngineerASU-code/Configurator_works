using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Configurator
{
    public partial class MainWindow
    {
        private void ShowKnownDatabasesLoading()
        {
            _leftPanel.ServerTree.Items.Clear();
            _leftPanel.SetLoadingStatus("Проверка сохранённых подключений…", true);
            _leftPanel.HidePlaceholder();

            foreach (var dbName in _connection.KnownDatabases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dbItem = new TreeViewItem
                {
                    Header = CreateTreeHeader($"◌ {dbName}", "Проверка подключения…", true),
                    IsEnabled = false,
                    ToolTip = "Проверяется подключение к базе данных"
                };
                _leftPanel.ServerTree.Items.Add(dbItem);
            }

            if (_leftPanel.ServerTree.Items.Count == 0)
                _leftPanel.ShowPlaceholder();
        }

        private void RebuildTree()
        {
            _leftPanel.ServerTree.Items.Clear();
            _leftPanel.SetLoadingStatus(_failedDatabases.Count > 0
                ? $"Подключено: {_connection.ConnectedDatabases.Count} · недоступно: {_failedDatabases.Count}"
                : $"Подключено: {_connection.ConnectedDatabases.Count}", false);
            _loadedTreeNodes.Clear();

            foreach (var dbName in _connection.ConnectedDatabases)
            {
                bool failed = _failedDatabases.Contains(dbName);
                var header = failed ? "❌ " + dbName : dbName;

                var dbItem = new TreeViewItem
                {
                    Header = CreateTreeHeader("▣  ONLINE · " + header, "SQL SERVER · АКТУАЛЬНЫЕ ДАННЫЕ", true),
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(7, 6, 7, 6),
                    Margin = new Thickness(1, 3, 1, 3),
                    Tag = dbName,
                    ToolTip = failed ? "База недоступна. Выберите её и используйте «База данных → Удалить выбранную базу» либо контекстное меню, чтобы убрать из списка опроса." : "База проекта",
                    // Keep unavailable databases selectable so the user can remove them from the polling list.
                    IsEnabled = true,
                    Opacity = failed ? 0.72 : 1.0
                };
                if (failed)
                {
                    var removeUnavailable = new MenuItem { Header = "Убрать из списка опроса" };
                    removeUnavailable.Click += (s, e) =>
                    {
                        dbItem.IsSelected = true;
                        _selectedDbName = dbName;
                        _activeDbName = dbName;
                        RemoveSelectedDatabase();
                        UpdateAddDbButtonState();
                        ApplyAuthorizationUi();
                    };
                    dbItem.ContextMenu = new ContextMenu();
                    dbItem.ContextMenu.Items.Add(removeUnavailable);
                }

                dbItem.Selected += (s, e) =>
                {
                    e.Handled = true;
                    _selectedDbName = dbName;
                    _activeDbName = dbName;
                    UpdateRemoveDbMenuState();
                };

                if (!failed)
                {
                    dbItem.Items.Add(new TreeViewItem { Header = "Загрузка...", IsEnabled = false });
                    dbItem.Expanded += (s, e) => OnDatabaseExpanded(dbName, dbItem);
                }
                else
                {
                    dbItem.Items.Clear();
                    dbItem.IsExpanded = false;
                }

                _leftPanel.ServerTree.Items.Add(dbItem);
            }

            _leftPanel.EmptyTreePlaceholder.Visibility = _leftPanel.ServerTree.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateRemoveDbMenuState();
        }

        private async void OnDatabaseExpanded(string dbName, TreeViewItem dbItem)
        {
            if (_failedDatabases.Contains(dbName) || !dbItem.IsEnabled) return;
            if (_loadedTreeNodes.Contains(dbName)) return;

            dbItem.Items.Clear();
            dbItem.Items.Add(new TreeViewItem { Header = "Загрузка...", IsEnabled = false });

            string cacheKey = VmCacheKey(dbName);
            if (!_vms.ContainsKey(cacheKey))
            {
                var connStr = _connection.ToConnectionString(dbName);
                _vms[cacheKey] = new TableViewModel(connStr);
            }

            var vm = _vms[cacheKey];

            try
            {
                var tables = await vm.GetTableListAsync();

                dbItem.Items.Clear();

                foreach (DataRow row in tables.Rows)
                {
                    string schema = row[0]?.ToString() ?? "dbo";
                    string tableName = row[1]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(tableName)) continue;

                    string displayName = $"{schema}.{tableName}";

                    var tableItem = new TreeViewItem
                    {
                        Header = CreateTreeHeader(displayName, "ONLINE-ТАБЛИЦА", false),
                        FontWeight = FontWeights.Normal,
                        Padding = new Thickness(5, 4, 5, 4),
                        Margin = new Thickness(1, 1, 1, 1),
                        ToolTip = displayName,
                        Tag = new TableNodeInfo
                        {
                            DatabaseName = dbName,
                            Schema = schema,
                            TableName = tableName
                        }
                    };
                    tableItem.Selected += (s, e) =>
                    {
                        e.Handled = true;
                        OnTableSelected(dbName, schema, tableName);
                    };
                    tableItem.PreviewMouseLeftButtonDown += (s, e) =>
                    {
                        if (s is TreeViewItem item)
                        {
                            item.Focus();
                            item.IsSelected = true;
                            e.Handled = true;
                        }
                    };
                    dbItem.Items.Add(tableItem);
                }

                if (dbItem.Items.Count == 0)
                    dbItem.Items.Add(new TreeViewItem { Header = "(таблиц нет)", IsEnabled = false });

                _loadedTreeNodes.Add(dbName);
            }
            catch (Exception ex)
            {
                MarkDatabaseUnavailable(dbName, ex);
                dbItem.Items.Clear();
                dbItem.Items.Add(new TreeViewItem { Header = "БД недоступна · обновите подключение", IsEnabled = false });
                dbItem.IsExpanded = false;
            }
        }

        private async void OnTableSelected(string dbName, string schema, string tableName)
        {
            string requestedTable = $"{schema}.{tableName}";
            if (_activeVm != null &&
                string.Equals(_activeDbName, dbName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeVm.CurrentTable, requestedTable, StringComparison.OrdinalIgnoreCase))
            {
                // WPF may raise Selected again on repeated clicks; keep the current page
                // and avoid issuing the same SELECT/OFFSET query again.
                UpdateRemoveDbMenuState();
                return;
            }

            _activeDbName = dbName;
            _selectedDbName = dbName;

            string cacheKey = VmCacheKey(dbName);
            if (!_vms.ContainsKey(cacheKey))
            {
                var connStr = _connection.ToConnectionString(dbName);
                _vms[cacheKey] = new TableViewModel(connStr);
            }

            _activeVm = _vms[cacheKey];

            try
            {
                await _activeVm.SelectTableAsync($"{schema}.{tableName}");
            }
            catch (Exception ex)
            {
                if (ex is Microsoft.Data.SqlClient.SqlException sqlEx && IsConnectionFailure(sqlEx))
                {
                    MarkDatabaseUnavailable(dbName, ex);
                }
                else if (ex is Microsoft.Data.SqlClient.SqlException missingObject && missingObject.Number == 208)
                {
                    // A table dropped outside Configurator should disappear immediately,
                    // without forcing the user to collapse/reopen the database node.
                    var databaseNode = _leftPanel.ServerTree.Items.OfType<TreeViewItem>()
                        .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), dbName, StringComparison.OrdinalIgnoreCase));
                    var staleTable = databaseNode?.Items.OfType<TreeViewItem>().FirstOrDefault(x =>
                        x.Tag is TableNodeInfo info && info.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
                        info.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
                    if (staleTable != null)
                    {
                        databaseNode.Items.Remove(staleTable);
                        _loadedTreeNodes.Add(dbName);
                    }
                    else _loadedTreeNodes.Remove(dbName);
                    StatusText.Text = $"Таблица {schema}.{tableName} удалена или переименована вне приложения; список обновлён.";
                }
                else
                {
                    Configurator.Infrastructure.ModernDialog.Show($"Ошибка загрузки метаданных: {ex.Message}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                ClearActiveTable();
                return;
            }

            _ = LoadDataAsync();
            UpdateRemoveDbMenuState();
        }


        private static StackPanel CreateTreeHeader(string title, string subtitle, bool bold)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(1, 0, 4, 0) };
            var titleText = new TextBlock { Text = title, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12 };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");
            panel.Children.Add(titleText);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var subText = new TextBlock { Text = subtitle, FontSize = 9 };
                subText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                panel.Children.Add(subText);
            }
            return panel;
        }

        public void RemoveSelectedDatabase()
        {
            string dbNameToRemove = "";

            if (_leftPanel.ServerTree?.SelectedItem is TreeViewItem selectedItem)
            {
                if (selectedItem.Parent is TreeView)
                {
                    dbNameToRemove = selectedItem.Tag?.ToString() ?? "";
                }
                else if (selectedItem.Parent is TreeViewItem parentItem
                    && parentItem.Parent is TreeView)
                {
                    dbNameToRemove = parentItem.Tag?.ToString() ?? "";
                }
            }

            if (string.IsNullOrEmpty(dbNameToRemove))
                return;

            _connection.ConnectedDatabases.Remove(dbNameToRemove);
            _connection.KnownDatabases.Remove(dbNameToRemove);
            _failedDatabases.Remove(dbNameToRemove);
            _loadedTreeNodes.Remove(dbNameToRemove);
            _vms.Remove(VmCacheKey(dbNameToRemove));

            bool wasActive = (_activeDbName == dbNameToRemove);

            RebuildTree();

            if (wasActive)
            {
                _activeDbName = "";
                _activeVm = null;
                ClearActiveTable();
            }

            _selectedDbName = "";
            UpdateRemoveDbMenuState();

            try
            {
                _connection.SaveToFile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoveSelectedDatabase] Save failed: {ex.Message}");
            }
        }
    }
}
