using System.Text.Json.Nodes;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public class UnifiedLanguageTests
{
    [Fact]
    public async Task OfflineToggleMirrorsExistingInsertedAndEditedRows()
    {
        var project = new ConfiguratorProject();
        var schema = new ProjectTableSchema { TableName = "dbo.Objects", HasStableKey = true, KeyColumns = { "Record" } };
        foreach (string name in new[] { "Record", "Name_L1", "Name_L2" })
            schema.Columns.Add(new ProjectColumnSchema { Name = name, DataType = name == "Record" ? "int" : "nvarchar", IsNullable = true });
        project.TableSchemas.Add(schema);
        var source = new OfflineProjectDataSource(project);
        var values = new Dictionary<string, object?> { ["Record"] = 1, ["Name_L1"] = "a", ["Name_L2"] = "b" };
        await source.CreateRecordAsync(schema.TableName, values);
        Assert.Equal("b", project.LocalRecords[0].Values["Name_L2"]!.ToString());
        var registry = new ProjectRecord { TableName = "dbo.Classes", Values = new JsonObject { ["Name_L1"] = "a", ["Name_L2"] = "b" } };
        project.LocalRecords.Add(registry);
        UnifiedLanguage.SetOffline(source, true);
        Assert.Equal("b", registry.Values["Name_L2"]!.ToString());
        Assert.Equal("a", project.LocalRecords[0].Values["Name_L2"]!.ToString());
        values["Record"] = 2;
        await source.CreateRecordAsync(schema.TableName, values);
        Assert.Equal("a", project.LocalRecords.Single(r => r.TableName == schema.TableName && r.Values["Record"]?.ToString() == "2").Values["Name_L2"]!.ToString());
        await source.UpdateRecordAsync(schema.TableName, schema.KeyColumns, values,
            new Dictionary<string, object?> { ["Name_L1"] = null });
        Assert.Null(project.LocalRecords.Single(r => r.TableName == schema.TableName && r.Values["Record"]?.ToString() == "2").Values["Name_L2"]);
        UnifiedLanguage.SetOffline(source, false);
        values["Record"] = 3;
        await source.CreateRecordAsync(schema.TableName, values);
        Assert.Equal("b", project.LocalRecords.Single(r => r.TableName == schema.TableName && r.Values["Record"]?.ToString() == "3").Values["Name_L2"]!.ToString());
    }

    [CompareLocalDbFact]
    public async Task OnlineToggleMirrorsExistingAndNewRows()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.Classes (Record int PRIMARY KEY, NameL1 nvarchar(20), NameL2 nvarchar(20)); INSERT dbo.Classes VALUES(1,N'a',N'b'); CREATE TABLE dbo.Example (Record int PRIMARY KEY, NameL1 nvarchar(20), NameL2 nvarchar(20)); INSERT dbo.Example VALUES(1,N'a',N'b');");
        var service = new DatabaseService(db.ConnectionString);
        await service.SetUnifiedLanguageAsync(true);
        await service.InsertRowAsync("dbo.Example", new() { ["Record"] = 2, ["NameL1"] = "x" });
        await service.UpdateRowAsync("dbo.Example", new() { "Record" }, new() { ["Record"] = 1 }, new() { ["NameL1"] = "y" });
        Assert.Equal(2m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Example WHERE NameL2=NameL1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Classes WHERE NameL2=N'b'"));
        await service.SetUnifiedLanguageAsync(false);
        Assert.False(service.UsesUnifiedLanguage);
    }
}
