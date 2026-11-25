using Xunit;

namespace Configurator.Security.Tests;

public sealed class ChangeJournalLatestAndRollbackTests
{
    [Fact]
    public void OnlineLatestRestorableEntryIncludesOperationsFromOtherModes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ConfiguratorJournalTests", Guid.NewGuid().ToString("N"));
        var journal = new ChangeJournalService(root);
        journal.BindOnlineProject("server-a", "database-a", "database-a", 1);
        Assert.True(journal.Record(RestorableUpdate("older-mode-1", 1, 1, "old", "after-old")));

        journal.BindOnlineProject("server-a", "database-a", "database-a", 3);
        Assert.True(journal.Record(RestorableUpdate("newer-mode-3", 3, 2, "before", "after")));
        journal.BindOnlineProject("server-a", "database-a", "database-a", 1);

        var latest = journal.LoadRecent().First(entry => entry.CanRestore);

        Assert.Equal("newer-mode-3", latest.Description);
        Assert.Equal(3, latest.Mode);
    }

    [CompareLocalDbFact]
    public async Task SqlRestoreRollsBackEarlierRowsWhenALaterRestoreConflicts()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.JournalRows (Id int NOT NULL PRIMARY KEY, Value nvarchar(40) NOT NULL); INSERT dbo.JournalRows VALUES (1,N'after-one'),(2,N'conflict');");
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ConfiguratorJournalTests", Guid.NewGuid().ToString("N")));
        var entry = new ChangeJournalEntry
        {
            Changes = new()
            {
                Update(2, "before-two", "expected-two"),
                Update(1, "before-one", "after-one")
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RestoreAsync(entry, db.ConnectionString));

        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.JournalRows WHERE Id=1 AND Value=N'after-one'"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.JournalRows WHERE Id=2 AND Value=N'conflict'"));
    }

    [CompareLocalDbFact]
    public async Task ProgramModeInsertionJournalsAndRestoresTheGeneratedRecordKey()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.Prog (Record int NOT NULL, PLC int NOT NULL, PLC_Class_Number int NOT NULL, Name_L1 nvarchar(40) NOT NULL, CONSTRAINT UQ_Prog UNIQUE (Record, PLC, PLC_Class_Number)); INSERT dbo.Prog VALUES (3,1,10,N'Existing'),(20,2,10,N'Other scope');");
        var repository = new ProgramManagementRepository(db.ConnectionString);
        var generatedRecord = await repository.AddProgramAsync(1, 10, new Dictionary<string, object> { ["Name_L1"] = "Created" });
        var inserted = new Dictionary<string, object>
        {
            ["Record"] = generatedRecord,
            ["PLC"] = 1,
            ["PLC_Class_Number"] = 10,
            ["Name_L1"] = "Created"
        };
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ConfiguratorJournalTests", Guid.NewGuid().ToString("N")));
        var entry = new ChangeJournalEntry
        {
            Changes = new() { new ChangeJournalRowChange
            {
                TableName = ProgramManagementRepository.ProgramTable,
                Kind = ChangeJournalOperationKind.Insert,
                IdentityAfter = Values(inserted, "Record", "PLC", "PLC_Class_Number"),
                After = Values(inserted, "Record", "PLC", "PLC_Class_Number", "Name_L1")
            } }
        };

        journal.BindOnlineProject("server-a", "database-a", "database-a", 3);
        Assert.True(journal.Record(entry));
        entry = Assert.Single(journal.LoadRecent());

        Assert.Equal(4, generatedRecord);
        Assert.True(entry.CanRestore);
        await journal.RestoreAsync(entry, db.ConnectionString);

        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog WHERE Record=4 AND PLC=1 AND PLC_Class_Number=10"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog WHERE Record=20 AND PLC=2 AND PLC_Class_Number=10"));
    }

    private static ChangeJournalEntry RestorableUpdate(string description, int mode, int id, string oldValue, string newValue)
        => new()
        {
            Description = description,
            Mode = mode,
            Changes = new() { Update(id, oldValue, newValue) }
        };

    private static ChangeJournalRowChange Update(int id, string before, string after)
        => new()
        {
            TableName = "dbo.JournalRows",
            Kind = ChangeJournalOperationKind.Update,
            IdentityBefore = new() { ["Id"] = ChangeJournalValue.FromObject(id) },
            IdentityAfter = new() { ["Id"] = ChangeJournalValue.FromObject(id) },
            Before = new() { ["Id"] = ChangeJournalValue.FromObject(id), ["Value"] = ChangeJournalValue.FromObject(before) },
            After = new() { ["Id"] = ChangeJournalValue.FromObject(id), ["Value"] = ChangeJournalValue.FromObject(after) }
        };

    private static Dictionary<string, ChangeJournalValue> Values(IReadOnlyDictionary<string, object> row, params string[] columns)
        => columns.ToDictionary(column => column, column => ChangeJournalValue.FromObject(row[column]), StringComparer.OrdinalIgnoreCase);
}


