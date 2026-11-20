#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProjectCrudContractTests
{
    private static ConfiguratorProject Project() => new()
    {
        Manifest = new ConfiguratorProjectManifest { Name = "CRUD contract", ModifiedUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
        TableSchemas = { new ProjectTableSchema
        {
            TableName = "dbo.Items",
            KeyColumns = { "Id" },
            HasStableKey = true,
            Columns =
            {
                new ProjectColumnSchema { Name = "Id", DataType = "int" },
                new ProjectColumnSchema { Name = "Name", DataType = "nvarchar", IsNullable = true }
            }
        } },
        LocalRecords =
        {
            new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Id"] = 1, ["Name"] = "one" } },
            new ProjectRecord { TableName = "dbo.Items", Key = "2", Values = new JsonObject { ["Id"] = 2, ["Name"] = "two" } }
        }
    };

    [Fact]
    public async Task OfflineContractCreatesUpdatesAndDeletesOnlyLocalRecords()
    {
        var project = Project();
        var source = new OfflineProjectDataSource(project);
        var baselineCount = project.BaselineRecords.Count;

        await source.CreateRecordAsync("dbo.Items", new Dictionary<string, object?> { ["Id"] = 3, ["Name"] = "three" });
        await source.UpdateRecordAsync("dbo.Items", new[] { "Id" },
            new Dictionary<string, object?> { ["Id"] = 2 },
            new Dictionary<string, object?> { ["Name"] = "updated" });
        var deleted = await source.DeleteRecordsAsync("dbo.Items", new[] { "Id" }, new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["Id"] = 1 }
        });

        Assert.Equal(1, deleted);
        Assert.Equal(2, source.GetRecords("dbo.Items").Count);
        Assert.Equal("updated", source.GetRecords("dbo.Items").Single(x => x.Values["Id"]!.ToString() == "2").Values["Name"]!.GetValue<string>());
        Assert.Equal(baselineCount, project.BaselineRecords.Count);
        Assert.True(project.Manifest.ModifiedUtc >= DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task OfflineContractRejectsDuplicateKeyAndMissingSchemaKey()
    {
        var source = new OfflineProjectDataSource(Project());
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CreateRecordAsync("dbo.Items",
            new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "duplicate" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CreateRecordAsync("dbo.Unknown",
            new Dictionary<string, object?> { ["Id"] = 9 }));
    }

    [Fact]
    public async Task OfflineCreateWithKeyReturnsGeneratedIdentity()
    {
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "Generated key" },
            TableSchemas = { new ProjectTableSchema
            {
                TableName = "dbo.Items", KeyColumns = { "Id" }, HasStableKey = true,
                Columns =
                {
                    new ProjectColumnSchema { Name = "Id", DataType = "int", IsIdentity = true },
                    new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" }
                }
            } },
            LocalRecords =
            {
                new ProjectRecord { TableName = "dbo.Items", Key = "one", Values = new JsonObject { ["Id"] = 1, ["Name"] = "one" } },
                new ProjectRecord { TableName = "dbo.Items", Key = "two", Values = new JsonObject { ["Id"] = 4, ["Name"] = "two" } }
            }
        };
        var source = new OfflineProjectDataSource(project);
        using var session = new ProjectCrudSession(source);

        var key = await session.CreateRecordAndGetKeyAsync("dbo.Items",
            new Dictionary<string, object?> { ["Name"] = "three" });

        Assert.Equal(5L, key["Id"]);
        Assert.Equal("5", source.GetRecords("dbo.Items").Single(x => x.Values["Name"]!.ToString() == "three").Values["Id"]!.ToString());
    }
}
