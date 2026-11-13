#nullable enable
using Configurator.Core.Projects;
using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineCrudIntegrityTests
{
    [Fact]
    public async Task UpdateRejectsOccupiedKeyWithoutChangingSnapshotOrIndex()
    {
        var project = Project();
        project.LocalRecords.Add(Record(1, "first"));
        project.LocalRecords.Add(Record(2, "second"));
        var source = new OfflineProjectDataSource(project);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.UpdateRecordAsync("dbo.Items", new[] { "Id" },
            new Dictionary<string, object?> { ["Id"] = 1 }, new Dictionary<string, object?> { ["Id"] = 2, ["Name"] = "changed" }));

        Assert.Equal(new[] { "1:first", "2:second" }, source.GetRecords("dbo.Items")
            .Select(x => $"{x.Values["Id"]}:{x.Values["Name"]}").ToArray());
    }

    [Fact]
    public async Task UpdateRejectsUnknownAndReadOnlyFieldsWithoutMutation()
    {
        var project = Project();
        project.TableSchemas[0].Columns.Add(new ProjectColumnSchema { Name = "Calculated", DataType = "int", IsComputed = true, IsReadOnly = true });
        project.LocalRecords.Add(Record(1, "first"));
        var before = project.Manifest.ModifiedUtc;
        var source = new OfflineProjectDataSource(project);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.UpdateRecordAsync("dbo.Items", new[] { "Id" },
            new Dictionary<string, object?> { ["Id"] = 1 }, new Dictionary<string, object?> { ["Missing"] = "x" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.UpdateRecordAsync("dbo.Items", new[] { "Id" },
            new Dictionary<string, object?> { ["Id"] = 1 }, new Dictionary<string, object?> { ["Calculated"] = 3 }));

        Assert.Equal("first", source.GetRecords("dbo.Items")[0].Values["Name"]!.GetValue<string>());
        Assert.Equal(before, project.Manifest.ModifiedUtc);
    }

    [Fact]
    public async Task TypedCompositeKeysDoNotCollideOnSeparatorsOrNullAndEmpty()
    {
        var schema = new ProjectTableSchema
        {
            TableName = "dbo.Composite", HasStableKey = true, KeyColumns = { "A", "B" },
            Columns =
            {
                new ProjectColumnSchema { Name = "A", DataType = "nvarchar", IsNullable = true },
                new ProjectColumnSchema { Name = "B", DataType = "nvarchar", IsNullable = true }
            }
        };
        var first = ProjectKeyCodec.Encode(schema, schema.KeyColumns, n => n == "A" ? JsonValue.Create("x|B=y") : JsonValue.Create("z"));
        var second = ProjectKeyCodec.Encode(schema, schema.KeyColumns, n => n == "A" ? JsonValue.Create("x") : JsonValue.Create("y|B=z"));
        var nullKey = ProjectKeyCodec.Encode(schema, new[] { "A" }, _ => null);
        var emptyKey = ProjectKeyCodec.Encode(schema, new[] { "A" }, _ => JsonValue.Create(""));

        Assert.NotEqual(first, second);
        Assert.NotEqual(nullKey, emptyKey);
        Assert.StartsWith(ProjectKeyCodec.Prefix, first);
    }

    [Fact]
    public void NumericKeyEncodingIsCultureIndependent()
    {
        var schema = new ProjectTableSchema
        {
            TableName = "dbo.Numeric", HasStableKey = true, KeyColumns = { "Id" },
            Columns = { new ProjectColumnSchema { Name = "Id", DataType = "decimal", Precision = 12, Scale = 2 } }
        };
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var comma = ProjectKeyCodec.Encode(schema, schema.KeyColumns, _ => JsonValue.Create("12.50"));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var dot = ProjectKeyCodec.Encode(schema, schema.KeyColumns, _ => JsonValue.Create(12.5m));
            Assert.Equal(comma, dot);
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    private static ConfiguratorProject Project() => new()
    {
        TableSchemas =
        {
            new ProjectTableSchema
            {
                TableName = "dbo.Items", HasStableKey = true, KeyColumns = { "Id" },
                Columns =
                {
                    new ProjectColumnSchema { Name = "Id", DataType = "int", IsNullable = false },
                    new ProjectColumnSchema { Name = "Name", DataType = "nvarchar", IsNullable = false, MaxLength = 40 }
                }
            }
        }
    };

    private static ProjectRecord Record(int id, string name) => new()
    {
        TableName = "dbo.Items", Key = id.ToString(), Values = new JsonObject { ["Id"] = id, ["Name"] = name }
    };
}
