using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Configurator.Security.Tests;

public class TableSelectionRaceTests
{
    [Fact]
    public async Task LateMetadataCannotOverwriteNewerSelection()
    {
        var firstColumns = new TaskCompletionSource<List<ColumnInfo>>();
        var firstKeys = new TaskCompletionSource<List<string>>();
        var vm = new TableViewModel(
            (table, _) => table == "dbo.First" ? firstColumns.Task : Task.FromResult(new List<ColumnInfo> { new() { Name = "SecondId", DataType = "int" } }),
            (table, _) => table == "dbo.First" ? firstKeys.Task : Task.FromResult(new List<string> { "SecondId" }));

        var first = vm.SelectTableAsync("dbo.First");
        await vm.SelectTableAsync("dbo.Second");
        firstColumns.SetResult(new List<ColumnInfo> { new() { Name = "FirstId", DataType = "int" } });
        firstKeys.SetResult(new List<string> { "FirstId" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("dbo.Second", vm.CurrentTable);
        Assert.Equal("SecondId", vm.KeyColumn);
        Assert.Equal("SecondId", Assert.Single(vm.Columns).Name);
    }
}
