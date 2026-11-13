using Configurator.Core.Architecture;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ArchitectureFingerprintCrudTests
{
    private static DatabaseSchema Schema(bool reverse = false, string columnType = "int", bool nullable = false)
    {
        var schema = new DatabaseSchema { DatabaseName = "test" };
        var a = new DatabaseTable { SchemaName = "dbo", Name = "A" };
        a.Columns.Add(new DatabaseColumn { Name = "Id", DataType = columnType, IsNullable = nullable });
        var b = new DatabaseTable { SchemaName = "dbo", Name = "B" };
        b.Columns.Add(new DatabaseColumn { Name = "Name", DataType = "nvarchar", IsNullable = true });
        if (reverse) { schema.Tables.Add(b); schema.Tables.Add(a); }
        else { schema.Tables.Add(a); schema.Tables.Add(b); }
        return schema;
    }

    [Fact]
    public void SameArchitectureProducesSameFingerprintRegardlessOfTableOrder() =>
        Assert.Equal(ArchitectureFingerprint.Create(Schema()), ArchitectureFingerprint.Create(Schema(reverse: true)));

    [Fact]
    public void ColumnTypeChangeChangesFingerprint() =>
        Assert.NotEqual(ArchitectureFingerprint.Create(Schema()), ArchitectureFingerprint.Create(Schema(columnType: "bigint")));

    [Fact]
    public void NullabilityChangeChangesFingerprint() =>
        Assert.NotEqual(ArchitectureFingerprint.Create(Schema()), ArchitectureFingerprint.Create(Schema(nullable: true)));

    [Fact]
    public void RelationAdditionChangesFingerprint()
    {
        var before = Schema();
        var after = Schema();
        var relation = new DatabaseRelation { RuleId = "fk_a_b", SourceTable = "dbo.A", TargetTable = "dbo.B" };
        relation.SourceColumns.Add("Id");
        relation.TargetColumns.Add("Id");
        after.Relations.Add(relation);
        Assert.NotEqual(ArchitectureFingerprint.Create(before), ArchitectureFingerprint.Create(after));
    }

    [Fact]
    public void FingerprintIsStableForSameSchemaInstance() =>
        Assert.Equal(ArchitectureFingerprint.Create(Schema()), ArchitectureFingerprint.Create(Schema()));
}
