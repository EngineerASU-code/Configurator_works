#nullable enable
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Core.Workspace;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public sealed class RecordCheckTests(WpfDialogHost host)
{
    [Fact]
    public void FindsTheMissingRecordsBetweenZeroAndNinetyNine()
    {
        var records = new SortedSet<int>(Enumerable.Range(0, 100).Except(new[] { 66, 48, 34 }));

        var result = RecordCheckResult.FromOccupied(records);

        Assert.Equal(0, result.MinRecord);
        Assert.Equal(99, result.MaxRecord);
        Assert.Equal(new[] { new RecordGap(34, 34), new RecordGap(48, 48), new RecordGap(66, 66) }, result.Gaps);
        Assert.Equal(3, result.FreeCount);
    }

    [Fact]
    public void HandlesEmptyAndContinuousRangesAndKeepsVeryLargeGapsCompact()
    {
        var empty = RecordCheckResult.FromOccupied([]);
        Assert.Null(empty.MinRecord);
        Assert.Null(empty.MaxRecord);
        Assert.Empty(empty.Gaps);

        var continuous = RecordCheckResult.FromOccupied([int.MaxValue - 1, int.MaxValue]);
        Assert.Empty(continuous.Gaps);

        var sparse = RecordCheckResult.FromOccupied([int.MinValue, int.MaxValue]);
        Assert.Equal(new RecordGap(int.MinValue + 1, int.MaxValue - 1), Assert.Single(sparse.Gaps));
        Assert.Equal(4_294_967_294, sparse.FreeCount);
    }

    [Fact]
    public async Task ResolvesRegistryCustomAndLegacyClassTablesWithTheirActualColumns()
    {
        var project = new ConfiguratorProject();
        project.TableSchemas.AddRange([
            Schema("dbo.Classes", "Name", "DBName"),
            Schema("dbo.AnalogIn", "PLC", "Record"),
            Schema("controls.Actuators", "Plc", "RECord", "PLCClassNumber"),
            Schema("dbo.Pumps", "PLC", "Record"),
            Schema("dbo.Lookup", "Record")
        ]);
        project.LocalRecords.Add(new ProjectRecord
        {
            TableName = "dbo.Classes", Values = new JsonObject { ["Name"] = "Actuator", ["DBName"] = "Actuators" }
        });
        using var workspace = new ProjectWorkspace(new OfflineProjectDataSource(project),
            architecture: new ProjectArchitectureDataSource(project));
        project.Architecture["classDefinitions"] = System.Text.Json.JsonSerializer.SerializeToNode<ClassDefinition[]>([
            new ClassDefinition { Name = "Pump", StorageMappings = [new StorageMapping { Role = "primary", TableName = "Pumps" }] },
            new ClassDefinition { Name = "Lookup" }
        ]);

        var classes = await RecordCheckCatalog.LoadAsync(workspace, default);

        Assert.Equal(3, classes.Count);
        Assert.Contains(new RecordCheckClass("AI", "dbo.AnalogIn", "PLC", "Record"), classes);
        Assert.Contains(new RecordCheckClass("Pump", "dbo.Pumps", "PLC", "Record"), classes);
        Assert.Contains(new RecordCheckClass("Actuator", "controls.Actuators", "Plc", "RECord"), classes);
    }

    [Fact]
    public void LocalRecordsAreScopedByPlcAndClassAndUseLegacyTableNames()
    {
        var project = new ConfiguratorProject();
        project.TableSchemas.Add(Schema("dbo.Devices", "PLC", "Record", "Class"));
        void Add(int plc, int group, JsonNode? record) => project.LocalRecords.Add(new ProjectRecord
        {
            TableName = "Devices", Values = new JsonObject { ["plc"] = plc, ["recORD"] = record, ["Class"] = group }
        });
        Add(1, 10, 0);
        Add(1, 10, 4);
        Add(1, 10, 4);
        Add(1, 20, 1);
        Add(2, 10, 2);
        Add(3, 10, null);
        var source = new OfflineProjectDataSource(project);
        var group = new RecordCheckClass("Pump", "dbo.Devices", "PLC", "Record", "Class", "10");
        var scopes = RecordCheckCatalog.LocalScopes(source, [group], default);
        Assert.Equal(2, scopes.Count);
        var first = Assert.Single(scopes, s => Equals(s.Plc, "1"));
        Assert.Equal(new RecordGap(1, 3), Assert.Single(RecordCheckCatalog.CheckLocal(source, first, default).Gaps));
        Add(1, 10, "invalid");
        source.InvalidateIndex();
        Assert.Throws<InvalidOperationException>(() => RecordCheckCatalog.CheckLocal(source, first, default));
    }

    [CompareLocalDbFact]
    public async Task SqlUsesNamedClassesAndIgnoresPlcClassNumbers()
    {
        await using var database = await CompareLocalDb.CreateAsync();
        await database.ExecuteAsync("""
            CREATE TABLE dbo.Classes (Record int PRIMARY KEY, Name nvarchar(100), DBName nvarchar(100));
            INSERT INTO dbo.Classes VALUES (10, N'Pump', N'Devices'), (20, N'Fan', N'Devices');
            CREATE TABLE dbo.Devices (PLC smallint NOT NULL, Class int NOT NULL, PLC_Class_Number smallint NULL, Record int NOT NULL);
            WITH Numbers AS (SELECT 0 AS N UNION ALL SELECT N + 1 FROM Numbers WHERE N < 99)
            INSERT INTO dbo.Devices SELECT 1, 10, CASE WHEN N % 2 = 0 THEN 100 END, N
            FROM Numbers WHERE N NOT IN (34, 48, 66);
            INSERT INTO dbo.Devices VALUES (1, 20, 200, 34), (1, 20, 200, 48), (1, 20, 200, 66), (1, 20, 200, 101),
                                         (2, 10, 100, -1), (2, 10, 100, 34), (2, 10, 100, 48), (2, 10, 100, 66);
            """);
        using var workspace = new ProjectWorkspace(new OnlineProjectDataSource(database.ConnectionString),
            architecture: new ProjectArchitectureDataSource(new ConfiguratorProject()));
        var classes = await RecordCheckCatalog.LoadAsync(workspace, default);
        var service = new DatabaseService(database.ConnectionString);
        var scopes = await service.GetRecordCheckScopesAsync(classes, default);

        Assert.Equal(new[] { "Fan", "Pump" }, scopes.Where(scope => Equals(scope.Plc, (short)1)).Select(scope => scope.Class.Title));
        Assert.Equal("Pump", Assert.Single(scopes, scope => Equals(scope.Plc, (short)2)).Class.Title);
        var selection = Assert.Single(scopes, scope => Equals(scope.Plc, (short)1) && scope.Class.Title == "Pump");

        var result = await service.CheckRecordsAsync(selection, default);
        Assert.Equal(0, result.MinRecord);
        Assert.Equal(99, result.MaxRecord);
        Assert.Equal(new[] { new RecordGap(34, 34), new RecordGap(48, 48), new RecordGap(66, 66) }, result.Gaps);
    }

    [CompareLocalDbFact]
    public async Task WpfDialogSelectsDatabaseThenPlcThenClassAndClearsStaleResults()
    {
        await using var database = await CompareLocalDb.CreateAsync();
        await using var otherDatabase = await CompareLocalDb.CreateAsync();
        await database.ExecuteAsync("""
            CREATE TABLE dbo.Prog (PLC smallint NOT NULL, PLC_Class_Number smallint NOT NULL, Record int NOT NULL);
            CREATE TABLE dbo.Valves (PLC smallint NOT NULL, Record int NOT NULL);
            WITH Numbers AS (SELECT 0 AS N UNION ALL SELECT N + 1 FROM Numbers WHERE N < 99)
            INSERT INTO dbo.Prog SELECT 1, N % 2, N FROM Numbers WHERE N NOT IN (34, 48, 66);
            INSERT INTO dbo.Valves VALUES (2, 0), (2, 1);
            """);
        await otherDatabase.ExecuteAsync("""
            CREATE TABLE dbo.Motors (PLC smallint NOT NULL, Record int NOT NULL);
            INSERT INTO dbo.Motors VALUES (3, 0), (3, 2);
            """);

        await host.RunAsync(async () =>
        {
            var window = new FreeRecordCheckWindow(name => name == "Test DB" ? database.ConnectionString : otherDatabase.ConnectionString,
                ["Test DB", "Other DB"], "Test DB")
            {
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                var databases = (ComboBox)window.FindName("CmbDatabase");
                var plc = (ComboBox)window.FindName("CmbPlc");
                var classList = (ComboBox)window.FindName("CmbClass");
                var check = (Button)window.FindName("BtnCheck");
                var result = (TextBlock)window.FindName("TxtResult");
                var status = (TextBlock)window.FindName("TxtStatus");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

                await WaitFor(() => plc.IsEnabled, status, timeout.Token);
                Assert.Null(window.FindName("BtnExport"));
                Assert.Null(window.FindName("CmbClassNumber"));
                Assert.False(classList.IsEnabled);
                Assert.False(check.IsEnabled);
                plc.SelectedIndex = 0;
                Assert.Equal("dbo.Prog", Assert.IsType<RecordCheckScope>(Assert.Single(classList.Items)).Class.TableName);
                Assert.False(check.IsEnabled);
                classList.SelectedIndex = 0;
                Assert.True(check.IsEnabled);
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => result.Text.Length > 0, status, timeout.Token);
                Assert.Contains("0–99", result.Text);
                Assert.Contains("34, 48, 66", result.Text);

                plc.SelectedIndex = 1;
                Assert.Empty(result.Text);
                Assert.Equal("dbo.Valves", Assert.IsType<RecordCheckScope>(Assert.Single(classList.Items)).Class.TableName);
                Assert.False(check.IsEnabled);
                classList.SelectedIndex = 0;
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => result.Text.Length > 0, status, timeout.Token);
                Assert.Contains("Свободных Record нет.", result.Text);

                databases.SelectedItem = "Other DB";
                await WaitFor(() => plc.IsEnabled, status, timeout.Token);
                Assert.Empty(result.Text);
                Assert.False(classList.IsEnabled);
                Assert.Null(plc.SelectedItem);
                plc.SelectedItem = Assert.Single(plc.Items);
                Assert.Equal("dbo.Motors", Assert.IsType<RecordCheckScope>(Assert.Single(classList.Items)).Class.TableName);
            }
            finally
            {
                window.Close();
            }
        });

        static async Task WaitFor(Func<bool> predicate, TextBlock status, CancellationToken ct)
        {
            while (!predicate() && !ct.IsCancellationRequested)
                await Task.Delay(30);
            Assert.True(predicate(), status.Text);
        }
    }

    private static ProjectTableSchema Schema(string tableName, params string[] columns) => new()
    {
        TableName = tableName,
        Columns = columns.Select(name => new ProjectColumnSchema { Name = name }).ToList()
    };
}
