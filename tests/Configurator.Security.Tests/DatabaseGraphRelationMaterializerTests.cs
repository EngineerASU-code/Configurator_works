using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Configurator;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class DatabaseGraphRelationMaterializerTests
{
    [Fact]
    public void Materialize_PreservesPhysicalForeignKeysAndFiltersOnlyExistingSuppressedInference()
    {
        var schema = Schema();
        schema.Relations.Add(Relation(DatabaseRelationKind.PhysicalForeignKey, "FK_A", "Id", "Id"));
        schema.Relations.Add(Relation(DatabaseRelationKind.PhysicalForeignKey, "FK_B", "Id", "Id"));
        schema.Relations.Add(Relation(DatabaseRelationKind.ConventionalLookup, "lookup", "Id", "Id"));
        var document = new GraphRelationProjectDocument
        {
            SuppressedAutomaticRelations = new List<string>
            {
                DatabaseGraphRelationMaterializer.AutomaticId("dbo.Source", "dbo.Target", new[] { "Id" }, new[] { "Id" }),
                "LOGICAL|dbo.Missing|dbo.Target|Id|Id"
            }
        };

        var result = DatabaseGraphRelationMaterializer.Materialize(schema, document);

        Assert.Equal(2, result.Count);
        Assert.All(result, relation => Assert.Equal(DatabaseGraphRelationOrigin.PhysicalForeignKey, relation.Origin));
        Assert.Equal(new[] { "FK_A", "FK_B" }, result.Select(relation => relation.Id.Split('|')[1]));
    }

    [Fact]
    public void Materialize_UsesCurrentColumnsAndManualDocumentAsSourceOfTruth()
    {
        var schema = Schema();
        schema.Relations.Add(Relation(DatabaseRelationKind.UnresolvedCandidate, "manual-dbo.Source-dbo.Target", "Removed", "Id"));
        schema.Relations.Add(Relation(DatabaseRelationKind.ConventionalLookup, "bad", "Missing", "Id"));
        schema.Relations.Add(Relation(DatabaseRelationKind.ConventionalLookup, "good", "Id", "Id"));
        schema.Relations.Add(new DatabaseRelation
        {
            SourceTable = "archive.Source",
            TargetTable = "archive.Target",
            Kind = DatabaseRelationKind.ConventionalLookup,
            Confidence = RelationConfidence.High,
            RuleId = "stale-schema"
        });
        var document = new GraphRelationProjectDocument
        {
            ManualRelations = new List<GraphRelationDefinition>
            {
                new() { Source = "dbo.Source", Target = "dbo.Target", SourceColumn = "ManualId", TargetColumn = "Id", Rule = "user rule" },
                new() { Source = "dbo.Gone", Target = "dbo.Target", SourceColumn = "Id", TargetColumn = "Id" }
            }
        };

        var result = DatabaseGraphRelationMaterializer.Materialize(schema, document);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, relation => relation.Origin == DatabaseGraphRelationOrigin.Inferred && relation.Confidence == RelationConfidence.High);
        var manual = Assert.Single(result, relation => relation.IsManual);
        Assert.Equal("user rule", manual.Rule);
        Assert.Equal("ManualId", Assert.Single(manual.SourceColumns));
    }

    [Fact]
    public void PhysicalSchemaSnapshot_DeserializesGetOnlyTablesAndRelations()
    {
        var original = Schema();
        original.Relations.Add(Relation(DatabaseRelationKind.PhysicalForeignKey, "FK_Source_Target", "Id", "Id"));

        var node = JsonSerializer.SerializeToNode(original);
        var restored = DatabaseGraphRelationMaterializer.ReadCapturedSchema(node);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.Tables.Count);
        Assert.Single(restored.Relations);
        Assert.Equal("Id", Assert.Single(restored.Relations[0].SourceColumns));
    }

    [Fact]
    public void WithProjectTables_PreservesColumnMetadataWithoutInventingPrimaryKeys()
    {
        var projectTable = new ProjectTableSchema
        {
            TableName = "dbo.Source",
            KeyColumns = new List<string> { "Id" },
            HasStableKey = false,
            Columns = new List<ProjectColumnSchema>
            {
                new() { Name = "Id", DataType = "int", IsIdentity = true, MaxLength = 4 }
            }
        };

        var schema = DatabaseGraphRelationMaterializer.WithProjectTables(null, new[] { projectTable });

        var table = Assert.Single(schema.Tables);
        var column = Assert.Single(table.Columns);
        Assert.Equal("int", column.DataType);
        Assert.True(column.IsIdentity);
        Assert.False(Assert.Single(table.Keys).IsPrimary);
        Assert.True(Assert.Single(table.Keys).IsUnique);
    }

    private static DatabaseSchema Schema()
    {
        var schema = new DatabaseSchema();
        var source = new DatabaseTable { SchemaName = "dbo", Name = "Source" };
        source.Columns.Add(new DatabaseColumn { Name = "Id" });
        source.Columns.Add(new DatabaseColumn { Name = "ManualId" });
        var target = new DatabaseTable { SchemaName = "dbo", Name = "Target" };
        target.Columns.Add(new DatabaseColumn { Name = "Id" });
        schema.Tables.Add(source);
        schema.Tables.Add(target);
        return schema;
    }

    private static DatabaseRelation Relation(DatabaseRelationKind kind, string rule, string sourceColumn, string targetColumn)
    {
        var relation = new DatabaseRelation
        {
            SourceTable = "dbo.Source",
            TargetTable = "dbo.Target",
            Kind = kind,
            Confidence = kind == DatabaseRelationKind.PhysicalForeignKey ? RelationConfidence.Confirmed : RelationConfidence.High,
            RuleId = rule,
            Comment = rule
        };
        relation.SourceColumns.Add(sourceColumn);
        relation.TargetColumns.Add(targetColumn);
        return relation;
    }
}
