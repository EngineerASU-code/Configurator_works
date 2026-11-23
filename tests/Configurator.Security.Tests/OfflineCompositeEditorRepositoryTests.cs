using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineCompositeEditorRepositoryTests
{
    [Fact]
    public async Task ProgramManagementReadsScopedProgramsFromSnapshot()
    {
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "local" },
            TableSchemas = { Schema("dbo.Prog", "Record", "PLC", "PLC_Class_Number", "Name_L1") },
            LocalRecords =
            {
                Record("dbo.Prog", new("Record", 4), new("PLC", 1), new("PLC_Class_Number", 10), new("Name_L1", "A")),
                Record("dbo.Prog", new("Record", 5), new("PLC", 2), new("PLC_Class_Number", 10), new("Name_L1", "B"))
            }
        };
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);

        var rows = await repository.GetProgramsAsync(1, 10);

        var row = Assert.Single(rows.Rows.Cast<System.Data.DataRow>());
        Assert.Equal(4, row["Record"]);
        Assert.Equal("A", row["Name_L1"]);
        Assert.Equal(System.Data.DataRowState.Unchanged, row.RowState);
    }

    [Fact]
    public async Task MatrixRepositoryUpdatesSnapshotUsingStableKey()
    {
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "local" },
            TableSchemas = { Schema("dbo.Matrix_List", "Record", "PLC", "PLC_Class_Number", "Name_L1") },
            LocalRecords =
            {
                Record("dbo.Matrix_List", new("Record", 7), new("PLC", 1), new("PLC_Class_Number", 20), new("Name_L1", "Before"))
            }
        };
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramSupportRepository(source);
        var data = await repository.LoadMatrixAsync("matrix-list", 1, 20);
        data.Rows.Rows[0]["Name_L1"] = "After";

        await repository.SaveAsync(data, new System.Collections.Generic.Dictionary<string, object>
        {
            ["PLC"] = 1, ["PLC_Class_Number"] = 20
        });

        Assert.Equal("After", source.GetRecords("dbo.Matrix_List").Single().Values["Name_L1"]!.GetValue<string>());
    }

    private static ProjectTableSchema Schema(string table, params string[] columns)
    {
        var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
        schema.KeyColumns.AddRange(columns.TakeWhile(name => name == "Record" || name == "PLC" || name == "PLC_Class_Number"));
        foreach (string name in columns)
            schema.Columns.Add(new ProjectColumnSchema { Name = name, DataType = name is "Record" or "PLC" or "PLC_Class_Number" ? "int" : "nvarchar", IsNullable = true });
        return schema;
    }

    private static ProjectRecord Record(string table, params (string Name, object Value)[] values)
    {
        var schema = Schema(table, values.Select(item => item.Name).ToArray());
        var json = new JsonObject();
        foreach (var pair in values) json[pair.Name] = ProjectKeyCodec.ToNode(pair.Value);
        return new ProjectRecord
        {
            TableName = table,
            Key = ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => json[name]),
            Values = json
        };
    }
}
