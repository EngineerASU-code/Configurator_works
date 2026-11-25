#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Configurator;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineObjectDeletionGuardTests
{
    [Fact]
    public async Task LogicalRelationOffersExistingReplacementAndCanDeleteInOneSnapshotTransaction()
    {
        var project = LogicalRelationProject(includeReplacement: true);
        var source = new OfflineProjectDataSource(project);
        var options = OfflineObjectDeletionGuard.BuildOptions(source, "dbo.Parents", [new() { ["Id"] = 1 }]);

        var option = Assert.Single(options);
        var candidate = Assert.Single(option.Candidates);
        Assert.Equal(new object[] { 2 }, candidate.Values);
        Assert.Equal("Зависимый", option.Hit.SourceName);
        Assert.Equal("Удаляемый", option.Hit.TargetName);
        Assert.Equal(1, option.Hit.TargetKey["Id"]);
        Assert.Equal("Замена", candidate.Display);
        Assert.Equal(2, candidate.DisplayKey["Id"]);

        var replacement = new ReferenceReplacement
        {
            SourceTable = option.Hit.SourceTable,
            SourceColumns = option.Hit.Relation.SourceColumns,
            NewValues = candidate.Values,
            KeyColumns = new List<string>(option.Hit.SourceKey.Keys),
            KeyValues = new List<object>(option.Hit.SourceKey.Values),
            ExpectedOldValues = option.Hit.SourceReferenceValues
        };
        var child = source.GetRecords("dbo.Children").Single();
        var target = source.GetRecords("dbo.Parents").Single(record => record.Values["Id"]!.GetValue<int>() == 1);

        await source.ExecuteMutationTransactionAsync(async () =>
        {
            await source.UpdateRecordAsync(replacement.SourceTable, replacement.KeyColumns,
                new Dictionary<string, object?> { ["Id"] = replacement.KeyValues[0] },
                new Dictionary<string, object?> { ["ParentId"] = replacement.NewValues[0] });
            Assert.True(project.LocalRecords.Remove(target));
        });

        Assert.Equal(2, child.Values["ParentId"]!.GetValue<int>());
        Assert.DoesNotContain(project.LocalRecords, record => record.Values["Id"]?.GetValue<int>() == 1);
        Assert.Equal(2, project.LocalRecords.Count);
    }

    [Fact]
    public void LogicalRelationBlocksDeleteWhenThereIsNoReplacementCandidate()
    {
        var source = new OfflineProjectDataSource(LogicalRelationProject(includeReplacement: false));
        var error = Assert.Throws<System.InvalidOperationException>(() =>
            OfflineObjectDeletionGuard.BuildOptions(source, "dbo.Parents", [new() { ["Id"] = 1 }]));
        Assert.Contains("нет допустимой записи для замены", error.Message);
    }

    [Fact]
    public void CheckBlocksLinkedDeleteEvenWhenReplacementExists()
    {
        var source = new OfflineProjectDataSource(LogicalRelationProject(includeReplacement: true));
        var error = Assert.Throws<System.InvalidOperationException>(() =>
            OfflineObjectDeletionGuard.Check(source, "dbo.Parents", [new() { ["Id"] = 1 }]));
        Assert.Contains("Сначала выберите замену", error.Message);
    }

    [Fact]
    public void UnreferencedLogicalTargetNeedsNoReplacement()
    {
        var source = new OfflineProjectDataSource(LogicalRelationProject(includeReplacement: false));
        Assert.Empty(OfflineObjectDeletionGuard.BuildOptions(source, "dbo.Parents", [new() { ["Id"] = 2 }]));
    }

    [CompareLocalDbFact]
    public async System.Threading.Tasks.Task PhysicalOnlineAndPersistedOfflineRelationReplaceAndDeleteTheSameRows()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Area (AreaId int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
            CREATE TABLE dbo.Equipment (EquipmentId int NOT NULL PRIMARY KEY, AreaId int NOT NULL, Name nvarchar(50) NOT NULL,
                CONSTRAINT FK_Equipment_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area(AreaId));
            INSERT dbo.Area (AreaId, Name) VALUES (1, N'Old'), (2, N'Replacement');
            INSERT dbo.Equipment (EquipmentId, AreaId, Name) VALUES (10, 1, N'Dependent');
            """);

        var sqlSchema = await new DatabaseArchitectureAnalyzer(db.ConnectionString).AnalyzeAsync();
        var onlineGuard = new DeletionProtectionService(db.ConnectionString, sqlSchema);
        var plan = await onlineGuard.BuildPlanAsync(new DeletionRequest
        {
            TableName = "dbo.Area", Key = new Dictionary<string, object> { ["AreaId"] = 1 },
            ExpectedValues = new Dictionary<string, object> { ["AreaId"] = 1, ["Name"] = "Old" }
        });
        Assert.True(plan.CanCommit);
        var hit = Assert.Single(plan.Dependencies);
        var onlineCandidate = Assert.Single(await onlineGuard.GetReplacementCandidatesAsync(hit,
            new Dictionary<string, object> { ["AreaId"] = 1 }));
        Assert.Empty(await onlineGuard.GetReplacementCandidatesAsync(hit, plan.Request.Key,
            excludedTargets: [new Dictionary<string, object> { ["AreaId"] = 2 }]));
        Assert.Equal("Dependent", DependencyRecordLabel.Name(hit.SourceValues));
        Assert.Equal("Old", hit.TargetName);
        Assert.Equal(1, hit.TargetKey["AreaId"]);
        Assert.Equal("Replacement", onlineCandidate.Display);
        Assert.Equal(2, onlineCandidate.DisplayKey["AreaId"]);
        await onlineGuard.ExecuteAsync(plan, [new ReferenceReplacement
        {
            SourceTable = hit.SourceTable, SourceColumns = hit.Relation.SourceColumns,
            NewValues = onlineCandidate.Values, TargetValues = onlineCandidate.TargetValues, KeyColumns = hit.SourceKey.Keys.ToList(),
            KeyValues = hit.SourceKey.Values.ToList(), ExpectedOldValues = hit.SourceReferenceValues,
            AdditionalAssignments = onlineCandidate.AutoAssignments
        }], (_, _, _) => System.Threading.Tasks.Task.FromResult(true));
        Assert.Equal(2, await db.DecimalAsync("SELECT AreaId FROM dbo.Equipment WHERE EquipmentId = 10"));
        Assert.Equal(0, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Area WHERE AreaId = 1"));

        var areaSchema = new ProjectTableSchema
        {
            TableName = "dbo.Area", KeyColumns = ["AreaId"], HasStableKey = true,
            Columns = [new() { Name = "AreaId", DataType = "int" }, new() { Name = "Name", DataType = "nvarchar" }]
        };
        var equipmentSchema = new ProjectTableSchema
        {
            TableName = "dbo.Equipment", KeyColumns = ["EquipmentId"], HasStableKey = true,
            Columns = [new() { Name = "EquipmentId", DataType = "int" }, new() { Name = "AreaId", DataType = "int" }]
        };
        var offlineProject = new ConfiguratorProject
        {
            TableSchemas = [areaSchema, equipmentSchema],
            GraphRelations = JsonSerializer.SerializeToNode(new GraphRelationProjectDocument
            {
                ManualRelations = [new GraphRelationDefinition
                {
                    Source = "dbo.Equipment", SourceColumn = "AreaId", Target = "dbo.Area", TargetColumn = "AreaId",
                    Rule = "Persisted Area dependency"
                }]
            })!.AsObject(),
            LocalRecords =
            [
                Record(areaSchema, 1, "Old"), Record(areaSchema, 2, "Replacement"),
                new ProjectRecord
                {
                    TableName = "dbo.Equipment",
                    Key = ProjectKeyCodec.Encode(equipmentSchema, equipmentSchema.KeyColumns, _ => JsonValue.Create(10)),
                    Values = new JsonObject { ["EquipmentId"] = 10, ["AreaId"] = 1 }
                }
            ]
        };
        var offline = new OfflineProjectDataSource(offlineProject);
        var options = OfflineObjectDeletionGuard.BuildOptions(offline, "dbo.Area", [new() { ["AreaId"] = JsonValue.Create(1)! }]);
        var option = Assert.Single(options);
        var offlineCandidate = Assert.Single(option.Candidates);
        await offline.ExecuteMutationTransactionAsync(async () =>
        {
            await offline.UpdateRecordAsync(option.Hit.SourceTable, option.Hit.SourceKey.Keys.ToList(),
                option.Hit.SourceKey, new Dictionary<string, object?> { ["AreaId"] = offlineCandidate.Values[0] });
            offlineProject.LocalRecords.RemoveAll(record => record.TableName == "dbo.Area" && record.Values["AreaId"]?.GetValue<int>() == 1);
        });
        var equipment = Assert.Single(offline.GetRecords("dbo.Equipment"));
        Assert.Equal(2, equipment.Values["AreaId"]!.GetValue<int>());
        Assert.DoesNotContain(offlineProject.LocalRecords, record => record.TableName == "dbo.Area" && record.Values["AreaId"]?.GetValue<int>() == 1);
    }

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

    private static ConfiguratorProject LogicalRelationProject(bool includeReplacement)
    {
        var parentSchema = new ProjectTableSchema
        {
            TableName = "dbo.Parents", KeyColumns = ["Id"], HasStableKey = true,
            Columns = [new ProjectColumnSchema { Name = "Id", DataType = "int" }, new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" }]
        };
        var childSchema = new ProjectTableSchema
        {
            TableName = "dbo.Children", KeyColumns = ["Id"], HasStableKey = true,
            Columns = [new ProjectColumnSchema { Name = "Id", DataType = "int" }, new ProjectColumnSchema { Name = "ParentId", DataType = "int" }, new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" }]
        };
        var project = new ConfiguratorProject
        {
            TableSchemas = [parentSchema, childSchema],
            GraphRelations = JsonSerializer.SerializeToNode(new GraphRelationProjectDocument
            {
                ManualRelations = [new GraphRelationDefinition
                {
                    Source = "dbo.Children", SourceColumn = "ParentId", Target = "dbo.Parents", TargetColumn = "Id",
                    Rule = "Test logical parent reference"
                }]
            })!.AsObject(),
            LocalRecords = new List<ProjectRecord> { Record(parentSchema, 1, "Удаляемый") }
        };
        if (includeReplacement) project.LocalRecords.Add(Record(parentSchema, 2, "Замена"));
        project.LocalRecords.Add(new ProjectRecord
        {
            TableName = "dbo.Children", Key = ProjectKeyCodec.Encode(childSchema, childSchema.KeyColumns, _ => JsonValue.Create(10)),
            Values = new JsonObject { ["Id"] = 10, ["ParentId"] = 1, ["Name"] = "Зависимый" }
        });
        return project;
    }

    private static ProjectRecord Record(ProjectTableSchema schema, int id, string name)
    {
        var values = new JsonObject();
        foreach (var column in schema.Columns)
            values[column.Name] = column.Name.Equals(schema.KeyColumns.Single(), System.StringComparison.OrdinalIgnoreCase)
                ? JsonValue.Create(id)
                : column.Name.Equals("Name", System.StringComparison.OrdinalIgnoreCase) ? JsonValue.Create(name) : null;
        return new ProjectRecord
        {
            TableName = schema.TableName,
            Key = ProjectKeyCodec.Encode(schema, schema.KeyColumns, _ => JsonValue.Create(id)),
            Values = values
        };
    }
}
