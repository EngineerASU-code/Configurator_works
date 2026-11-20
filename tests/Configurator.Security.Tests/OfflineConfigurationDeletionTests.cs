#nullable enable
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineConfigurationDeletionTests
{
    [Fact]
    public void SnapshotDependencyValuesConvertWithoutReenteringJsonConversion()
    {
        Assert.True(ProjectModeView.TryConvertSnapshotValue(JsonValue.Create(42), "int", out var plc));
        Assert.Equal(42, Assert.IsType<int>(plc));
        Assert.True(ProjectModeView.TryConvertSnapshotValue(JsonValue.Create("Цех"), "nvarchar", out var area));
        Assert.Equal("Цех", area);
        Assert.True(ProjectModeView.TryConvertSnapshotValue(null, "int", out var empty));
        Assert.Same(System.DBNull.Value, empty);
    }

    [Fact]
    public async Task FailedReplacementAndDeleteRestoresRecordsKeysAndValues()
    {
        var original = new JsonObject { ["Id"] = 1, ["Name"] = "Before" };
        var child = new ProjectRecord { TableName = "dbo.Items", Key = "legacy-child", Values = original };
        var target = new ProjectRecord { TableName = "dbo.Items", Key = "legacy-target", Values = new JsonObject { ["Id"] = 2, ["Name"] = "Target" } };
        var project = new ConfiguratorProject
        {
            TableSchemas =
            [
                new ProjectTableSchema
                {
                    TableName = "dbo.Items", KeyColumns = ["Id"], HasStableKey = true,
                    Columns = [new() { Name = "Id", DataType = "int" }, new() { Name = "Name", DataType = "nvarchar" }]
                }
            ],
            LocalRecords = [child, target]
        };
        var source = new OfflineProjectDataSource(project);
        int changes = 0;
        source.ProjectChanged += (_, _) => changes++;

        await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            source.ExecuteMutationTransactionAsync(async () =>
            {
                await source.UpdateRecordAsync("dbo.Items", ["Id"], new Dictionary<string, object?> { ["Id"] = 1 },
                    new Dictionary<string, object?> { ["Id"] = 3, ["Name"] = "Changed" });
                Assert.True(source.DeleteSnapshotRecord("dbo.Items", "legacy-target"));
                throw new System.InvalidOperationException("Rollback");
            }));

        Assert.Equal(0, changes);
        Assert.Collection(source.GetRecords("dbo.Items"), row => Assert.Same(child, row), row => Assert.Same(target, row));
        Assert.Equal("legacy-child", child.Key);
        Assert.Same(original, child.Values);
        Assert.Equal("Before", child.Values["Name"]!.GetValue<string>());
    }
}