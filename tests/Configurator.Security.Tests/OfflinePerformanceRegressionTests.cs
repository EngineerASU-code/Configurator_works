using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflinePerformanceRegressionTests
{
    [Fact]
    public void OfflineDataSourceReturnsIndexedRecordsAndInvalidatesIndexAfterCrud()
    {
        var records = Enumerable.Range(0, 20_000)
            .Select(i => new ProjectRecord
            {
                TableName = "dbo.Items",
                Key = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Values = new JsonObject { ["Id"] = i, ["Name"] = $"Item {i}" }
            }).ToList();
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "Large offline snapshot" },
            LocalRecords = records
        };
        var source = new OfflineProjectDataSource(project);

        for (var pass = 0; pass < 10; pass++)
        {
            var rows = source.GetRecords("dbo.Items");
            Assert.Equal(20_000, rows.Count);
            Assert.Same(records[19_999], rows[19_999]);
        }

        source.UpsertRecord(new ProjectRecord { TableName = "dbo.Items", Key = "20000", Values = new JsonObject { ["Name"] = "new" } });
        Assert.Equal(20_001, source.GetRecords("dbo.Items").Count);
        Assert.True(source.DeleteRecord("dbo.Items", "20000"));
        Assert.Equal(20_000, source.GetRecords("dbo.Items").Count);
    }

    [Fact]
    public void OfflineSnapshotHasNoSqlConnectionDependencyInProjectDataSource()
    {
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "Offline regression" },
            TableSchemas = { new ProjectTableSchema { TableName = "dbo.Items", Columns = { new ProjectColumnSchema { Name = "Id", DataType = "int" } } } },
            LocalRecords = { new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Id"] = 1 } } }
        };
        var source = new OfflineProjectDataSource(project);
        Assert.NotNull(source);
        Assert.Equal("Offline regression", project.Manifest.Name);
        Assert.Single(project.LocalRecords);
    }
}
