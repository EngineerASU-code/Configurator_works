using Configurator.Core.Projects;
using Configurator.Infrastructure.Database;
using Xunit;

namespace Configurator.Security.Tests;

public class ProjectWriteGuardTests
{
    [Theory]
    [InlineData("Classes")]
    [InlineData("[dbo].[cLaSsEs]")]
    [InlineData("dbo.__ConfiguratorSavedQueries")]
    public async Task ProtectedTablesAreReadOnlyBeforeAnySqlConnection(string table)
    {
        var service = new DatabaseService("unused");
        Assert.False(ProjectWriteGuard.IsDataTableWritable(table));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClearTableAsync(table));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InsertRowAsync(table, new() { ["Record"] = 9 }));
        var project = new ConfiguratorProject();
        var offline = new OfflineProjectDataSource(project);
        Assert.Throws<InvalidOperationException>(() => offline.UpsertRecord(new ProjectRecord { TableName = table, Key = "9" }));
        Assert.Empty(project.LocalRecords);
    }

    [Fact]
    public async Task SchemaAndClassApiRejectChangesBeforeConnecting()
    {
        var service = new DatabaseService("unused");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DropTableAsync("dbo.Items"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateTemplateTableAsync("dbo.Items", new()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncTemplateColumnsAsync("dbo.Items", new()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameTableAsync("dbo.Items", "dbo.Other"));
        var registry = new ClassRegistryService("unused");
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.UpsertAsync(new()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RemoveAsync(new()));
    }

    [Fact]
    public void RegistryCanBeReadButCannotBeEditedInFreeSql()
    {
        FreeSqlPolicy.Validate("SELECT Record FROM dbo.Classes");
        Assert.Throws<InvalidOperationException>(() => FreeSqlPolicy.Validate("UPDATE dbo.Classes SET Name = @name WHERE Record = @id"));
    }
}
