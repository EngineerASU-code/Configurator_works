using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineLookupOptionsServiceTests
{
    [Fact]
    public void Build_UsesSnapshotLookupsAndPreservesStoredKeys()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.Valves", new[] { "Type", "PLC", "Class" });
        AddTable(project, "dbo.Valves_Type", new[] { "Record", "Name_L2" }, "Record");
        AddTable(project, "dbo.PLC", new[] { "Record", "Name" }, "Record");
        AddTable(project, "dbo.Classes", new[] { "Record", "Name" }, "Record");
        AddRecord(project, "dbo.Valves_Type", ("Record", 1), ("Name_L2", "Discret"));
        AddRecord(project, "dbo.Valves_Type", ("Record", 2), ("Name_L2", "AS-i"));
        AddRecord(project, "dbo.PLC", ("Record", 6), ("Name", "PLC06"));
        AddRecord(project, "dbo.Classes", ("Record", 7), ("Name", "Digital output"));

        var options = OfflineLookupOptionsService.Build(project, "Valves", new[]
        {
            new ColumnInfo { Name = "Type", DataType = "int" },
            new ColumnInfo { Name = "PLC", DataType = "int" },
            new ColumnInfo { Name = "Class", DataType = "int" }
        });

        Assert.Equal("1", Assert.Single(options["Type"], item => item.Key == "1").Key);
        Assert.Equal("Discret [1]", Assert.Single(options["Type"], item => item.Key == "1").Display);
        Assert.Equal("6 · PLC06", Assert.Single(options["PLC"]).Display);
        Assert.Equal("7 · Digital output", Assert.Single(options["Class"]).Display);
    }

    [Fact]
    public void Build_LeavesMissingAndEmptyLookupsUnavailable()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.Valves", new[] { "Type", "PLC" });
        AddTable(project, "dbo.Valves_Type", new[] { "Record", "Name_L2" }, "Record");
        // Existing schema with no records is still unavailable as a selectable lookup.

        var options = OfflineLookupOptionsService.Build(project, "dbo.Valves", new[]
        {
            new ColumnInfo { Name = "Type", DataType = "int" },
            new ColumnInfo { Name = "PLC", DataType = "int" }
        });

        Assert.Empty(options);
    }

    private static void AddTable(ConfiguratorProject project, string table, IEnumerable<string> columns, params string[] keys)
        => project.TableSchemas.Add(new ProjectTableSchema
        {
            TableName = table,
            KeyColumns = keys.ToList(),
            Columns = columns.Select(name => new ProjectColumnSchema { Name = name, DataType = name == "Name" || name == "Name_L2" ? "varchar" : "int" }).ToList()
        });

    private static void AddRecord(ConfiguratorProject project, string table, params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values) json[name] = JsonValue.Create(value);
        project.LocalRecords.Add(new ProjectRecord { TableName = table, Values = json });
    }
}
