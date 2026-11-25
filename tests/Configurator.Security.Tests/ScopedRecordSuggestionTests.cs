using System.Data;
using System.Globalization;
using Configurator.Core.Workspace;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ScopedRecordSuggestionTests
{
    [Fact]
    public void SuggestsFirstInternalGapAndFillsItBeforeExtendingRange()
    {
        var suggestion = new ScopedRecordSuggestion();
        foreach (int record in Enumerable.Range(58, 183).Reverse().Where(record => record != 60 && record != 100))
            suggestion.Add(record);

        Assert.Equal(60, suggestion.GetValue());
        suggestion.Add("060");
        Assert.Equal(100, suggestion.GetValue());
        suggestion.Add(100m);
        Assert.Equal(241, suggestion.GetValue());
    }

    [Fact]
    public void StartsAtOneForEmptyPlcAndChecksInt32LimitAfterSearchingGaps()
    {
        var suggestion = new ScopedRecordSuggestion();
        suggestion.Add(DBNull.Value);
        Assert.Equal(1, suggestion.GetValue());
        suggestion.Add(int.MaxValue);
        Assert.Throws<InvalidOperationException>(() => suggestion.GetValue());
        suggestion.Add(int.MaxValue - 2);
        Assert.Equal(int.MaxValue - 1, suggestion.GetValue());
    }

    [Fact]
    public void OfflineUsesAllRowsOfOnlyTheSelectedPlcIncludingRowsHiddenByFilters()
    {
        using var table = new DataTable("Valve");
        table.Columns.Add("PLC", typeof(string));
        table.Columns.Add("Record", typeof(string));
        foreach (int record in Enumerable.Range(58, 183))
            table.Rows.Add("01", record.ToString(CultureInfo.InvariantCulture));
        table.Rows.Add("2", "60");
        table.AcceptChanges();
        table.Rows[2].Delete();
        table.DefaultView.RowFilter = "Record = '240'";

        Assert.Equal(60, MainWindow.SuggestOfflineScopedRecord(table, "1"));
        Assert.Equal(61, MainWindow.SuggestOfflineScopedRecord(table, "2"));
        Assert.Equal(1, MainWindow.SuggestOfflineScopedRecord(table, "3"));
    }

    [CompareLocalDbFact]
    public async Task OnlineUsesOnlyTheSelectedTableAndPlc()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Valve (PLC int NOT NULL, Record nvarchar(20) NOT NULL, PRIMARY KEY (PLC, Record));
            CREATE TABLE dbo.Motor (PLC int NOT NULL, Class int NOT NULL, Record int NOT NULL, PRIMARY KEY (PLC, Class, Record));
            INSERT dbo.Valve VALUES (1,N'58'),(1,N'059'),(1,N'61'),(2,N'60');
            INSERT dbo.Motor VALUES (1,10,58),(1,10,60),(1,20,59),(2,10,59);
            """);
        var service = new DatabaseService(db.ConnectionString);

        Assert.Equal(60, await service.GetFreeScopedRecordValueAsync("dbo.Valve", "Record", "PLC", 1, default));
        Assert.Equal(61, await service.GetFreeScopedRecordValueAsync("dbo.Valve", "Record", "PLC", 2, default));
        Assert.Equal(59, await service.GetFreeScopedRecordValueAsync("dbo.Motor", "Record", "PLC", 1, default, "Class", 10));

        var vm = new TableViewModel(db.ConnectionString);
        await vm.SelectTableAsync("dbo.Valve");
        var page = await vm.LoadPageAsync();
        var original = page.DefaultView.Cast<DataRowView>().First(row => (int)row["PLC"] == 1);
        vm.SetCurrentRow(original);
        var duplicate = await vm.DuplicateCurrentRowAsync();

        Assert.Equal(60, duplicate["Record"]);
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valve WHERE PLC = 1 AND Record = N'60'"));

        await vm.SelectTableAsync("dbo.Motor");
        var motors = await vm.LoadPageAsync();
        vm.SetCurrentRow(motors.DefaultView.Cast<DataRowView>().First(row => (int)row["PLC"] == 1 && (int)row["Class"] == 10));
        Assert.Equal(59, (await vm.DuplicateCurrentRowAsync())["Record"]);
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Motor WHERE PLC = 1 AND Class = 10 AND Record = 59"));
    }
}
