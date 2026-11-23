using System;
using System.Data;
using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProgramTransitionConditionsTests
{
    private static DataRow Transition()
    {
        var table = new DataTable();
        table.Columns.Add("TransPrev_CondMask", typeof(int));
        table.Columns.Add("TransNext_CondMask", typeof(int));
        for (int i = 1; i <= 8; i++)
        {
            table.Columns.Add($"TransDest_{i:00}", typeof(int));
            table.Columns.Add($"TransDest_{i:00}_CondMask", typeof(int));
        }
        var row = table.NewRow();
        row["TransPrev_CondMask"] = 0x10001;
        row["TransDest_01"] = 99;
        row["TransDest_01_CondMask"] = 0x8000;
        table.Rows.Add(row);
        table.AcceptChanges();
        return row;
    }

    [Fact]
    public void AppliesOnlyEditedBitsAndPreservesOtherBitsAndNulls()
    {
        var row = Transition();
        var grid = ProgramTransitionConditions.Create(row);
        Assert.True((bool)grid.Rows[0]["C00"]);
        Assert.True((bool)grid.Rows[2]["C15"]);
        grid.Rows[0]["C01"] = true;
        grid.Rows[2]["C15"] = false;
        ProgramTransitionConditions.Apply(grid, row);
        Assert.Equal(0x10003, row["TransPrev_CondMask"]);
        Assert.Equal(0, row["TransDest_01_CondMask"]);
        Assert.Equal(99, row["TransDest_01"]);
        Assert.Equal(DBNull.Value, row["TransNext_CondMask"]);
        Assert.Equal(DBNull.Value, row["TransDest_02"]);
    }

    [Fact]
    public void AppliesEditedDestinationWithoutChangingItsNullMask()
    {
        var row = Transition();
        var grid = ProgramTransitionConditions.Create(row);
        grid.Rows[3]["Назначение"] = 42;
        ProgramTransitionConditions.Apply(grid, row);
        Assert.Equal(42, row["TransDest_02"]);
        Assert.Equal(DBNull.Value, row["TransDest_02_CondMask"]);
    }

    [Fact]
    public void RefusesIncompleteTransitionSchema()
    {
        var row = new DataTable().NewRow();
        Assert.Throws<InvalidOperationException>(() => ProgramTransitionConditions.Create(row));
    }
}
