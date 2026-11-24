using System.Text.Json.Nodes;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineProjectDataSourceTests
{
    [Fact]
    public void UpsertCreatesThenUpdatesWithoutCreatingDuplicateKey()
    {
        var project = CreateProject();
        var source = new OfflineProjectDataSource(project);

        source.UpsertRecord(Record("dbo.Items", "1", ("Name", "first")));
        var firstModified = project.Manifest.ModifiedUtc;
        source.UpsertRecord(Record("dbo.Items", "1", ("Name", "updated")));

        var records = source.GetRecords("Items");
        var record = Assert.Single(records);
        Assert.Equal("updated", record.Values["Name"]!.GetValue<string>());
        Assert.True(project.Manifest.ModifiedUtc >= firstModified);
    }

    [Fact]
    public void DeleteIsIdempotentAndOnlyDeletesMatchingTableAndKey()
    {
        var project = CreateProject();
        var source = new OfflineProjectDataSource(project);
        source.UpsertRecord(Record("dbo.Items", "1", ("Name", "one")));
        source.UpsertRecord(Record("dbo.Items", "2", ("Name", "two")));
        source.UpsertRecord(Record("dbo.Other", "1", ("Name", "other")));

        Assert.True(source.DeleteRecord("dbo.Items", "1"));
        Assert.False(source.DeleteRecord("dbo.Items", "1"));
        Assert.Single(source.GetRecords("dbo.Items"));
        Assert.Single(source.GetRecords("dbo.Other"));
    }

    [Fact]
    public void DeleteSnapshotRecordDeletesExactlyThePersistedInternalIdentityWithoutSqlKeyColumns()
    {
        var project = CreateProject();
        project.TableSchemas.Add(new ProjectTableSchema { TableName = "dbo.PLC", HasStableKey = false, KeyColumns = new(), Columns = new() { new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" } } });
        project.LocalRecords.Add(Record("dbo.PLC", "snapshot:0", ("Name", "duplicate label")));
        project.LocalRecords.Add(Record("dbo.PLC", "snapshot:1", ("Name", "duplicate label")));
        var source = new OfflineProjectDataSource(project);

        Assert.True(source.DeleteSnapshotRecord("dbo.PLC", "snapshot:1"));
        var remaining = Assert.Single(source.GetRecords("dbo.PLC"));
        Assert.Equal("snapshot:0", remaining.Key);
        Assert.Equal("duplicate label", remaining.Values["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteSnapshotRecordPersistsAcrossProjectReopen()
    {
        var project = CreateProject();
        project.TableSchemas.Add(new ProjectTableSchema { TableName = "dbo.PLC", HasStableKey = false, Columns = { new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" } } });
        project.LocalRecords.Add(Record("dbo.PLC", "snapshot:0", ("Name", "first")));
        project.LocalRecords.Add(Record("dbo.PLC", "snapshot:1", ("Name", "second")));
        var source = new OfflineProjectDataSource(project);
        Assert.True(source.DeleteSnapshotRecord("dbo.PLC", "snapshot:1"));

        var path = Path.Combine(Path.GetTempPath(), $"cfgproj-delete-{Guid.NewGuid():N}.cfgproj");
        try
        {
            var package = new ProjectPackageService();
            await package.SaveAsync(path, project);
            var reopened = await package.OpenAsync(path);
            var remaining = Assert.Single(new OfflineProjectDataSource(reopened).GetRecords("dbo.PLC"));
            Assert.Equal("snapshot:0", remaining.Key);
            Assert.Equal("first", remaining.Values["Name"]!.GetValue<string>());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void DeleteSnapshotRecordRejectsAmbiguousInternalIdentityWithoutMutatingProject()
    {
        var project = CreateProject();
        project.LocalRecords.Add(Record("dbo.PLC", "snapshot:0", ("Name", "first")));
        project.LocalRecords.Add(Record("dbo.PLC", "snapshot:0", ("Name", "second")));
        var source = new OfflineProjectDataSource(project);
        var before = project.Manifest.ModifiedUtc;

        Assert.Throws<InvalidOperationException>(() => source.DeleteSnapshotRecord("dbo.PLC", "snapshot:0"));
        Assert.Equal(2, project.LocalRecords.Count);
        Assert.Equal(before, project.Manifest.ModifiedUtc);
    }

    [Theory]
    [InlineData("", "1")]
    [InlineData("dbo.Items", "")]
    [InlineData("   ", "   ")]
    public void UpsertRejectsMissingTableOrKeyWithoutMutatingProject(string table, string key)
    {
        var project = CreateProject();
        var source = new OfflineProjectDataSource(project);
        var before = project.Manifest.ModifiedUtc;

        Assert.Throws<ArgumentException>(() => source.UpsertRecord(Record(table, key, ("Name", "x"))));
        Assert.Empty(project.LocalRecords);
        Assert.Equal(before, project.Manifest.ModifiedUtc);
    }

    [Fact]
    public void UpsertRejectsNullAndSchemaUpsertRejectsNullOrMissingName()
    {
        var source = new OfflineProjectDataSource(CreateProject());
        Assert.Throws<ArgumentNullException>(() => source.UpsertRecord(null!));
        Assert.Throws<ArgumentNullException>(() => source.UpsertSchema(null!));
        Assert.Throws<ArgumentException>(() => source.UpsertSchema(new ProjectTableSchema { TableName = " " }));
    }

    [Fact]
    public void CannotReplaceExistingSchemaInOpenedProject()
    {
        var project = CreateProject();
        project.TableSchemas.Add(new ProjectTableSchema { TableName = "dbo.Items", KeyColumns = { "Id" } });
        var source = new OfflineProjectDataSource(project);
        Assert.Throws<InvalidOperationException>(() => source.UpsertSchema(new ProjectTableSchema { TableName = "DBO.ITEMS" }));
        Assert.Equal("Id", Assert.Single(Assert.Single(project.TableSchemas).KeyColumns));
    }

    [Fact]
    public void AmbiguousLeafNameDoesNotMixRecordsOrSchemasAcrossSchemas()
    {
        var project = CreateProject();
        project.TableSchemas.Add(new ProjectTableSchema { TableName = "dbo.Items" });
        project.TableSchemas.Add(new ProjectTableSchema { TableName = "archive.Items" });
        project.LocalRecords.Add(Record("dbo.Items", "1", ("Name", "live")));
        project.LocalRecords.Add(Record("archive.Items", "1", ("Name", "archive")));
        var source = new OfflineProjectDataSource(project);

        Assert.Null(source.GetSchema("Items"));
        Assert.Empty(source.GetRecords("Items"));
        Assert.Equal("dbo.Items", source.ResolveTableName("dbo.Items"));
    }

    [Fact]
    public void TablesAreDeduplicatedCaseInsensitivelyAndIncludeMappedAndRecordedTables()
    {
        var project = CreateProject();
        project.TableSchemas.Add(new ProjectTableSchema { TableName = "dbo.Items" });
        project.Mappings.Add(new ProjectMapping { TableName = "DBO.ITEMS" });
        project.LocalRecords.Add(Record("dbo.Other", "1", ("Name", "x")));
        var source = new OfflineProjectDataSource(project);

        Assert.Equal(2, source.GetTables().Count);
        Assert.Contains(source.GetTables(), x => x.TableName.Equals("dbo.Items", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(source.GetTables(), x => x.TableName == "dbo.Other");
    }

    [Fact]
    public void OfflineSourceMutationsStayWithinTheProjectAndNeverPretendToBeReadOnly()
    {
        var project = CreateProject();
        var source = new OfflineProjectDataSource(project);
        Assert.False(source.IsReadOnly);
        source.UpsertRecord(Record("dbo.Items", "1", ("Name", "local-only")));
        Assert.Single(project.LocalRecords);
        Assert.Equal("local-only", project.LocalRecords[0].Values["Name"]!.GetValue<string>());
        Assert.Empty(project.BaselineRecords);
    }

    private static ConfiguratorProject CreateProject() => new()
    {
        Manifest = new ConfiguratorProjectManifest { Name = "Offline tests" },
        Settings = new ProjectSettings(),
        Mappings = new(), Architecture = new JsonObject(), GraphRelations = new JsonObject(), Filters = new JsonObject(),
        TextLists = new(), LocalRecords = new(), BaselineRecords = new(), TableSchemas = new()
    };

    private static ProjectRecord Record(string table, string key, params (string Name, string Value)[] values)
    {
        var json = new JsonObject();
        foreach (var value in values) json[value.Name] = JsonValue.Create(value.Value);
        return new ProjectRecord { TableName = table, Key = key, Values = json };
    }
}
