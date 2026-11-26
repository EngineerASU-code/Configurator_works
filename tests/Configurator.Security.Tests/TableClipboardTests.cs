#nullable disable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public class TableClipboardTests
{
    [Fact]
    public void PreservesQuotedCellsAndEmptyTrailingCells()
    {
        object[][] rows = { new object[] { 1, "a\tb\r\nc\"d", DBNull.Value }, new object[] { 2, "x", "" } };

        var result = TableClipboard.Read(TableClipboard.Write(new[] { "Record", "Field\tName", "Value" }, rows));

        Assert.Equal(new[] { "Record", "Field\tName", "Value" }, result[0]);
        Assert.Equal(new[] { "1", "a\tb\r\nc\"d", "" }, result[1]);
        Assert.Equal(new[] { "2", "x", "" }, result[2]);
    }

    [CompareLocalDbFact]
    public async Task PasteChecksCompositeRecordAndRollsBackWhenReplacementChanges()
    {
        await using var fixture = await CompareLocalDb.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE dbo.ClipboardRows (PLC int NOT NULL, Record int NOT NULL, Value int NOT NULL,
                PRIMARY KEY (PLC, Record));
            INSERT dbo.ClipboardRows VALUES (1, 5, 10);
            """);
        var database = new DatabaseService(fixture.ConnectionString);
        const string table = "dbo.ClipboardRows";
        string[] primaryKey = { "PLC", "Record" };
        Dictionary<string, object> existing = new() { ["PLC"] = 1, ["Record"] = 5 };
        Dictionary<string, object> newKey = new() { ["PLC"] = 2, ["Record"] = 5 };
        var before = Assert.Single(await database.FindTablePasteRowsAsync(table, existing));
        Assert.Empty(await database.FindTablePasteRowsAsync(table, newKey));
        Dictionary<string, object> newValues = new(newKey) { ["Value"] = 20 };
        Dictionary<string, object> replacement = new() { ["Value"] = 30 };

        await fixture.ExecuteAsync("UPDATE dbo.ClipboardRows SET Value = 11 WHERE PLC = 1 AND Record = 5");
        var plan = new[] { (newKey, newValues, (Dictionary<string, object>)null), (existing, replacement, before) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.ApplyTablePasteAsync(table, primaryKey, plan));
        Assert.Empty(await database.FindTablePasteRowsAsync(table, newKey));
        Assert.Equal(11, Assert.Single(await database.FindTablePasteRowsAsync(table, existing))["Value"]);

        plan[1].Item3 = Assert.Single(await database.FindTablePasteRowsAsync(table, existing));
        await database.ApplyTablePasteAsync(table, primaryKey, plan);
        Assert.Equal(20, Assert.Single(await database.FindTablePasteRowsAsync(table, newKey))["Value"]);
        Assert.Equal(30, Assert.Single(await database.FindTablePasteRowsAsync(table, existing))["Value"]);
    }
}
