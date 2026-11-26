using Configurator;
using Configurator.Core.Architecture;
using Configurator.Infrastructure.Database;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class DeletionConcurrencyTests
{
    [CompareLocalDbFact]
    public async Task StalePlanRollsBackEntireDeletion()
    {
        foreach (var mutation in new[] { "target", "dependency", "new_dependency", "recreate", "replacement_deleted", "replacement_changed" })
            await VerifyStalePlanAsync(mutation);
    }

    private static async Task VerifyStalePlanAsync(string mutation)
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.ParentRows (Id int NOT NULL PRIMARY KEY, Label nvarchar(50) NOT NULL);
            CREATE TABLE dbo.ChildRows (Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL, Label nvarchar(50) NOT NULL);
            INSERT dbo.ParentRows VALUES (1,N'old'),(2,N'replacement');
            INSERT dbo.ChildRows VALUES (10,1,N'child');
            """);
        var schema = await new DatabaseArchitectureAnalyzer(db.ConnectionString).AnalyzeAsync();
        var relation = new DatabaseRelation { SourceTable="dbo.ChildRows", TargetTable="dbo.ParentRows", Confidence=RelationConfidence.High };
        relation.SourceColumns.Add("ParentId"); relation.TargetColumns.Add("Id"); schema.Relations.Add(relation);
        var guard = new DeletionProtectionService(db.ConnectionString, schema);
        var plan = await guard.BuildPlanAsync(new DeletionRequest { TableName="dbo.ParentRows", Key=new Dictionary<string,object> { ["Id"]=1 }, ExpectedValues=new Dictionary<string,object> { ["Id"]=1, ["Label"]="old" } });
        var hit = Assert.Single(plan.Dependencies);
        var replacement = new ReferenceReplacement { SourceTable=hit.SourceTable, SourceColumns=hit.Relation.SourceColumns, NewValues=new object[] { 2 }, TargetValues=new Dictionary<string,object> { ["Id"]=2, ["Label"]="replacement" }, KeyColumns=hit.SourceKey.Keys.ToList(), KeyValues=hit.SourceKey.Values.ToList(), ExpectedOldValues=hit.SourceReferenceValues };
        await db.ExecuteAsync(mutation switch {
            "target" => "UPDATE dbo.ParentRows SET Label=N'changed' WHERE Id=1",
            "dependency" => "UPDATE dbo.ChildRows SET Label=N'changed' WHERE Id=10",
            "new_dependency" => "INSERT dbo.ChildRows VALUES (11,1,N'new')",
            "replacement_deleted" => "DELETE dbo.ParentRows WHERE Id=2",
            "replacement_changed" => "UPDATE dbo.ParentRows SET Label=N'changed' WHERE Id=2",
            _ => "DELETE dbo.ParentRows WHERE Id=1; INSERT dbo.ParentRows VALUES (1,N'new instance')"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.ExecuteAsync(plan, new[] { replacement }, (_,_,_) => Task.FromResult(true)));
        Assert.Equal(1, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.ParentRows WHERE Id=1"));
        Assert.Equal(1, await db.DecimalAsync("SELECT ParentId FROM dbo.ChildRows WHERE Id=10"));
    }

    [CompareLocalDbFact]
    public async Task GenericDeleteRequiresFullSnapshotAndBlocksDependents()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.ParentRows (Id int NOT NULL PRIMARY KEY, Label nvarchar(50) NOT NULL);
            CREATE TABLE dbo.ChildRows (Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL,
              CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.ParentRows(Id));
            INSERT dbo.ParentRows VALUES (1,N'old'),(2,N'free');
            INSERT dbo.ChildRows VALUES (10,1);
            """);
        var service = new DatabaseService(db.ConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteRowsAsync("dbo.ParentRows", new() { "Id" }, new() { new() { ["Id"]=1 } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteRowsByValuesAsync("dbo.ParentRows", new() { new() { ["Id"]=1, ["Label"]="old" } }));
        Assert.Equal(1, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.ParentRows WHERE Id=1"));
        Assert.Equal(1, await service.DeleteRowsAsync("dbo.ParentRows", new() { "Id" }, new() { new() { ["Id"]=2, ["Label"]="free" } }));
    }

    [CompareLocalDbFact]
    public async Task UndoInsertRejectsNewDependentAndIncompleteSnapshot()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.ParentRows (Id int NOT NULL PRIMARY KEY, Label nvarchar(50) NOT NULL);
            CREATE TABLE dbo.ChildRows (Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL,
              CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.ParentRows(Id));
            INSERT dbo.ParentRows VALUES (1,N'old'),(2,N'free'); INSERT dbo.ChildRows VALUES (10,1);
            """);
        ChangeJournalEntry Entry(int id, bool full) => new() { Changes = new() { new ChangeJournalRowChange {
            TableName="dbo.ParentRows", Kind=ChangeJournalOperationKind.Insert,
            IdentityAfter=new() { ["Id"]=ChangeJournalValue.FromObject(id) },
            After=full ? new() { ["Id"]=ChangeJournalValue.FromObject(id), ["Label"]=ChangeJournalValue.FromObject(id == 1 ? "old" : "free") }
                       : new() { ["Id"]=ChangeJournalValue.FromObject(id) }
        } } };
        var journal = new ChangeJournalService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RestoreAsync(Entry(1, true), db.ConnectionString));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RestoreAsync(Entry(2, false), db.ConnectionString));
        Assert.Equal(2, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.ParentRows"));
        await journal.RestoreAsync(Entry(2, true), db.ConnectionString);
        Assert.Equal(1, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.ParentRows"));
    }

    [CompareLocalDbFact]
    public async Task UndoAreaCreationAllowsSelfReferenceButProtectsOtherRows()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Areas (Id int PRIMARY KEY, AreaName nvarchar(40), Area nvarchar(40));
            INSERT dbo.Areas VALUES (1,N'Zone1',N'Zone1'),(2,N'Zone2',N'Zone1');
            """);
        var entry = new ChangeJournalEntry { Changes = new() { new ChangeJournalRowChange {
            TableName = "dbo.Areas", Kind = ChangeJournalOperationKind.Insert,
            IdentityAfter = new() { ["Id"] = ChangeJournalValue.FromObject(1) },
            After = new() { ["Id"] = ChangeJournalValue.FromObject(1), ["AreaName"] = ChangeJournalValue.FromObject("Zone1"),
                ["Area"] = ChangeJournalValue.FromObject("Zone1") }
        } } };
        var journal = new ChangeJournalService();
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RestoreAsync(entry, db.ConnectionString));
        Assert.Contains("dbo.Areas", conflict.Message);
        Assert.Equal(2, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Areas"));
        await db.ExecuteAsync("UPDATE dbo.Areas SET Area=NULL WHERE Id=2");
        await journal.RestoreAsync(entry, db.ConnectionString);
        Assert.Equal(0, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Areas WHERE Id=1"));
        Assert.Equal(1, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Areas WHERE Id=2"));
    }

    [CompareLocalDbFact]
    public async Task FreeSqlTimeoutRollsBackAndReleasesLockForSecondConnection()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.TimeoutRows (Id int NOT NULL PRIMARY KEY, Value int NOT NULL); INSERT dbo.TimeoutRows VALUES (1,10)");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DatabaseService(db.ConnectionString);
        var database = new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
        Task<(System.Data.DataTable Result, int AffectedRows)> operation;
        var callerContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new UnresponsiveDispatcher());
            operation = service.ExecuteFreeSqlCoreAsync("UPDATE dbo.TimeoutRows SET Value=11 WHERE Id=1", null, database,
                CancellationToken.None, _ => { entered.SetResult(); return response.Task; }, TimeSpan.FromSeconds(2));
        }
        finally { SynchronizationContext.SetSynchronizationContext(callerContext); }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var other = new SqlConnection(db.ConnectionString);
        await other.OpenAsync();
        await using var read = new SqlCommand("SELECT Value FROM dbo.TimeoutRows WHERE Id=1", other) { CommandTimeout = 5 };
        var blockedRead = read.ExecuteScalarAsync();
        await Task.Delay(50);
        Assert.False(blockedRead.IsCompleted);
        await Assert.ThrowsAsync<FreeSqlRolledBackException>(() => operation);
        Assert.Equal(10, Convert.ToInt32(await blockedRead));
        response.TrySetResult(true);
        Assert.Equal(10, await db.DecimalAsync("SELECT Value FROM dbo.TimeoutRows WHERE Id=1"));
    }

    [CompareLocalDbFact]
    public async Task SavedQueryConcurrentEditIsRejectedAndMissingTableIsNotCreated()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        var database = new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
        var first = new SavedSqlQueryStore(db.ConnectionString, database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.SaveAsync(null, "one", "SELECT 1"));
        Assert.Equal(0, await db.DecimalAsync("SELECT COUNT(*) FROM sys.tables WHERE name='__ConfiguratorSavedQueries'"));
        await db.ExecuteAsync("CREATE TABLE dbo.__ConfiguratorSavedQueries (Id int IDENTITY PRIMARY KEY, Name nvarchar(128) NOT NULL UNIQUE, SqlText nvarchar(max) NOT NULL)");
        var created = await first.SaveAsync(null, "one", "SELECT 1");
        var opened1 = await first.ReadAsync(created.Id);
        var second = new SavedSqlQueryStore(db.ConnectionString, database);
        var opened2 = await second.ReadAsync(created.Id);
        await second.SaveAsync(created.Id, "one", "SELECT 2", opened2.Query.Name, opened2.Sql);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.SaveAsync(created.Id, "one", "SELECT 3", opened1.Query.Name, opened1.Sql));
        Assert.Equal("SELECT 2", (await first.ReadAsync(created.Id)).Sql);
    }
    private sealed class UnresponsiveDispatcher : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object state) { }
    }

}

