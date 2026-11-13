using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ChangeJournalProjectScopeTests
{
    [Fact]
    public void OfflineJournal_IsEmbeddedInProjectAndSurvivesServiceRebind()
    {
        var project = new ConfiguratorProject();
        project.Manifest.ProjectId = "project-a";
        project.Manifest.Name = "Локальный проект A";
        var root = Path.Combine(Path.GetTempPath(), "ConfiguratorJournalTests", Guid.NewGuid().ToString("N"));
        var service = new ChangeJournalService(root);
        var dirty = false;
        service.BindOfflineProject(project, () => dirty = true, 3);

        var saved = service.Record(new ChangeJournalEntry
        {
            Description = "Изменён объект",
            Category = "Объект",
            EntityType = "Объект",
            EntityIdentity = "Record=12",
            Changes = new List<ChangeJournalRowChange>()
        });

        Assert.True(saved);
        Assert.True(dirty);
        Assert.NotNull(project.Architecture["changeJournal"]);
        var loaded = service.LoadRecent();
        var entry = Assert.Single(loaded);
        Assert.Equal("Offline", entry.ProjectType);
        Assert.Equal("project-a", entry.ProjectId);
        Assert.Equal("Локальный проект A", entry.ProjectName);
        Assert.Equal(3, entry.Mode);
    }

    [Fact]
    public void OnlineJournal_IsScopedByServerAndDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ConfiguratorJournalTests", Guid.NewGuid().ToString("N"));
        var service = new ChangeJournalService(root);
        service.BindOnlineProject("server-a", "database-a", "database-a", 1);
        Assert.True(service.Record(new ChangeJournalEntry { Description = "A" }));
        Assert.Single(service.LoadRecent());

        service.BindOnlineProject("server-b", "database-b", "database-b", 1);
        Assert.Empty(service.LoadRecent());
        Assert.True(service.Record(new ChangeJournalEntry { Description = "B" }));
        Assert.Equal("B", Assert.Single(service.LoadRecent()).Description);
    }
    [Fact]
    public void OfflineRestore_UpdateRevertsOnlyChangedFields_WhenNoConflict()
    {
        var project = NewProjectWithRow("Record=7", 7, "after", "untouched");
        var service = new ChangeJournalService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        service.BindOfflineProject(project, () => { }, 1);
        var entry = OfflineEntry(project, new ChangeJournalRowChange
        {
            TableName = "dbo.Objects", Kind = ChangeJournalOperationKind.Update,
            IdentityBefore = Values(("Record", 7)), IdentityAfter = Values(("Record", 7)),
            Before = Values(("Record", 7), ("Name", "before"), ("Note", "untouched")),
            After = Values(("Record", 7), ("Name", "after"), ("Note", "untouched"))
        });

        service.RestoreOffline(entry, project, () => { });

        Assert.Equal("before", project.LocalRecords.Single().Values["Name"]?.ToString());
        Assert.Equal("untouched", project.LocalRecords.Single().Values["Note"]?.ToString());
    }

    [Fact]
    public void OfflineRestore_RejectsConflict_WithoutChangingProject()
    {
        var project = NewProjectWithRow("Record=7", 7, "changed-again", "untouched");
        var service = new ChangeJournalService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        service.BindOfflineProject(project, () => { }, 1);
        var entry = OfflineEntry(project, new ChangeJournalRowChange
        {
            TableName = "dbo.Objects", Kind = ChangeJournalOperationKind.Update,
            IdentityBefore = Values(("Record", 7)), IdentityAfter = Values(("Record", 7)),
            Before = Values(("Record", 7), ("Name", "before"), ("Note", "untouched")),
            After = Values(("Record", 7), ("Name", "after"), ("Note", "untouched"))
        });

        var error = Assert.Throws<InvalidOperationException>(() => service.RestoreOffline(entry, project, () => { }));

        Assert.Contains("Конфликт", error.Message);
        Assert.Equal("changed-again", project.LocalRecords.Single().Values["Name"]?.ToString());
    }

    [Fact]
    public void OfflineRestore_DeletedRowIsReinsertedUsingSnapshotKey()
    {
        var project = new ConfiguratorProject();
        project.Manifest.ProjectId = "restore-project";
        project.TableSchemas.Add(new ProjectTableSchema
        {
            TableName = "dbo.Objects",
            KeyColumns = new List<string> { "Record" },
            HasStableKey = true,
            Columns = new List<ProjectColumnSchema>
            {
                new() { Name = "Record", DataType = "int" },
                new() { Name = "Name", DataType = "nvarchar", IsNullable = true }
            }
        });
        var service = new ChangeJournalService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        service.BindOfflineProject(project, () => { }, 1);
        var entry = OfflineEntry(project, new ChangeJournalRowChange
        {
            TableName = "dbo.Objects", Kind = ChangeJournalOperationKind.Delete,
            IdentityBefore = Values(("Record", 9)),
            Before = Values(("Record", 9), ("Name", "removed"))
        });

        service.RestoreOffline(entry, project, () => { });

        var restored = Assert.Single(project.LocalRecords);
        Assert.Equal(
            ProjectKeyCodec.Encode(project.TableSchemas.Single(), new[] { "Record" }, name => restored.Values[name]),
            restored.Key);
        Assert.Equal("removed", restored.Values["Name"]?.ToString());
    }

    private static ConfiguratorProject NewProjectWithRow(string key, int record, string name, string note)
    {
        var project = new ConfiguratorProject();
        project.Manifest.ProjectId = "restore-project";
        project.TableSchemas.Add(new ProjectTableSchema
        {
            TableName = "dbo.Objects",
            KeyColumns = new List<string> { "Record" },
            HasStableKey = true,
            Columns = new List<ProjectColumnSchema>
            {
                new() { Name = "Record", DataType = "int" },
                new() { Name = "Name", DataType = "nvarchar", IsNullable = true },
                new() { Name = "Note", DataType = "nvarchar", IsNullable = true }
            }
        });
        var values = new System.Text.Json.Nodes.JsonObject { ["Record"] = record, ["Name"] = name, ["Note"] = note };
        project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.Objects", Key = key, Values = values });
        return project;
    }

    private static ChangeJournalEntry OfflineEntry(ConfiguratorProject project, ChangeJournalRowChange change)
        => new()
        {
            ProjectType = "Offline", ProjectId = project.Manifest.ProjectId, ProjectName = project.Manifest.Name,
            Mode = 1, Description = "Изменение", Result = "Успешно", Changes = new List<ChangeJournalRowChange> { change }
        };

    private static Dictionary<string, ChangeJournalValue> Values(params (string Key, object Value)[] pairs)
        => pairs.ToDictionary(x => x.Key, x => ChangeJournalValue.FromObject(x.Value), StringComparer.OrdinalIgnoreCase);

}
