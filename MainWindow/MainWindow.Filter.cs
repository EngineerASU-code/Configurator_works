using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Configurator.Core.Projects;

namespace Configurator;

public partial class MainWindow
{
    private readonly ConditionalWeakTable<ConfiguratorProject, Dictionary<string, TableColumnViewSettings>> _offlineColumnViews = new();

    private TableColumnViewSettings CurrentColumnView
    {
        get
        {
            if (!_isOfflineWorkspace) return _activeVm?.ColumnView;
            if (_openedLocalProject == null || _offlineCurrentTableName == null) return null;
            var tables = _offlineColumnViews.GetValue(_openedLocalProject, _ => new(StringComparer.OrdinalIgnoreCase));
            if (!tables.TryGetValue(_offlineCurrentTableName, out var settings))
                tables.Add(_offlineCurrentTableName, settings = new TableColumnViewSettings());
            return settings;
        }
    }

    private void ConfigureColumnHeader(DataGridColumn column, string name)
    {
        var settings = CurrentColumnView;
        var filter = settings?.FilterFor(name);
        var sorts = settings?.Sorts ?? Array.Empty<TableColumnSort>();
        int rank = Array.FindIndex(sorts, sort => sort.Column.Equals(name, StringComparison.OrdinalIgnoreCase));
        var button = new Button
        {
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = $"{name} · Фильтр и сортировка" + (filter != null ? " · Фильтр включён" : "") +
                (rank >= 0 ? $" · Приоритет сортировки {rank + 1}" : "")
        };
        button.SetResourceReference(StyleProperty, "BtnCompact");
        AutomationProperties.SetName(button, $"{name} · Фильтр и сортировка");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0 0 H 12 L 7.5 5 V 10 L 4.5 8 V 5 Z"),
            Width = 12, Height = 10, Stretch = Stretch.Uniform,
            Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, filter == null ? "BrushTextSecondary" : "BrushAccent");
        content.Children.Add(icon);
        if (rank >= 0)
            content.Children.Add(new TextBlock
            {
                Text = sorts[rank].Direction == ListSortDirection.Ascending ? "↑" : "↓",
                Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
        button.Content = content;
        button.Click += async (_, _) => await EditColumnViewAsync(name);
        column.Header = button;
    }

    private async Task EditColumnViewAsync(string name)
    {
        if (_currentMode != 1 || !UserAuthorizationService.Has(Permission.Mode1View)) return;
        var settings = CurrentColumnView;
        var columns = _isOfflineWorkspace ? _offlineCurrentColumns : _activeVm?.Columns;
        var column = columns?.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (settings == null || column == null) return;
        var oldFilter = settings.FilterFor(name);
        var oldSort = settings.SortFor(name);
        var window = new TableFilterWindow(column, oldFilter, oldSort) { Owner = this };
        if (window.ShowDialog() != true || _currentMode != 1 || settings != CurrentColumnView ||
            !UserAuthorizationService.Has(Permission.Mode1View)) return;
        settings.Set(name, window.Result, window.Sort);
        if (_isOfflineWorkspace)
        {
            RefreshOfflineColumnView();
        }
        else if (_activeVm is { } vm)
        {
            int oldPage = vm.CurrentPage;
            vm.CurrentPage = 1;
            await LoadDataAsync();
            if (vm == _activeVm && settings == CurrentColumnView && _rightPanel.DataTableGrid.ItemsSource == null)
            {
                settings.Set(name, oldFilter, oldSort);
                vm.CurrentPage = oldPage;
                await LoadDataAsync();
            }
        }
    }

    private async void BtnResetColumnView_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMode != 1 || !UserAuthorizationService.Has(Permission.Mode1View) ||
            _rightPanel.DataTableGrid.ItemsSource is not DataView || CurrentColumnView is not { IsActive: true } settings)
            return;

        settings.Clear();
        if (_isOfflineWorkspace)
        {
            RefreshOfflineColumnView();
        }
        else if (_activeVm is { } vm)
        {
            vm.CurrentPage = 1;
            await LoadDataAsync();
        }
    }

    private void RefreshOfflineColumnView()
    {
        BindOfflineColumnView(_offlineCurrentDataTable);
        SetOfflineEditorButtons();
    }

    private void BindOfflineColumnView(DataTable table)
    {
        var settings = CurrentColumnView;
        var view = OfflineColumnView.Create(table, _offlineCurrentColumns, settings);
        _rightPanel.HasActiveColumnView = settings?.IsActive == true;
        _rightPanel.DataTableGrid.ItemsSource = view;
        foreach (var column in _rightPanel.DataTableGrid.Columns)
            ConfigureColumnHeader(column, column.SortMemberPath);
        view.ListChanged += (_, _) =>
        {
            if (ReferenceEquals(_rightPanel.DataTableGrid.ItemsSource, view)) UpdateOfflineTableCount();
        };
        UpdateOfflineTableCount();
    }

    private void UpdateOfflineTableCount()
    {
        if (_rightPanel.DataTableGrid.ItemsSource is not DataView view) return;
        int total = view.Table.Rows.Count;
        _rightPanel.PageInfo.Text = view.Count == total ? $"{total:N0} строк · локально" : $"{view.Count:N0} из {total:N0} строк · локально";
    }
}
