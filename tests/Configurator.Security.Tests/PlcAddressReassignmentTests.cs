#nullable enable
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

        await workspace.ReassignPlcAddressAsync(owner, "I3.0");
        Assert.Equal(1, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I3.0'"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReassignPlcAddressAsync(owner, "I4.0"));
        Assert.Equal(0, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves WHERE Address_Input='I4.0'"));
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

    private static async Task<AddressOwner> Owner(ProjectWorkspace workspace)
    {
        var map = (await PlcAddressCatalog.LoadAsync(workspace, default))["1"];
        var descriptions = map.Rows().Single(r => r.Label == "I1").Bits[0];
        return descriptions.Select(map.FindOwner).First(o => o != null && ProjectClassCatalog.Read(o.Record, "Record") == "1")!;
    }
}
