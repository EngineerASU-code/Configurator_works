#nullable enable
using System.IO;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;
using Configurator.Core.Workspace;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class PlcAddressReassignmentTests
{
    [CompareLocalDbFact]
    public async Task ReassignmentChecksCurrentOccupancyAndSourceInsideTransaction()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.Valves (Record int PRIMARY KEY, PLC int, Address_Input varchar(30)); INSERT dbo.Valves VALUES (1,1,'I1.0'),(2,1,'I1.0');");
        using var workspace = new ProjectWorkspace(new OnlineProjectDataSource(db.ConnectionString),
            new OnlineProjectCrudDataSource(db.ConnectionString), new ProjectArchitectureDataSource(new ConfiguratorProject()));
        var owner = await Owner(workspace);

        await db.ExecuteAsync("INSERT dbo.Valves VALUES (3,1,'I2.0');");
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReassignPlcAddressAsync(owner, "I2.0"));
        Assert.Equal(2, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I1.0'"));

        var captured = await workspace.ReassignPlcAddressAsync(owner, "I3.0");
        Assert.Equal(1, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I3.0'"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReassignPlcAddressAsync(owner, "I4.0"));
        Assert.Equal(0, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I4.0'"));

        var entry = new ChangeJournalEntry { Changes = new() { captured } };
        Assert.True(entry.CanRestore);
        await new ChangeJournalService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")))
            .RestoreAsync(entry, db.ConnectionString);
        Assert.Equal(2, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I1.0'"));
        Assert.Equal(0, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I3.0'"));
    }

    [CompareLocalDbFact]
    public async Task NumericAnalogAddressKeepsNumericStorageAndRespectsFeedbackOccupancy()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.AnaIn (Record int PRIMARY KEY, PLC int, Address int); INSERT dbo.AnaIn VALUES (1,1,40),(2,1,40); CREATE TABLE dbo.Valves2S (Record int PRIMARY KEY, PLC int, Address_Feedback varchar(30)); INSERT dbo.Valves2S VALUES (1,1,'I50.0');");
        using var workspace = new ProjectWorkspace(new OnlineProjectDataSource(db.ConnectionString),
            new OnlineProjectCrudDataSource(db.ConnectionString), new ProjectArchitectureDataSource(new ConfiguratorProject()));
        var map = (await PlcAddressCatalog.LoadAsync(workspace, default))["1"];
        var owner = map.Rows().Single(r => r.Label == "I40").Bits[0].Select(map.FindOwner)
            .First(o => o != null && ProjectClassCatalog.Read(o.Record, "Record") == "1")!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReassignPlcAddressAsync(owner, "50"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReassignPlcAddressAsync(owner, "49"));
        Assert.DoesNotContain("49", map.FreeAddresses(owner, 'I'));
        Assert.Equal("IW60 · I60–I61 · 2 байта", PlcAddressMap.DescribeAddress("60", 'I', true).Label);
        Assert.Equal("QW5 · Q5–Q6 · 2 байта", PlcAddressMap.DescribeAddress("5", 'Q', true).Label);
        await workspace.ReassignPlcAddressAsync(owner, "60");
        var updated = (await PlcAddressCatalog.LoadAsync(workspace, default))["1"].Rows();
        Assert.All(updated.Where(r => r.First is 40 or 41 or 60 or 61),
            row => Assert.All(row.Bits, owners => Assert.Single(owners)));
        Assert.Equal(60, await db.DecimalAsync("SELECT Address FROM dbo.AnaIn WHERE Record=1"));
    }

    [CompareLocalDbFact]
    public async Task ConcurrentMovesCannotClaimSameDestination()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.Valves (Record int PRIMARY KEY, PLC int, Address_Input varchar(30)); INSERT dbo.Valves VALUES (1,1,'I1.0'),(2,1,'I1.0'),(3,1,'I2.0'),(4,1,'I2.0');");
        using var workspace = new ProjectWorkspace(new OnlineProjectDataSource(db.ConnectionString),
            new OnlineProjectCrudDataSource(db.ConnectionString), new ProjectArchitectureDataSource(new ConfiguratorProject()));
        var map = (await PlcAddressCatalog.LoadAsync(workspace, default))["1"];
        AddressOwner Pick(string row, string id) => map.Rows().Single(r => r.Label == row).Bits[0].Select(map.FindOwner)
            .First(o => o != null && ProjectClassCatalog.Read(o.Record, "Record") == id)!;
        async Task<bool> Move(AddressOwner owner)
        {
            try { await workspace.ReassignPlcAddressAsync(owner, "I10.0"); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Move(Pick("I1", "1")), Move(Pick("I2", "3")));
        Assert.Single(outcomes, x => x);
        Assert.Equal(1, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I10.0'"));
    }

    [CompareLocalDbFact]
    public async Task UnknownAddressBearingClassBlocksWrite()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.Valves (Record int PRIMARY KEY, PLC int, Address_Input varchar(30)); INSERT dbo.Valves VALUES (1,1,'I1.0'),(2,1,'I1.0'); CREATE TABLE dbo.Unregistered (Record int PRIMARY KEY, PLC int, Address_Input varchar(30)); INSERT dbo.Unregistered VALUES (1,1,'I10.0');");
        using var workspace = new ProjectWorkspace(new OnlineProjectDataSource(db.ConnectionString),
            new OnlineProjectCrudDataSource(db.ConnectionString), new ProjectArchitectureDataSource(new ConfiguratorProject()));
        var owner = await Owner(workspace);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReassignPlcAddressAsync(owner, "I10.0"));
        Assert.Equal(2, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I1.0'"));
    }

    [Theory]
    [InlineData("Record", "Address_Input")]
    [InlineData("record", "address_input")]
    public async Task OfflineMovePersistsAddressAndUndoInExistingPackage(string keyField, string addressField)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfgproj");
        var packages = new ProjectPackageService();
        var project = CreateOfflineProject(keyField, addressField);
        try
        {
            await packages.SaveAsync(path, project);
            using var workspace = new ProjectWorkspace(new OfflineProjectDataSource(project),
                architecture: new ProjectArchitectureDataSource(project), allowWrite: false);
            var owner = await Owner(workspace);
            await using (var reserved = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    PlcAddressReassignment.ExecuteOfflineAsync(project, path, reserved, packages, owner, "I8.0", default));
                var entry = await PlcAddressReassignment.ExecuteOfflineAsync(project, path, reserved, packages, owner, "I3.0", default);
                Assert.True(entry.CanRestore);
                Assert.Equal(project.Manifest.ProjectId, entry.ProjectId);
                Assert.Equal("I3.0", project.LocalRecords[0].Values[addressField]!.ToString());
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    PlcAddressReassignment.ExecuteOfflineAsync(project, path, reserved, packages, owner, "I4.0", default));
            }
            var reopened = await packages.OpenAsync(path);
            Assert.Equal("I3.0", reopened.LocalRecords[0].Values[addressField]!.ToString());
            Assert.Equal("I1.0", reopened.LocalRecords[1].Values[addressField]!.ToString());
            Assert.Single((JsonArray)reopened.Architecture["changeJournal"]!);
            var journal = new ChangeJournalService();
            journal.BindOfflineProject(reopened, () => { }, mode: 1);
            journal.RestoreOffline(Assert.Single(journal.LoadRecent()), reopened, () => { });
            Assert.Equal("I1.0", reopened.LocalRecords[0].Values[addressField]!.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OfflineWriteFailurePreservesLiveAndReopenedPackage()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfgproj");
        var packages = new ProjectPackageService();
        var project = CreateOfflineProject();
        try
        {
            await packages.SaveAsync(path, project);
            var originalBytes = await File.ReadAllBytesAsync(path);
            using var workspace = new ProjectWorkspace(new OfflineProjectDataSource(project),
                architecture: new ProjectArchitectureDataSource(project), allowWrite: false);
            var owner = await Owner(workspace);
            packages.SaveCheckpoint = point =>
            {
                if (point == "after-truncate") throw new IOException("Injected package write failure.");
            };
            await using (var reserved = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                await Assert.ThrowsAsync<IOException>(() =>
                    PlcAddressReassignment.ExecuteOfflineAsync(project, path, reserved, packages, owner, "I3.0", default));
            }
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            foreach (var state in new[] { project, await packages.OpenAsync(path) })
            {
                Assert.Equal("I1.0", state.LocalRecords[0].Values["Address_Input"]!.ToString());
                Assert.Null(state.Architecture["changeJournal"]);
            }
        }
        finally { File.Delete(path); }
    }

    private static ConfiguratorProject CreateOfflineProject(string keyField = "Record", string addressField = "Address_Input")
    {
        const string table = "dbo.Valves";
        return new ConfiguratorProject
        {
            TableSchemas = [new()
            {
                TableName = table, HasStableKey = true, KeyColumns = ["Record"], Columns =
                [
                    new() { Name = "Record", DataType = "int" },
                    new() { Name = "PLC", DataType = "int" },
                    new() { Name = "Address_Input", DataType = "varchar", MaxLength = 30 }
                ]
            }],
            LocalRecords = Enumerable.Range(1, 3).Select(id => new ProjectRecord
            {
                TableName = table, Key = id.ToString(), Values = new JsonObject
                {
                    [keyField] = id, ["PLC"] = 1, [addressField] = id <= 2 ? "I1.0" : "I8.0"
                }
            }).ToList()
        };
    }

    private static async Task<AddressOwner> Owner(ProjectWorkspace workspace)
    {
        var map = (await PlcAddressCatalog.LoadAsync(workspace, default))["1"];
        var descriptions = map.Rows().Single(r => r.Label == "I1").Bits[0];
        return descriptions.Select(map.FindOwner).First(o => o != null && ProjectClassCatalog.Read(o.Record, "Record") == "1")!;
    }
}
