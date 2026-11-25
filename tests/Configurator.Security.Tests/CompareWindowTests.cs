#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public sealed class CompareWindowTests(WpfDialogHost host)
{
    [Fact]
    public Task OnlinePairHasIndependentSelectionsAndRejectsSameDatabase() => host.RunAsync(async () =>
    {
        var window = new CompareWindow(["First", "Second"], "First", _ => throw new InvalidOperationException());
        try
        {
            window.Show();
            window.ScenarioBox.SelectedItem = window.ScenarioBox.Items.Cast<object>().Single(item => item.ToString() == "Онлайн и онлайн");
            Assert.True(window.OnlineDatabaseA.IsVisible && window.OnlineDatabaseB.IsVisible);
            window.FlipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Second", window.OnlineDatabaseA.SelectedItem);
            Assert.Equal("First", window.OnlineDatabaseB.SelectedItem);
            window.OnlineDatabaseA.SelectedItem = "First";
            window.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Выберите две разные онлайн-базы.", window.ProgressText.Text);
            await Dispatcher.Yield();
        }
        finally { window.Close(); }
    });
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task SelectingSourceEnablesCopyAndRefreshesBothSides(bool aToB, bool delete)
    {
        using var files = new CompareTestDirectory();
        var source = PidProject(includePid: true);
        var target = PidProject(includePid: false);
        target.Manifest.ProjectId = source.Manifest.ProjectId;
        var packages = new ProjectPackageService();
        await packages.SaveAsync(files.A, aToB ? source : target);
        await packages.SaveAsync(files.B, aToB ? target : source);
        ChangeJournalEntry? observed = null;
        string? observedPath = null;
        await host.RunAsync(async () =>
        {
            var window = new CompareWindow(isTransferAuthorized: (_, _) => true, canUseOnline: false, canRequestWrite: true)
            {
                ApplyTransferAsync = CompareTransfer.ApplyToTargetAsync,
                ShowInTaskbar = false
            };
            window.JournalChanged += (entry, path) => (observed, observedPath) = (entry, path);
            try
            {
                window.Show();
                window.UpdateLayout();
                var toolbarY = window.CompareButton.TranslatePoint(new Point(), window).Y;
                Assert.InRange(Math.Abs(window.SectionBox.TranslatePoint(new Point(), window).Y - toolbarY), 0, 4);
                Assert.InRange(Math.Abs(window.ScenarioBox.TranslatePoint(new Point(), window).Y - toolbarY), 0, 4);
                window.SelectOfflineProject(true, files.A);
                window.SelectOfflineProject(false, files.B);
                window.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => window.CompareButton.IsEnabled);
                if (aToB && !delete)
                {
                    window.WindowState = WindowState.Normal;
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                var from = aToB ? window.LeftGrid : window.RightGrid;
                var to = aToB ? window.RightGrid : window.LeftGrid;
                var copy = aToB ? window.CopyToB : window.CopyToA;
                var reverse = aToB ? window.CopyToA : window.CopyToB;
                from.SelectedItem = Assert.Single(from.Items.Cast<object>());
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Null(to.SelectedItem);
                Assert.True(copy.IsEnabled, copy.ToolTip?.ToString());
                Assert.False(reverse.IsEnabled);
                foreach (var grid in new[] { from, to })
                {
                    var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(from.SelectedItem));
                    Assert.InRange(grid.Columns[0].ActualWidth / grid.ActualWidth, 0.69, 0.71);
                    var valueCell = Assert.IsType<TextBlock>(grid.Columns[1].GetCellContent(row));
                    Assert.InRange(valueCell.TranslatePoint(new Point(), grid).X - grid.Columns[0].ActualWidth, 8.9, 11.1);
                    var color = Assert.IsType<SolidColorBrush>(row.Background).Color;
                    Assert.True(grid == from ? color.G > color.R : color.R > color.G);
                    Assert.Equal(grid == from ? "Pump 2" : "Отсутствует",
                        valueCell.Text);
                }
                if (delete)
                {
                    to.SelectedItem = from.SelectedItem;
                    Assert.Null(from.SelectedItem);
                    Assert.True(reverse.IsEnabled);
                    Assert.False(copy.IsEnabled);
                }
                (delete ? reverse : copy).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => window.CompareButton.IsEnabled && from.Items.Count == 0);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                foreach (var grid in new[] { from, to })
                {
                    var image = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)grid.ActualWidth, (int)grid.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(grid);
                    int width = image.PixelWidth - 8;
                    int height = image.PixelHeight - 100;
                    var pixels = new int[width * height];
                    image.CopyPixels(new Int32Rect(4, 80, width, height), pixels, width * 4, 0);
                    Assert.True(pixels.All(pixel => pixel == pixels[0]), "Empty Compare viewport contains unexpected pixels.");
                }
            }
            finally { window.Close(); }
        });
        var destination = (aToB != delete) ? files.B : files.A;
        Assert.Equal(destination, observedPath);
        Assert.NotNull(observed);
        var saved = await packages.OpenAsync(destination);
        var journal = new ChangeJournalService();
        journal.BindOfflineProject(saved, () => { }, 3);
        var latest = Assert.Single(journal.LoadRecent());
        Assert.Equal(observed.OperationId, latest.OperationId);
        Assert.Equal(observed.DisplayDescription, latest.DisplayDescription);
        Assert.True(latest.CanRestore);
        Assert.Equal(!delete, saved.LocalRecords.Any(record => record.Values["Name"]?.ToString() == "Pump 2"));
        journal.RestoreOffline(latest, saved, () => { });
        journal.MarkRestored(latest);
        Assert.Null(journal.LoadLatestPending());
        Assert.True(Assert.Single(journal.LoadRecent()).IsRestored);
        Assert.Equal(delete, saved.LocalRecords.Any(record => record.Values["Name"]?.ToString() == "Pump 2"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransfersSelectedRangeWithoutRestartingFullComparison(bool delete)
    {
        using var files = new CompareTestDirectory();
        var source = PidProject(true);
        var target = PidProject(false);
        target.Manifest.ProjectId = source.Manifest.ProjectId;
        var first = source.LocalRecords.Single(r => r.TableName == "dbo.Reglers");
        var second = (JsonObject)first.Values.DeepClone();
        second["Record"] = 4;
        second["Name"] = "Pump 3";
        var schema = source.TableSchemas.Single(s => s.TableName == first.TableName);
        source.LocalRecords.Add(new ProjectRecord { TableName = first.TableName, Values = second,
            Key = ProjectKeyCodec.Encode(schema, schema.KeyColumns, key => second[key]) });
        var packages = new ProjectPackageService();
        await packages.SaveAsync(files.A, source);
        await packages.SaveAsync(files.B, target);
        await host.RunAsync(async () =>
        {
            var window = new CompareWindow(isTransferAuthorized: (_, _) => true, canUseOnline: false, canRequestWrite: true)
            {
                ApplyTransferAsync = CompareTransfer.ApplyToTargetAsync, ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.SelectOfflineProject(true, files.A);
                window.SelectOfflineProject(false, files.B);
                window.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => window.CompareButton.IsEnabled);
                var grid = delete ? window.RightGrid : window.LeftGrid;
                Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
                grid.SelectAll();
                Assert.Equal(2, grid.SelectedItems.Count);
                var arrow = delete ? window.CopyToA : window.CopyToB;
                Assert.True(arrow.IsEnabled);
                var openingSources = 0;
                var text = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
                void TrackFullCompare(object? sender, EventArgs args)
                {
                    if (window.ProgressText.Text == "Открытие источников…") openingSources++;
                }
                text.AddValueChanged(window.ProgressText, TrackFullCompare);
                try
                {
                    arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitFor(() => window.CompareButton.IsEnabled && grid.Items.Count == 0);
                    Assert.Equal(0, openingSources);
                }
                finally { text.RemoveValueChanged(window.ProgressText, TrackFullCompare); }
            }
            finally { window.Close(); }
        });
        var saved = await packages.OpenAsync(delete ? files.A : files.B);
        Assert.Equal(delete ? 0 : 2, saved.LocalRecords.Count(r => r.TableName == first.TableName));
    }

    [Fact]
    public async Task DifferentObjectsAtSameRecordShowOneOrangeRowAndDisableBothDirections()
    {
        using var files = new CompareTestDirectory();
        var a = PidProject(true);
        var b = PidProject(true);
        b.Manifest.ProjectId = a.Manifest.ProjectId;
        var other = b.LocalRecords.Single(r => r.TableName == "dbo.Reglers");
        other.Values["Name"] = "Another pump";
        other.Values["AI_Number"] = 7;
        var packages = new ProjectPackageService();
        await packages.SaveAsync(files.A, a);
        await packages.SaveAsync(files.B, b);
        await host.RunAsync(async () =>
        {
            var window = new CompareWindow(isTransferAuthorized: (_, _) => true, canUseOnline: false, canRequestWrite: true)
            {
                ApplyTransferAsync = CompareTransfer.ApplyToTargetAsync, ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.SelectOfflineProject(true, files.A);
                window.SelectOfflineProject(false, files.B);
                window.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => window.CompareButton.IsEnabled);
                foreach (var grid in new[] { window.LeftGrid, window.RightGrid })
                {
                    grid.SelectedItem = Assert.Single(grid.Items.Cast<object>());
                    window.UpdateLayout();
                    var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem));
                    Assert.Equal(grid == window.LeftGrid ? "Pump 2" : "Another pump",
                        Assert.IsType<TextBlock>(grid.Columns[1].GetCellContent(row)).Text);
                    var color = Assert.IsType<SolidColorBrush>(row.Background).Color;
                    Assert.True(color.R > color.G && color.G > color.B);
                    Assert.False(window.CopyToA.IsEnabled);
                    Assert.False(window.CopyToB.IsEnabled);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task RemainingFieldCanBeCopiedBackAfterWritingTheFirstField()
    {
        using var files = new CompareTestDirectory();
        var a = PidProject(true);
        var b = PidProject(true);
        b.Manifest.ProjectId = a.Manifest.ProjectId;
        var changed = b.LocalRecords.Single(r => r.TableName == "dbo.Reglers");
        changed.Values["Gain"] = 8;
        changed.Values["Limit"] = 9;
        var packages = new ProjectPackageService();
        await packages.SaveAsync(files.A, a);
        await packages.SaveAsync(files.B, b);
        await host.RunAsync(async () =>
        {
            var window = new CompareWindow(isTransferAuthorized: (_, _) => true, canUseOnline: false, canRequestWrite: true)
            {
                ApplyTransferAsync = CompareTransfer.ApplyToTargetAsync, ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.SelectOfflineProject(true, files.A);
                window.SelectOfflineProject(false, files.B);
                window.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => window.CompareButton.IsEnabled);
                async Task CopyField(DataGrid grid, Button arrow, string field, int remaining)
                {
                    window.UpdateLayout();
                    grid.SelectedItem = grid.Items.Cast<object>().Single(item =>
                        grid.Columns[0].GetCellContent(item) is TextBlock label && label.Text.EndsWith(field, StringComparison.Ordinal));
                    Assert.True(arrow.IsEnabled, arrow.ToolTip?.ToString());
                    arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitFor(() => window.CompareButton.IsEnabled && grid.Items.Count == remaining);
                }
                await CopyField(window.LeftGrid, window.CopyToB, "Gain", 2);
                await CopyField(window.RightGrid, window.CopyToA, "Limit", 0);
            }
            finally { window.Close(); }
        });
        foreach (var path in new[] { files.A, files.B })
        {
            var saved = await packages.OpenAsync(path);
            var row = saved.LocalRecords.Single(r => r.TableName == "dbo.Reglers");
            Assert.Equal(1, row.Values["Gain"]!.GetValue<int>());
            Assert.Equal(9, row.Values["Limit"]!.GetValue<int>());
        }
    }

    private static ConfiguratorProject PidProject(bool includePid)
    {
        var project = new ConfiguratorProject();
        var physical = new DatabaseSchema();
        void Table(string name, string[] keys, JsonObject values, bool include = true)
        {
            var schema = new ProjectTableSchema { TableName = "dbo." + name, HasStableKey = true, KeyColumns = keys.ToList() };
            foreach (var (field, _) in values)
                schema.Columns.Add(new ProjectColumnSchema { Name = field, DataType = field == "Name" ? "varchar" : "int",
                    MaxLength = field == "Name" ? 40 : 0 });
            project.TableSchemas.Add(schema);
            physical.Tables.Add(new DatabaseTable { Name = name });
            if (include)
                project.LocalRecords.Add(new ProjectRecord { TableName = schema.TableName, Values = values,
                    Key = ProjectKeyCodec.Encode(schema, keys, key => values[key]) });
        }
        Table("PLC", ["Record"], new() { ["Record"] = 5 });
        Table("PLC_CFG", ["PLC_Number", "PLC_Class_Number"], new() { ["PLC_Number"] = 5, ["PLC_Class_Number"] = 8 });
        Table("AnaIN", ["PLC", "Record"], new() { ["PLC"] = 5, ["Record"] = 12 });
        Table("Reglers", ["PLC", "Record", "PLC_Class_Number"], new()
            { ["PLC"] = 5, ["Record"] = 3, ["PLC_Class_Number"] = 8, ["AI_Number"] = 12, ["Gain"] = 1, ["Limit"] = 2, ["Name"] = "Pump 2" }, includePid);
        var pid = new ClassDefinition { Id = "pid", Name = "PID", IsSystem = true, ClassNumber = 8, Available = true };
        project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(physical);
        project.Architecture["classDefinitions"] = JsonSerializer.SerializeToNode(new[] { pid });
        return project;
    }

    private static async Task WaitFor(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!completed()) await Task.Delay(20, timeout.Token);
    }
}
