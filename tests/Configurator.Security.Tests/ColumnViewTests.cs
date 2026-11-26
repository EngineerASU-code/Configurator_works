using System;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Xunit;
using WpfVisualTree = System.Windows.Media.VisualTreeHelper;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public class ColumnViewTests(WpfDialogHost host)
{
    private static TableFilter NameFilter() => new() { Column = "Name", Operator = "Contains", Value = "%" };
    private static TableFilter NumberFilter() => new() { Column = "Score", Operator = "Greater", Value = "1", TypedValue = 1 };

    [Fact]
    public void OfflineFilteringAndTypedOrderingTrackChangesToTheOriginalTable()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Score", typeof(string));
        table.Rows.Add("B%", "2");
        table.Rows.Add("A%", "10");
        var edited = table.Rows.Add("A%", "1");
        table.Rows.Add("A", "100");
        var settings = new TableColumnViewSettings();
        settings.Set("Name", NameFilter(), new("Name", ListSortDirection.Ascending));
        settings.Set("Score", NumberFilter(), new("Score", ListSortDirection.Descending));
        ColumnInfo[] columns = { new() { Name = "Name", DataType = "nvarchar" }, new() { Name = "Score", DataType = "int" } };

        using var view = OfflineColumnView.Create(table, columns, settings);
        Assert.Equal(new[] { "10", "2" }, Scores(view));
        edited["Score"] = "12";
        Assert.Equal(new[] { "12", "10", "2" }, Scores(view));
        settings.Set("Name", null, null);
        using var resetView = OfflineColumnView.Create(table, columns, settings);
        Assert.Equal(new[] { "100", "12", "10", "2" }, Scores(resetView));

        settings.Set("Name", NameFilter(), new("Name", ListSortDirection.Ascending));
        settings.Clear();
        using var defaultView = OfflineColumnView.Create(table, columns, settings);
        Assert.Equal(new[] { "2", "10", "12", "100" }, Scores(defaultView));
    }

    [Fact]
    public async Task WpfDialogRestoresColumnConditionAndSort()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var column = new ColumnInfo { Name = "Score", DataType = "int" };
                var dialog = new TableFilterWindow(column, NumberFilter(), new("Score", ListSortDirection.Descending));
                Assert.Equal("Greater", ((ComboBox)dialog.FindName("CmbOperator")).SelectedValue);
                Assert.Equal("1", ((TextBox)dialog.FindName("TxtValue")).Text);
                Assert.Equal(2, ((ComboBox)dialog.FindName("CmbSort")).SelectedIndex);
                dialog.Close();
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task;
    }

    [CompareLocalDbFact]
    public async Task OnlineColumnConditionsAndSortingApplyBeforePagingAndStayWithTheirTable()
    {
        await using var fixture = await CompareLocalDb.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE dbo.FilterRows (ID int PRIMARY KEY, Name nvarchar(40), Score int);
            CREATE TABLE dbo.OtherRows (ID int PRIMARY KEY, Name nvarchar(40), Score int);
            INSERT dbo.OtherRows VALUES (1, N'other', 1);
            WITH numbers AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM numbers WHERE n < 125)
            INSERT dbo.FilterRows SELECT n, N'b%', n FROM numbers OPTION (MAXRECURSION 0);
            INSERT dbo.FilterRows VALUES (126, N'a%', 2), (127, N'a', 500), (128, NULL, 500);
            """);
        var vm = new TableViewModel(fixture.ConnectionString);
        await vm.SelectTableAsync("dbo.FilterRows");
        vm.ColumnView.Set("Name", NameFilter(), new("Name", ListSortDirection.Ascending));
        vm.ColumnView.Set("Score", NumberFilter(), new("Score", ListSortDirection.Descending));
        Assert.Equal(125, await vm.GetTotalCountAsync());
        var page = await vm.LoadPageAsync();
        Assert.Equal(100, page.Rows.Count);
        Assert.Equal(126, page.Rows[0]["ID"]);
        Assert.Equal(125, page.Rows[1]["ID"]);
        Assert.Equal(2, await vm.FindRowPageAsync(new Dictionary<string, object> { ["ID"] = 26 }));
        Assert.Equal(1, await vm.FindRowPageAsync(new Dictionary<string, object> { ["ID"] = 126 }));
        Assert.Null(await vm.FindRowPageAsync(new Dictionary<string, object> { ["ID"] = 128 }));
        vm.CurrentPage = 2;
        Assert.Equal(26, (await vm.LoadPageAsync()).Rows[0]["ID"]);

        await vm.SelectTableAsync("dbo.OtherRows");
        Assert.False(vm.ColumnView.IsActive);
        Assert.Equal(1, await vm.GetTotalCountAsync());
        vm.ColumnView.Set("Name", NameFilter(), new("Name", ListSortDirection.Descending));
        await vm.SelectTableAsync("dbo.FilterRows");
        vm.ColumnView.Set("Name", null, null);
        Assert.Equal(127, await vm.GetTotalCountAsync());
        Assert.Equal(127, (await vm.LoadPageAsync()).Rows[0]["ID"]);

        vm.ColumnView.Set("Name", NameFilter(), new("Name", ListSortDirection.Ascending));
        vm.ColumnView.Clear();
        Assert.False(vm.ColumnView.IsActive);
        Assert.Equal(128, await vm.GetTotalCountAsync());
        Assert.Equal(1, (await vm.LoadPageAsync()).Rows[0]["ID"]);
        await vm.SelectTableAsync("dbo.OtherRows");
        Assert.True(vm.ColumnView.HasFilters);
        Assert.Single(vm.ColumnView.Sorts);
        Assert.Equal(0, await vm.GetTotalCountAsync());
    }

    [Fact]
    public async Task WpfFilterPopupShowsOnlyOperatorLabels()
    {
        await host.RunAsync(async () =>
        {
            var dialog = new TableFilterWindow(new ColumnInfo { Name = "DescriptionL1", DataType = "nvarchar" }, null, null)
            {
                ShowInTaskbar = false,
                ShowActivated = false
            };
            try
            {
                dialog.Show();
                var combo = (ComboBox)dialog.FindName("CmbOperator");
                combo.IsDropDownOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                Assert.True(popup.IsOpen);
                popup.Child.UpdateLayout();
                string[] labels =
                [
                    "Без фильтра", "Равно", "Не равно", "Содержит", "Не содержит",
                    "Начинается с", "Заканчивается на", "Пусто (NULL)", "Не пусто (NOT NULL)"
                ];
                Assert.Equal(labels, VisualDescendants(popup.Child).OfType<TextBlock>().Select(text => text.Text));

                combo.SelectedIndex = 3;
                combo.IsDropDownOpen = false;
                Assert.Equal("Contains", combo.SelectedValue);
            }
            finally { dialog.Close(); }
        });
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int i = 0; i < WpfVisualTree.GetChildrenCount(parent); i++)
        {
            var child = WpfVisualTree.GetChild(parent, i);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static string[] Scores(DataView view) => view.Cast<DataRowView>().Select(row => (string)row["Score"]).ToArray();
}
