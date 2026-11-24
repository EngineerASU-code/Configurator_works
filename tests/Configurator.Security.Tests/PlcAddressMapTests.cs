#nullable enable
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using WpfVisualTree = System.Windows.Media.VisualTreeHelper;
using System.Windows.Threading;
using Configurator.Core.Projects;
using Configurator.Core.Workspace;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class PlcAddressSuggestionTests
{
    [Fact]
    public void RepeatedAnalogAssignmentsShareOneHeaderAndKeepIndividualOwners()
    {
        var map = new PlcAddressMap();
        for (int i = 0; i < 25; i++) map.Add("IW0", $"AI {i}", 'I', true);
        map.Add("ID2", "AI next", 'I', true);
        var groups = map.SpanGroups();
        Assert.Equal("I0–I1 · 2 байта × 25", groups[('I', 0)]);
        Assert.Equal(groups[('I', 0)], groups[('I', 1)]);
        Assert.All(Enumerable.Range(2, 4), offset => Assert.Equal("I2–I5 · 4 байта", groups[('I', offset)]));
        Assert.Equal(25, map.Rows()[0].Bits[0].Count);
    }

    [Fact]
    public void AmbiguousOwnerDescriptionsCannotResolveToAnArbitraryRecord()
    {
        var map = new PlcAddressMap();
        var schema = new ProjectTableSchema { TableName = "dbo.Signals" };
        var first = new AddressOwner("dbo.Signals", "Address", "1", "I1.0", schema, new ProjectRecord(), "Signal", 'I', false);
        var second = first with { Record = new ProjectRecord() };
        map.Add("I1.0", "Signal", 'I', false, first);
        Assert.Same(first, map.FindOwner("Signal · I1.0"));
        map.Add("I1.0", "Signal", 'I', false, second);
        Assert.Null(map.FindOwner("Signal · I1.0"));
    }

    [Fact]
    public void SuggestionsRespectAreaWidthAndOccupiedBits()
    {
        var map = new PlcAddressMap();
        var schema = new ProjectTableSchema { TableName = "dbo.Signals", HasStableKey = true,
            KeyColumns = ["Record"], Columns = [new ProjectColumnSchema { Name = "Address" }] };
        var owner = new AddressOwner("dbo.Signals", "Address", "1", "IW5", schema, new ProjectRecord(), "Signal", 'I', false);
        map.Add("I0.0;I2.7", "occupied", 'I', false);
        var suggestions = map.FreeAddresses(owner, 'I', 4);
        Assert.Equal(new[] { "IW3", "IW4", "IW5", "IW6" }, suggestions);
        Assert.Empty(map.FreeAddresses(owner, 'Q'));
        Assert.Empty(map.FreeAddresses(owner with { Raw = "IW5;IW6" }, 'I'));
        map.Issues.Add("unknown");
        Assert.Empty(map.FreeAddresses(owner, 'I'));
    }

    [Fact]
    public void AnalogNumericSuggestionsPreserveNumericRepresentation()
    {
        var map = new PlcAddressMap();
        var schema = new ProjectTableSchema { TableName = "dbo.AnalogIn", HasStableKey = true,
            KeyColumns = ["Record"], Columns = [new ProjectColumnSchema { Name = "Address", DataType = "int" }] };
        var owner = new AddressOwner("dbo.AnalogIn", "Address", "1", "40", schema, new ProjectRecord(), "AI", 'I', true);
        map.Add("I0.1", "occupied", 'I', false);
        Assert.Equal(new[] { "1", "2" }, map.FreeAddresses(owner, 'I', 2));
    }

    [Fact]
    public async Task AdditionalSignalFieldsReserveTheirArea()
    {
        var project = new ConfiguratorProject();
        project.TableSchemas.Add(new ProjectTableSchema { TableName = "dbo.Valves2S", Columns = [
            new() { Name = "PLC" }, new() { Name = "Address_LiftUp" }, new() { Name = "Address_LiftDown" },
            new() { Name = "Address_FeedbackOpen" }, new() { Name = "Address_MS" }] });
        project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.Valves2S", Values = new JsonObject {
            ["PLC"] = 1, ["Address_LiftUp"] = "3.0", ["Address_LiftDown"] = "3.1",
            ["Address_FeedbackOpen"] = "4.0", ["Address_MS"] = "Q3.2" } });
        using var workspace = new ProjectWorkspace(new OfflineProjectDataSource(project),
            architecture: new ProjectArchitectureDataSource(project));
        var map = (await PlcAddressCatalog.LoadAsync(workspace, default))["1"];
        var rows = map.Rows();
        Assert.All(rows.Single(r => r.Label == "Q3").Bits.Take(3), bit => Assert.Single(bit));
        Assert.Single(rows.Single(r => r.Label == "I4").Bits[0]);
        Assert.Empty(map.Issues);
    }

}

