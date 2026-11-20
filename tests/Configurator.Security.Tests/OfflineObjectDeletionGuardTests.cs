using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineObjectDeletionGuardTests
{
    [Fact]
    public void CapturedForeignKeyProtectsOnlyRecordsThatCouldHaveDependents()
    {
        var captured = new DatabaseSchema();
        captured.Relations.Add(new DatabaseRelation
        {
            SourceTable = "dbo.Children", TargetTable = "dbo.Parents",
            SourceColumns = { "ParentId" }, TargetColumns = { "Id" },
            Kind = DatabaseRelationKind.PhysicalForeignKey, Confidence = RelationConfidence.Confirmed, RuleId = "FK_Children"
        });
        var project = new ConfiguratorProject
        {
            TableSchemas = [Table("dbo.Children", "ParentId"), Table("dbo.Parents", "Id")],
            LocalRecords =
            [
                new() { TableName = "dbo.Children", Values = new JsonObject { ["ParentId"] = 7 } },
                new() { TableName = "dbo.Children", Values = new JsonObject { ["ParentId"] = null } },
                new() { TableName = "dbo.Parents", Values = new JsonObject { ["Id"] = 7 } }
            ]
        };
        project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(captured);
        var source = new OfflineProjectDataSource(project);

        OfflineObjectDeletionGuard.Check(source, "dbo.Parents", [new() { ["Id"] = 8 }]);
        Assert.Throws<System.InvalidOperationException>(() =>
            OfflineObjectDeletionGuard.Check(source, "dbo.Parents", [new() { ["Id"] = 7 }]));
        Assert.Equal(3, project.LocalRecords.Count);
    }

    private static ProjectTableSchema Table(string name, string column) => new()
    {
        TableName = name, Columns = [new ProjectColumnSchema { Name = column, DataType = "int" }]
    };
}