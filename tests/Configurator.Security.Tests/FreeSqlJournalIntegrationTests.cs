using Configurator;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class FreeSqlJournalIntegrationTests
{
    [CompareLocalDbFact]
    public async Task FreeSqlCallbackCapturesGeneratedInsertAndUpdateSnapshotsForUndo()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.JournalItems (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, Name nvarchar(40) NOT NULL); ");
        var database = new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
        var service = new DatabaseService(db.ConnectionString);
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ConfiguratorFreeSqlJournalTests", Guid.NewGuid().ToString("N")));
        IReadOnlyList<ChangeJournalRowChange> inserted = null;
        var insert = await service.ExecuteFreeSqlCoreAsync(
            "INSERT INTO dbo.JournalItems (Name) VALUES (N'created')", null, database, CancellationToken.None,
            _ => Task.FromResult(true), TimeSpan.FromSeconds(2), changes => inserted = changes);

        Assert.Equal(1, insert.AffectedRows);
        var insertChange = Assert.Single(inserted);
        Assert.Equal(ChangeJournalOperationKind.Insert, insertChange.Kind);
        Assert.Equal(1, Assert.Single(insertChange.IdentityAfter).Value.ToObject());
        Assert.Equal("created", insertChange.After["Name"].ToObject());
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.JournalItems WHERE Id=1 AND Name=N'created'"));

        IReadOnlyList<ChangeJournalRowChange> updated = null;
        var update = await service.ExecuteFreeSqlCoreAsync(
            "UPDATE dbo.JournalItems SET Name=N'changed' WHERE Id=1", null, database, CancellationToken.None,
            _ => Task.FromResult(true), TimeSpan.FromSeconds(2), changes => updated = changes);

        Assert.Equal(1, update.AffectedRows);
        var updateChange = Assert.Single(updated);
        Assert.Equal(ChangeJournalOperationKind.Update, updateChange.Kind);
        Assert.Equal("created", updateChange.Before["Name"].ToObject());
        Assert.Equal("changed", updateChange.After["Name"].ToObject());
        Assert.Equal(1, Assert.Single(updateChange.IdentityBefore).Value.ToObject());
        await journal.RestoreAsync(new ChangeJournalEntry { Changes = updated.ToList() }, db.ConnectionString);
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.JournalItems WHERE Id=1 AND Name=N'created'"));

        await journal.RestoreAsync(new ChangeJournalEntry { Changes = inserted.ToList() }, db.ConnectionString);
        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.JournalItems WHERE Id=1"));
    }

    [CompareLocalDbFact]
    public async Task FreeSqlDeclinedMutationDoesNotCommitOrPublishJournalSnapshot()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.JournalItems (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, Name nvarchar(40) NOT NULL);");
        var database = new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
        var service = new DatabaseService(db.ConnectionString);
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ConfiguratorFreeSqlJournalTests", Guid.NewGuid().ToString("N")));
        var callbackCount = 0;

        await Assert.ThrowsAsync<FreeSqlRolledBackException>(() => service.ExecuteFreeSqlCoreAsync(
            "INSERT INTO dbo.JournalItems (Name) VALUES (N'declined')", null, database, CancellationToken.None,
            _ => Task.FromResult(false), TimeSpan.FromSeconds(2), _ => callbackCount++));

        Assert.Equal(0, callbackCount);
        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.JournalItems"));
    }

    [CompareLocalDbFact]
    public async Task FreeSqlUserOutputClauseStillCapturesTheGeneratedRowForJournal()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.JournalItems (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, Name nvarchar(40) NOT NULL);");
        var database = new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
        var service = new DatabaseService(db.ConnectionString);
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ConfiguratorFreeSqlJournalTests", Guid.NewGuid().ToString("N")));
        IReadOnlyList<ChangeJournalRowChange> captured = null;

        await service.ExecuteFreeSqlCoreAsync(
            "INSERT INTO dbo.JournalItems (Name) OUTPUT INSERTED.Id VALUES (N'with-output')", null, database, CancellationToken.None,
            _ => Task.FromResult(true), TimeSpan.FromSeconds(2), changes => captured = changes);

        var inserted = Assert.Single(captured);
        Assert.Equal(1, Assert.Single(inserted.IdentityAfter).Value.ToObject());
        Assert.Equal("with-output", inserted.After["Name"].ToObject());
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.JournalItems WHERE Id=1 AND Name=N'with-output'"));
    }

    [CompareLocalDbFact]
    public async Task InsertSnapshotsIncludeDefaultsAndComputedColumnsForUndo()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Logged (Id int IDENTITY PRIMARY KEY, Name nvarchar(40) NOT NULL,
                Active bit NOT NULL DEFAULT 1, NameSize AS LEN(Name), Revision rowversion);
            """);
        var service = new DatabaseService(db.ConnectionString);
        var row = await service.InsertRowAndGetSnapshotAsync("dbo.Logged", new() { ["Name"] = "pump" });
        Assert.True((bool)row["Active"]);
        Assert.Equal(4, row["NameSize"]);
        Assert.IsType<byte[]>(row["Revision"]);
        var change = new ChangeJournalRowChange { TableName = "dbo.Logged", Kind = ChangeJournalOperationKind.Insert,
            After = row.ToDictionary(x => x.Key, x => ChangeJournalValue.FromObject(x.Value)),
            IdentityAfter = new() { ["Id"] = ChangeJournalValue.FromObject(row["Id"]) } };
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await journal.RestoreAsync(new ChangeJournalEntry { Changes = new() { change } }, db.ConnectionString);
        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Logged"));
    }

    [CompareLocalDbFact]
    public async Task SavedQueryUndoRejectsConflictAndRestoresExactStoredText()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.__ConfiguratorSavedQueries (Id int IDENTITY PRIMARY KEY, Name nvarchar(128) UNIQUE NOT NULL, SqlText nvarchar(max) NOT NULL)");
        var name = new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
        var store = new Configurator.Infrastructure.Database.SavedSqlQueryStore(db.ConnectionString, name);
        var query = await store.SaveAsync(null, "Q", "SELECT 1");
        await store.SaveAsync(query.Id, "Q2", "SELECT 2", "Q", "SELECT 1");
        var revision = new Configurator.Infrastructure.Database.SavedSqlQueryStore.Revision(query.Id, "Q2", "SELECT 2", "Q", "SELECT 1");
        var entry = new ChangeJournalEntry { ProjectType = "Online", Database = name, OperationKind = ChangeJournalOperationKind.Update,
            RestoreKind = Configurator.Infrastructure.Database.SavedSqlQueryStore.JournalKind,
            RestorePayloadJson = System.Text.Json.JsonSerializer.Serialize(revision) };
        Assert.True(entry.CanRestore);
        await store.RestoreAsync(entry, default);
        Assert.Equal("SELECT 1", (await store.ReadAsync(query.Id)).Sql);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreAsync(entry, default));
    }

    [CompareLocalDbFact]
    public async Task CascadeDeleteIsLoggedWithoutOfferingPartialUndo()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.ParentRows(Id int PRIMARY KEY);
            CREATE TABLE dbo.ChildRows(Id int PRIMARY KEY, ParentId int REFERENCES dbo.ParentRows(Id) ON DELETE CASCADE);
            INSERT dbo.ParentRows VALUES (1); INSERT dbo.ChildRows VALUES(2,1);
            """);
        var database = new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
        IReadOnlyList<ChangeJournalRowChange> captured = null;
        await new DatabaseService(db.ConnectionString).ExecuteFreeSqlCoreAsync("DELETE FROM dbo.ParentRows WHERE Id=1",
            null, database, default, _ => Task.FromResult(true), TimeSpan.FromSeconds(2), changes => captured = changes);
        Assert.False(string.IsNullOrEmpty(Assert.Single(captured).CaptureWarning));
        Assert.False(new ChangeJournalEntry { Changes = captured.ToList() }.CanRestore);
        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.ChildRows"));
    }
}
