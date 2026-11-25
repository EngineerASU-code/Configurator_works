using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProgramContextReplacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRejectsStalePreviewAndCollisionsThenRestoresBothSides(bool sequence)
    {
        string table = sequence ? ProgramManagementRepository.SequenceTable : ProgramManagementRepository.ProgramTable;
        string child = sequence ? ProgramManagementRepository.ParameterTable : "dbo.Prog_Msg";
        string reference = sequence ? "Seq_Record" : "Prog_Record";
        var project = new ConfiguratorProject();
        project.TableSchemas.Add(Schema(table, sequence
            ? new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record" }
            : new[] { "Record", "PLC", "PLC_Class_Number" }));
        project.TableSchemas.Add(Schema(child, sequence
            ? new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Step_Number", "Type" }
            : new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Type" }));

        ProjectRecord Root(int record, int plc = 1) => sequence
            ? Row(table, new("PLC", plc), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 1), new("Record", record), new("Value", 5))
            : Row(table, new("PLC", plc), new("PLC_Class_Number", 10), new("Record", record), new("Value", 5));
        ProjectRecord Child(int owner, int record) => sequence
            ? Row(child, new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 1), new("Seq_Record", owner), new("Record", record), new("Step_Number", 0), new("Type", 0), new("Value", 7))
            : Row(child, new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", owner), new("Record", record), new("Type", 0), new("Value", 7));

        foreach (var row in new[] { Root(1), Root(2), Root(3, plc: 2), Child(1, 1), Child(1, 2), Child(2, 2) })
            project.LocalRecords.Add(row);
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 1, SeqRecordValue = 1 };
        var roots = sequence ? await repository.GetSequencesAsync(context) : await repository.GetProgramsAsync(1, 10);
        var original = roots.Rows.Cast<DataRow>().Single(row => (int)row["Record"] == 1);
        var expected = roots.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => original[column]);
        Task<ProgramManagementRepository.ContextReplacementPreview> Preview() => repository.PrepareContextReplacementAsync(context, sequence ? 1 : null, expected);
        int CountChildren(int owner) => source.GetRecords(child).Count(row => row.Values[reference]!.GetValue<int>() == owner);

        var preview = await Preview();
        Assert.Equal(2, Assert.Single(preview.Candidates)["Record"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReplaceAndDeleteContextAsync(preview, 3));
        var target = source.GetRecords(table).Single(row => row.Values["Record"]!.GetValue<int>() == 2);
        target.Values["Value"] = 9;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReplaceAndDeleteContextAsync(preview, 2));
        Assert.Equal(2, CountChildren(1));

        preview = await Preview();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReplaceAndDeleteContextAsync(preview, 2));
        Assert.Equal(2, CountChildren(1));
        Assert.Equal(1, CountChildren(2));
        Assert.Equal(3, source.GetRecords(table).Count);

        var collision = source.GetRecords(child).Single(row => row.Values[reference]!.GetValue<int>() == 2);
        project.LocalRecords.Remove(collision);
        source.InvalidateIndex();
        var changes = await repository.ReplaceAndDeleteContextAsync(await Preview(), 2);
        Assert.Equal(new[] { ChangeJournalOperationKind.Update, ChangeJournalOperationKind.Update, ChangeJournalOperationKind.Delete },
            changes.Select(change => change.Kind));
        Assert.Equal(0, CountChildren(1));
        Assert.Equal(2, CountChildren(2));
        Assert.Equal(2, source.GetRecords(table).Count);

        var entry = new ChangeJournalEntry
        {
            ProjectType = "Offline",
            ProjectId = project.Manifest.ProjectId,
            OperationKind = ChangeJournalOperationKind.Composite,
            RestoreIncludesAllChanges = true,
            Changes = changes.ToList()
        };
        new ChangeJournalService(System.IO.Path.GetTempPath()).RestoreOffline(entry, project, source.InvalidateIndex);
        Assert.Equal(2, CountChildren(1));
        Assert.Equal(0, CountChildren(2));
        Assert.Equal(3, source.GetRecords(table).Count);
    }


    [CompareLocalDbFact]
    public async Task OnlineProgramReplacementMovesForeignKeyAndJournalRestoresIt()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Prog (
              Record int NOT NULL, PLC int NOT NULL, PLC_Class_Number int NOT NULL, Name_L1 nvarchar(40) NOT NULL,
              PRIMARY KEY (Record, PLC, PLC_Class_Number));
            CREATE TABLE dbo.Prog_Softkey (
              Record int NOT NULL, PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL,
              PRIMARY KEY (Record, PLC, PLC_Class_Prog_Number, Prog_Record),
              FOREIGN KEY (Prog_Record, PLC, PLC_Class_Prog_Number) REFERENCES dbo.Prog (Record, PLC, PLC_Class_Number));
            INSERT dbo.Prog VALUES (1,1,10,N'old'),(2,1,10,N'new');
            INSERT dbo.Prog_Softkey VALUES (4,1,10,1),(4,1,10,2);
            """);
        var repository = new ProgramManagementRepository(db.ConnectionString);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 1 };
        var roots = await repository.GetProgramsAsync(1, 10);
        var original = roots.Rows.Cast<DataRow>().Single(row => (int)row["Record"] == 1);
        var expected = roots.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => original[column]);
        var preview = await repository.PrepareContextReplacementAsync(context, null, expected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReplaceAndDeleteContextAsync(preview, 2));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog WHERE Record=1"));
        await db.ExecuteAsync("DELETE dbo.Prog_Softkey WHERE Prog_Record=2");

        var changes = await repository.ReplaceAndDeleteContextAsync(preview, 2);
        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog WHERE Record=1"));
        Assert.Equal(2m, await db.DecimalAsync("SELECT Prog_Record FROM dbo.Prog_Softkey"));
        var entry = new ChangeJournalEntry
        {
            OperationKind = ChangeJournalOperationKind.Composite,
            RestoreIncludesAllChanges = true,
            Changes = changes.ToList()
        };
        await new ChangeJournalService().RestoreAsync(entry, db.ConnectionString);
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog WHERE Record=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT Prog_Record FROM dbo.Prog_Softkey"));
    }

    [CompareLocalDbFact]
    public async Task OnlineParameterReplacementMovesRecipeValueAndJournalRestoresIt()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Prog_StepParam (
              Record int NOT NULL, PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL,
              Seq_Record int NOT NULL, Step_Number int NOT NULL, Type int NOT NULL, Value int NOT NULL,
              PRIMARY KEY (Record, PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record, Step_Number, Type));
            CREATE TABLE dbo.Prog_Recipes (
              Record int NOT NULL, PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL,
              Seq_Record int NOT NULL, Recipe_Record int NOT NULL, Step_Number int NOT NULL, Type int NOT NULL, Setpoint int NOT NULL,
              PRIMARY KEY (Record, PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record, Recipe_Record, Step_Number, Type),
              FOREIGN KEY (Record, PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record, Step_Number, Type)
                REFERENCES dbo.Prog_StepParam (Record, PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record, Step_Number, Type));
            INSERT dbo.Prog_StepParam VALUES (1,1,10,3,1,2,1,111),(2,1,10,3,1,2,1,222),(3,1,10,3,1,3,1,333);
            INSERT dbo.Prog_Recipes VALUES (1,1,10,3,1,4,2,1,100),(1,1,10,3,1,5,2,1,200);
            """);

        var repository = new ProgramManagementRepository(db.ConnectionString);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };
        var parameters = await repository.GetParametersAsync(context, 2);
        var selected = parameters.Rows.Cast<DataRow>().Single(row => (int)row["Record"] == 1);
        var expected = parameters.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => selected[column]);
        var preview = await repository.PrepareParameterDeletionAsync(context, 2, 1, 1);
        Assert.Equal(2, preview.Options.Count);
        var replacements = preview.Options.Select(option => new ReferenceReplacement
        {
            SourceTable = option.Hit.SourceTable,
            KeyColumns = option.Hit.SourceKey.Keys.ToArray(),
            KeyValues = option.Hit.SourceKey.Values.ToArray(),
            NewValues = Assert.Single(option.Candidates).Values
        }).ToArray();

        await db.ExecuteAsync("INSERT dbo.Prog_Recipes VALUES (2,1,10,3,1,5,2,1,999)");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteParameterWithReplacementsAsync(preview, expected, replacements));
        Assert.Equal(2m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_Recipes WHERE Record=1"));
        Assert.Equal(999m, await db.DecimalAsync("SELECT Setpoint FROM dbo.Prog_Recipes WHERE Record=2"));
        await db.ExecuteAsync("DELETE dbo.Prog_Recipes WHERE Record=2");

        var changes = await repository.DeleteParameterWithReplacementsAsync(preview, expected, replacements);
        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_StepParam WHERE Record=1"));
        Assert.Equal(2m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_Recipes WHERE Record=2"));
        Assert.Equal(300m, await db.DecimalAsync("SELECT SUM(Setpoint) FROM dbo.Prog_Recipes WHERE Record=2"));
        var entry = new ChangeJournalEntry
        {
            OperationKind = ChangeJournalOperationKind.Composite,
            RestoreIncludesAllChanges = true,
            Changes = changes.ToList()
        };
        await new ChangeJournalService().RestoreAsync(entry, db.ConnectionString);
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_StepParam WHERE Record=1"));
        Assert.Equal(2m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_Recipes WHERE Record=1"));
        Assert.Equal(300m, await db.DecimalAsync("SELECT SUM(Setpoint) FROM dbo.Prog_Recipes WHERE Record=1"));
    }


    [CompareLocalDbFact]
    public async Task OnlineSequenceReplacementMovesOnlyRowsInItsProgram()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Prog (
              Record int NOT NULL, PLC int NOT NULL, PLC_Class_Number int NOT NULL,
              PRIMARY KEY (Record, PLC, PLC_Class_Number));
            CREATE TABLE dbo.Prog_Seq (
              Record int NOT NULL, PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL,
              PRIMARY KEY (Record, PLC, PLC_Class_Prog_Number, Prog_Record));
            CREATE TABLE dbo.Prog_QueueSelections (
              Record int NOT NULL, PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL,
              Seq_Record int NOT NULL, Queue_Record int NOT NULL,
              PRIMARY KEY (Record, PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record, Queue_Record),
              FOREIGN KEY (Seq_Record, PLC, PLC_Class_Prog_Number, Prog_Record)
                REFERENCES dbo.Prog_Seq (Record, PLC, PLC_Class_Prog_Number, Prog_Record));
            INSERT dbo.Prog VALUES (1,1,10),(2,1,10);
            INSERT dbo.Prog_Seq VALUES (1,1,10,1),(2,1,10,1),(1,1,10,2);
            INSERT dbo.Prog_QueueSelections VALUES (7,1,10,1,1,8),(7,1,10,2,1,8);
            """);
        var repository = new ProgramManagementRepository(db.ConnectionString);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 1 };
        var sequences = await repository.GetSequencesAsync(context);
        var original = sequences.Rows.Cast<DataRow>().Single(row => (int)row["Record"] == 1);
        var expected = sequences.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => original[column]);
        var preview = await repository.PrepareContextReplacementAsync(context, 1, expected);

        var changes = await repository.ReplaceAndDeleteContextAsync(preview, 2);
        Assert.Equal(2, changes.Count);
        Assert.Equal(2m, await db.DecimalAsync("SELECT Seq_Record FROM dbo.Prog_QueueSelections WHERE Prog_Record=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT Seq_Record FROM dbo.Prog_QueueSelections WHERE Prog_Record=2"));
        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_Seq WHERE Prog_Record=1 AND Record=1"));
        var entry = new ChangeJournalEntry { OperationKind = ChangeJournalOperationKind.Composite, RestoreIncludesAllChanges = true, Changes = changes.ToList() };
        await new ChangeJournalService().RestoreAsync(entry, db.ConnectionString);
        Assert.Equal(1m, await db.DecimalAsync("SELECT Seq_Record FROM dbo.Prog_QueueSelections WHERE Prog_Record=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_Seq WHERE Prog_Record=1 AND Record=1"));
    }

    private static ProjectTableSchema Schema(string table, string[] keys)
    {
        var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
        schema.KeyColumns.AddRange(keys);
        foreach (var field in keys.Append("Value"))
            schema.Columns.Add(new ProjectColumnSchema { Name = field, DataType = "int" });
        return schema;
    }

    private static ProjectRecord Row(string table, params KeyValuePair<string, int>[] fields)
    {
        var values = new JsonObject();
        foreach (var field in fields)
            values[field.Key] = field.Value;
        return new ProjectRecord { TableName = table, Values = values };
    }
}
