#nullable enable
using System.Collections.Generic;
using System.Data;
using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class Mode3DuplicateValuesTests
{
    [Fact]
    public void Build_AllocatesRecordWithinSourcePlcAndCopiesOnlyWritableColumns()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("PLC", typeof(int));
        table.Columns.Add("Record", typeof(int));
        table.Columns.Add("PLC_Class_Number", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Computed", typeof(int));
        table.Columns.Add("Version", typeof(byte[]));
        var source = table.NewRow();
        source["Id"] = 5;
        source["PLC"] = 1;
        source["Record"] = 2;
        source["PLC_Class_Number"] = 10;
        source["Name"] = "XV-002";
        source["Computed"] = 10;
        source["Version"] = new byte[] { 1 };

        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["PLC"] = "01", ["PLC_Class_Number"] = 10, ["Record"] = 2 },
            new Dictionary<string, object?> { ["PLC"] = 1, ["PLC_Class_Number"] = "010", ["Record"] = 7 },
            new Dictionary<string, object?> { ["PLC"] = 1, ["PLC_Class_Number"] = 20, ["Record"] = 3 },
            new Dictionary<string, object?> { ["PLC"] = 2, ["PLC_Class_Number"] = 10, ["Record"] = 3 }
        };
        var columns = new List<ColumnInfo>
        {
            new() { Name = "Id", DataType = "int", IsIdentity = true },
            new() { Name = "PLC", DataType = "int" },
            new() { Name = "Record", DataType = "int" },
            new() { Name = "PLC_Class_Number", DataType = "int" },
            new() { Name = "Name", DataType = "nvarchar" },
            new() { Name = "Computed", DataType = "int", IsComputed = true },
            new() { Name = "Version", DataType = "rowversion" }
        };

        var result = Mode3DuplicateValues.Build(source, columns, rows, classColumn: "PLC_Class_Number");

        Assert.Equal(3, result["Record"]);
        Assert.Equal(1, result["PLC"]);
        Assert.Equal("XV-002", result["Name"]);
        Assert.DoesNotContain("Id", result.Keys);
        Assert.DoesNotContain("Computed", result.Keys);
        Assert.DoesNotContain("Version", result.Keys);
    }
}
