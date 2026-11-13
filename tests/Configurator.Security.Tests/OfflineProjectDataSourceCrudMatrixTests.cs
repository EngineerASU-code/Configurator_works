using System;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineProjectDataSourceCrudMatrixTests
{
    private static ConfiguratorProject NewProject() => new()
    {
        Manifest = new ConfiguratorProjectManifest { Name = "CRUD test", ModifiedUtc = DateTimeOffset.UtcNow.AddDays(-1) },
        TableSchemas = { new ProjectTableSchema { TableName = "dbo.Items", KeyColumns = { "Id" }, HasStableKey = true } },
        LocalRecords =
        {
            new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Name"] = "alpha" }, RowVersion = "v1" },
            new ProjectRecord { TableName = "dbo.Items", Key = "2", Values = new JsonObject { ["Name"] = "beta" }, RowVersion = "v1" }
        },
        BaselineRecords = { new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Name"] = "baseline" } } }
    };

    [Fact]
    public void Create_AddsRecordAndUpdatesModifiedTimestamp()
    {
        var project = NewProject();
        var oldModified = project.Manifest.ModifiedUtc;
        var source = new OfflineProjectDataSource(project);
        source.UpsertRecord(new ProjectRecord { TableName = "dbo.Items", Key = "3", Values = new JsonObject { ["Name"] = "gamma" } });
        Assert.Equal(3, source.GetRecords("dbo.Items").Count);
        Assert.Equal("gamma", source.GetRecords("dbo.Items").Single(x => x.Key == "3").Values["Name"]!.GetValue<string>());
        Assert.True(project.Manifest.ModifiedUtc >= oldModified);
    }

    [Fact]
    public void UpdateSameTableAndKeyDoesNotCreateDuplicate()
    {
        var project = NewProject();
        var source = new OfflineProjectDataSource(project);
        source.UpsertRecord(new ProjectRecord { TableName = "DBO.ITEMS", Key = "1", Values = new JsonObject { ["Name"] = "updated" }, RowVersion = "v2" });
        Assert.Equal(2, source.GetRecords("dbo.Items").Count);
        var updated = Assert.Single(source.GetRecords("dbo.Items"), x => x.Key == "1");
        Assert.Equal("updated", updated.Values["Name"]!.GetValue<string>());
        Assert.Equal("v2", updated.RowVersion);
    }

    [Fact]
    public void KeysAreOrdinalAndCaseSensitiveWhileTableNamesAreCaseInsensitive()
    {
        var project = NewProject();
        var source = new OfflineProjectDataSource(project);
        source.UpsertRecord(new ProjectRecord { TableName = "DBO.ITEMS", Key = "a", Values = new JsonObject() });
        source.UpsertRecord(new ProjectRecord { TableName = "dbo.items", Key = "A", Values = new JsonObject() });
        Assert.Equal(4, source.GetRecords("dbo.Items").Count);
    }

    [Fact]
    public void DeleteRemovesOnlyExactTableAndKeyAndReturnsStatus()
    {
        var project = NewProject();
        var source = new OfflineProjectDataSource(project);
        Assert.True(source.DeleteRecord("DBO.ITEMS", "1"));
        Assert.False(source.DeleteRecord("dbo.Items", "1"));
        Assert.Single(source.GetRecords("dbo.Items"));
        Assert.Single(project.BaselineRecords);
    }

    [Fact]
    public void DeleteDoesNotTreatKeyAsCaseInsensitive()
    {
        var project = NewProject();
        var source = new OfflineProjectDataSource(project);
        Assert.False(source.DeleteRecord("dbo.Items", "01"));
        Assert.Equal(2, source.GetRecords("dbo.Items").Count);
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("", "key")]
    [InlineData("dbo.Items", null)]
    [InlineData("dbo.Items", "")]
    [InlineData(" ", "key")]
    public void UpsertRejectsMissingTableOrKeyWithoutChangingData(string table, string key)
    {
        var project = NewProject();
        var source = new OfflineProjectDataSource(project);
        var before = project.LocalRecords.Count;
        Assert.Throws<ArgumentException>(() => source.UpsertRecord(new ProjectRecord { TableName = table, Key = key }));
        Assert.Equal(before, project.LocalRecords.Count);
    }

    [Fact]
    public void UpsertRejectsNullRecord()
    {
        var source = new OfflineProjectDataSource(NewProject());
        Assert.Throws<ArgumentNullException>(() => source.UpsertRecord(null!));
    }

    [Fact]
    public void SchemaUpsertCreatesThenReplacesCaseInsensitively()
    {
        var project = NewProject();
        var source = new OfflineProjectDataSource(project);
        source.UpsertSchema(new ProjectTableSchema { TableName = "dbo.NewTable", KeyColumns = { "Id" } });
        source.UpsertSchema(new ProjectTableSchema { TableName = "DBO.NEWTABLE", KeyColumns = { "Code" } });
        Assert.Equal(2, project.TableSchemas.Count);
        Assert.Equal("Code", Assert.Single(source.GetSchema("dbo.NewTable")!.KeyColumns));
    }

    [Fact]
    public void SchemaUpsertRejectsBlankNameWithoutMutation()
    {
        var project = NewProject();
        var source = new OfflineProjectDataSource(project);
        Assert.Throws<ArgumentException>(() => source.UpsertSchema(new ProjectTableSchema { TableName = " " }));
        Assert.Single(project.TableSchemas);
    }

    [Fact]
    public void GetTablesReturnsDistinctNamesAcrossSchemaMappingsAndRecords()
    {
        var project = NewProject();
        project.Mappings.Add(new ProjectMapping { EntityName = "Item", TableName = "dbo.Items" });
        project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.Other", Key = "1" });
        var names = new OfflineProjectDataSource(project).GetTables().Select(x => x.TableName).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("dbo.Items", names);
        Assert.Contains("dbo.Other", names);
    }

    [Fact]
    public void BaselineIsNotChangedByLocalCreateUpdateOrDelete()
    {
        var project = NewProject();
        var source = new OfflineProjectDataSource(project);
        source.UpsertRecord(new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Name"] = "local-change" } });
        source.UpsertRecord(new ProjectRecord { TableName = "dbo.Items", Key = "3", Values = new JsonObject() });
        source.DeleteRecord("dbo.Items", "2");
        Assert.Single(project.BaselineRecords);
        Assert.Equal("baseline", project.BaselineRecords[0].Values["Name"]!.GetValue<string>());
    }

    [Fact]
    public void ResolveTableNameDoesNotGuessWhenLeafNameIsAmbiguous()
    {
        var project = NewProject();
        project.TableSchemas.Add(new ProjectTableSchema { TableName = "archive.Items" });
        var source = new OfflineProjectDataSource(project);
        Assert.Equal("Items", source.ResolveTableName("Items"));
        Assert.Null(source.GetSchema("Items"));
    }
}