[Collection("WPF dialogs")]
public sealed class PlcAddressMapTests(WpfDialogHost host)
{
    [Fact]
    public void MapsWidthsPartialOverlapsSeparateAreasAndSkippedRanges()
    {
        var map = new PlcAddressMap();
        map.Add("I40; %ID50", "AI", 'I', true);
        map.Add("I41.7 Q41.7", "Valve", null, false);
        map.Add("IB52,%QX41.6", "Motor Speed", null, false);
        map.Add("I41.8;IW65535;DB2;IW50.1", "invalid", null, false);
        var rows = map.Rows();
        Assert.Equal(4, map.Issues.Count);
        Assert.Equal(2, rows.Single(r => r.Label == "I41").Bits[7].Count);
        Assert.All(rows.Single(r => r.Label == "I40").Bits, b => Assert.Single(b));
        Assert.All(rows.Single(r => r.Label == "I52").Bits, b => Assert.Equal(2, b.Count));
        Assert.All(rows.Single(r => r.Label == "I53").Bits, b => Assert.Single(b));
        Assert.All(rows.Single(r => r.Label == "I42–I49").Bits, b => Assert.Empty(b));
        Assert.False(rows.Single(r => r.Label == "Q41").HasConflict);
    }

    [Fact]
    public async Task OfflineScopesClassesFieldsAndControllers()
    {
        using var workspace = Fixture();
        var maps = await PlcAddressCatalog.LoadAsync(workspace, default);
        Assert.Equal(new[] { "1", "2", "3" }, maps.Keys);
        var rows = maps["1"].Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[1].Bits[7].Count);
        Assert.Contains(rows[1].Bits[7], s => s.Contains("Motor Speed"));
        Assert.Single(maps["2"].Rows());
        Assert.Empty(maps["3"].Rows());
    }

    [CompareLocalDbFact]
    public async Task SqlReadsLaterPagesAndReportsCrossClassOverlap()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.AnaIn (Record int PRIMARY KEY, PLC int, Address varchar(30));
            INSERT dbo.AnaIn VALUES (1,1,'I40');
            CREATE TABLE dbo.Valves (Record int PRIMARY KEY, PLC int, Address_Input varchar(30));
            WITH N AS (SELECT 1 i UNION ALL SELECT i+1 FROM N WHERE i<1001)
            INSERT dbo.Valves SELECT i, 1, CASE WHEN i=1001 THEN 'I41.1' END FROM N OPTION (MAXRECURSION 0);
            """);
        using var workspace = new ProjectWorkspace(new OnlineProjectDataSource(db.ConnectionString),
            architecture: new ProjectArchitectureDataSource(new ConfiguratorProject()));
        var maps = await PlcAddressCatalog.LoadAsync(workspace, default);
        Assert.Equal(2, maps["1"].Rows().Last().Bits[1].Count);
    }

    [Fact]
    public async Task WpfShowsFilteredConflictsAndUpdatesOnPlcChange()
    {
        await host.RunAsync(async () =>
        {
            var window = new PlcAddressWindow([new("Offline", ct => Task.FromResult(Fixture()))], (_, _) => throw new InvalidOperationException()) { ShowInTaskbar = false };
            try
            {
                window.Show();
                var plc = (ComboBox)window.FindName("Plc");
                var grid = (ItemsControl)window.FindName("GridRows");
                var filter = (ComboBox)window.FindName("Filter");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var projects = (ComboBox)window.FindName("Project");
                Assert.False(plc.IsEnabled);
                projects.SelectedIndex = 0;
                while (!plc.IsEnabled && !timeout.IsCancellationRequested) await Task.Delay(20);
                Assert.Null(plc.SelectedItem);
                plc.SelectedIndex = 0;
                Assert.True(plc.SelectedItem != null, ((TextBlock)window.FindName("Status")).Text);
                Assert.Equal(2, grid.Items.Count);
                filter.SelectedIndex = 2;
                Assert.Single(grid.Items.Cast<object>());
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.IsNotType<ListBox>(grid);
                window.UpdateLayout();
                var button = Descendants(grid).OfType<Button>().Single(b => Equals(b.Content, "7 · Конфликт"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var adjacent = Descendants(grid).OfType<Button>().Single(b => Equals(b.Content, "4 · Занят"));
                bool Selected(Button b) => (bool)b.DataContext.GetType().GetProperty("IsSelected")!.GetValue(b.DataContext)!;
                Assert.True(Selected(button));
                adjacent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(Selected(adjacent));
                Assert.False(Selected(button));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(Selected(adjacent));
                var title = (TextBlock)window.FindName("DetailTitle");
                var owners = (ListBox)window.FindName("OwnerList");
                Assert.Contains("I41.7", title.Text);
                Assert.Contains(owners.Items.Cast<string>(), name => name.Contains("Motor Speed"));
                Assert.Contains(owners.Items.Cast<string>(), name => name.Contains("AI"));
                plc.SelectedItem = "2";
                Assert.Empty(owners.Items);
                Assert.Empty(grid.Items);
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < WpfVisualTree.GetChildrenCount(parent); index++)
        {
            var child = WpfVisualTree.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static ProjectWorkspace Fixture()
    {
        var project = new ConfiguratorProject();
        void Add(string table, JsonObject values)
        {
            if (!project.TableSchemas.Any(t => t.TableName == table))
                project.TableSchemas.Add(new ProjectTableSchema { TableName = table,
                    Columns = values.Select(p => new ProjectColumnSchema { Name = p.Key }).ToList() });
            project.LocalRecords.Add(new ProjectRecord { TableName = table, Values = values });
        }
        Add("dbo.Classes", new() { ["Name"] = "Motor Speed", ["DBName"] = "Drives" });
        Add("dbo.Drives", new() { ["PLC"] = 1, ["Address_Input"] = "I41.7" });
        Add("dbo.AnalogIn", new() { ["PLC"] = 1, ["Address"] = "40" });
        Add("dbo.Drives", new() { ["PLC"] = 2, ["Address_Input"] = "I41.7" });
        Add("dbo.Classes", new() { ["Name"] = "Area", ["DBName"] = "Shared", ["Record"] = 21 });
        Add("dbo.Classes", new() { ["Name"] = "Pump", ["DBName"] = "Shared", ["Record"] = 22 });
        Add("dbo.Shared", new() { ["Class"] = 21, ["PLC"] = 1, ["Address"] = "ID0" });
        Add("dbo.Shared", new() { ["Class"] = 22, ["PLC"] = 2, ["Address"] = "I41.0" });
        Add("dbo.PLC", new() { ["Record"] = 3, ["Address"] = "I4.0" });
        foreach (var table in new[] { "Prog", "Prog_Step", "Matrix", "Area", "Lookup" })
            Add("dbo." + table, new() { ["PLC"] = 1, ["Address"] = "IB2" });
        return new ProjectWorkspace(new OfflineProjectDataSource(project), architecture: new ProjectArchitectureDataSource(project));
    }
}
