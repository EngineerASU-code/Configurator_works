using System.Data;
using System.Text.Json.Nodes;
using Configurator;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProgramStepMoveJournalTests
{
    [CompareLocalDbFact]
    public async Task OnlineMoveCapturesAndRestoresTransitionAndDependentRows()
    {
        await using var db = await CreateMoveDatabaseAsync();
        var context = Context();
        var repository = new ProgramManagementRepository(db.ConnectionString);
        var rows = await repository.GetTransitionsAsync(context);
        var selected = rows.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["SeqStep_Number"]) == 1);
        var original = rows.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => selected[column]);

        var move = await repository.MoveStepWithJournalAsync(context, original, 1);
        var entry = MoveEntry(move);

        Assert.True(entry.CanRestore);
        Assert.Equal(2, move.NewStepNumber);
        Assert.Contains(move.Changes, change => change.TableName == ProgramManagementRepository.TransitionTable);
        Assert.Contains(move.Changes, change => change.TableName == ProgramManagementRepository.ParameterTable);
        Assert.Contains(move.Changes, change => change.TableName == "dbo.Prog_Recipes");
        Assert.All(move.Changes, change =>
        {
            Assert.Equal(ChangeJournalOperationKind.Update, change.Kind);
            Assert.NotEmpty(change.Before);
            Assert.NotEmpty(change.After);
            Assert.NotEmpty(change.IdentityBefore);
            Assert.NotEmpty(change.IdentityAfter);
        });

        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ProgramStepMoveTests", Guid.NewGuid().ToString("N")));
        await journal.RestoreAsync(entry, db.ConnectionString);

        Assert.Equal(2m, await db.DecimalAsync("SELECT TransDest_01 FROM dbo.Prog_StepTransition WHERE SeqStep_Number=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT TransDest_01 FROM dbo.Prog_StepTransition WHERE SeqStep_Number=2"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT TransDest_01 FROM dbo.Prog_StepTransition WHERE SeqStep_Number=3"));
        Assert.Equal(10m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepParam WHERE Step_Number=1"));
        Assert.Equal(20m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepParam WHERE Step_Number=2"));
        Assert.Equal(111m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_Recipes WHERE Step_Number=1"));
        Assert.Equal(222m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_Recipes WHERE Step_Number=2"));
    }

    [CompareLocalDbFact]
    public async Task OnlineMoveRestoreRollsBackStagedKeysWhenDependentConstraintRejectsRestore()
    {
        await using var db = await CreateMoveDatabaseAsync();
        var context = Context();
        var repository = new ProgramManagementRepository(db.ConnectionString);
        var rows = await repository.GetTransitionsAsync(context);
        var selected = rows.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["SeqStep_Number"]) == 1);
        var original = rows.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => selected[column]);
        var move = await repository.MoveStepWithJournalAsync(context, original, 1);
        var entry = MoveEntry(move);
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ProgramStepMoveTests", Guid.NewGuid().ToString("N")));

        await db.ExecuteAsync("ALTER TABLE dbo.Prog_StepParam ADD CONSTRAINT CK_Prog_StepParam_NoOriginalFirst CHECK (Step_Number <> 1 OR Marker <> 10)");

        await Assert.ThrowsAsync<SqlException>(() => journal.RestoreAsync(entry, db.ConnectionString));

        Assert.Equal(102m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepTransition WHERE SeqStep_Number=1"));
        Assert.Equal(101m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepTransition WHERE SeqStep_Number=2"));
        Assert.Equal(2m, await db.DecimalAsync("SELECT TransDest_01 FROM dbo.Prog_StepTransition WHERE SeqStep_Number=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT TransDest_01 FROM dbo.Prog_StepTransition WHERE SeqStep_Number=2"));
        Assert.Equal(10m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepParam WHERE Step_Number=2"));
        Assert.Equal(20m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepParam WHERE Step_Number=1"));
        Assert.Equal(111m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_Recipes WHERE Step_Number=2"));
        Assert.Equal(222m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_Recipes WHERE Step_Number=1"));
    }

    [CompareLocalDbFact]
    public async Task OnlineMoveRestoreRejectsDependentRowsAddedAfterTheMove()
    {
        await using var db = await CreateMoveDatabaseAsync();
        var context = Context();
        var repository = new ProgramManagementRepository(db.ConnectionString);
        var rows = await repository.GetTransitionsAsync(context);
        var selected = rows.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["SeqStep_Number"]) == 1);
        var original = rows.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => selected[column]);
        var move = await repository.MoveStepWithJournalAsync(context, original, 1);
        var entry = MoveEntry(move);
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ProgramStepMoveTests", Guid.NewGuid().ToString("N")));

        await db.ExecuteAsync("INSERT dbo.Prog_StepParam VALUES (1,10,3,1,1,30)");

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RestoreAsync(entry, db.ConnectionString));

        Assert.Equal(102m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepTransition WHERE SeqStep_Number=1"));
        Assert.Equal(101m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepTransition WHERE SeqStep_Number=2"));
        Assert.Equal(10m, await db.DecimalAsync("SELECT Marker FROM dbo.Prog_StepParam WHERE Step_Number=2"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_StepParam WHERE Step_Number=1 AND Marker=20"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_StepParam WHERE Step_Number=1 AND Marker=30"));
    }

    [Fact]
    public async Task OfflineMoveCapturesAndRestoresTheSameAffectedRows()
    {
        var project = CreateOfflineMoveProject();
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = Context();
        var rows = await repository.GetTransitionsAsync(context);
        var selected = rows.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["SeqStep_Number"]) == 1);
        var original = rows.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => selected[column]);

        var move = await repository.MoveStepWithJournalAsync(context, original, 1);
        var entry = MoveEntry(move, project.Manifest.ProjectId);
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ProgramStepMoveTests", Guid.NewGuid().ToString("N")));
        journal.BindOfflineProject(project, () => { }, 3);

        Assert.True(entry.CanRestore);
        journal.RestoreOffline(entry, project, () => { });

        Assert.Equal(2, OfflineValue(project, ProgramManagementRepository.TransitionTable, "SeqStep_Number", 1, "TransDest_01"));
        Assert.Equal(1, OfflineValue(project, ProgramManagementRepository.TransitionTable, "SeqStep_Number", 2, "TransDest_01"));
        Assert.Equal(1, OfflineValue(project, ProgramManagementRepository.ParameterTable, "Marker", 10, "Step_Number"));
        Assert.Equal(2, OfflineValue(project, ProgramManagementRepository.ParameterTable, "Marker", 20, "Step_Number"));
        Assert.Equal(1, OfflineValue(project, "dbo.Prog_Recipes", "Marker", 111, "Step_Number"));
        Assert.Equal(2, OfflineValue(project, "dbo.Prog_Recipes", "Marker", 222, "Step_Number"));
    }

    [Fact]
    public async Task OfflineMoveRestoreRejectsDependentRowsAddedAfterTheMove()
    {
        var project = CreateOfflineMoveProject();
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = Context();
        var rows = await repository.GetTransitionsAsync(context);
        var selected = rows.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["SeqStep_Number"]) == 1);
        var original = rows.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => selected[column]);
        var move = await repository.MoveStepWithJournalAsync(context, original, 1);
        var entry = MoveEntry(move, project.Manifest.ProjectId);
        var journal = new ChangeJournalService(Path.Combine(Path.GetTempPath(), "ProgramStepMoveTests", Guid.NewGuid().ToString("N")));
        journal.BindOfflineProject(project, () => { }, 3);
        var parameters = project.TableSchemas.Single(schema => schema.TableName == ProgramManagementRepository.ParameterTable);
        AddRecord(project, parameters, new Dictionary<string, object> { ["PLC"] = 1, ["PLC_Class_Prog_Number"] = 10,
            ["Prog_Record"] = 3, ["Seq_Record"] = 1, ["Step_Number"] = 1, ["Marker"] = 30 });
        var beforeRestore = string.Join("\n", project.LocalRecords.Select(record => record.Values.ToJsonString()));

        Assert.Throws<InvalidOperationException>(() => journal.RestoreOffline(entry, project, () => { }));

        Assert.Equal(beforeRestore, string.Join("\n", project.LocalRecords.Select(record => record.Values.ToJsonString())));
        Assert.Equal(102, OfflineValue(project, ProgramManagementRepository.TransitionTable, "SeqStep_Number", 1, "Marker"));
        Assert.Equal(2, OfflineValue(project, ProgramManagementRepository.ParameterTable, "Marker", 10, "Step_Number"));
        Assert.Equal(1, OfflineValue(project, ProgramManagementRepository.ParameterTable, "Marker", 30, "Step_Number"));
    }

    private static async Task<CompareLocalDb> CreateMoveDatabaseAsync()
    {
        var db = await CompareLocalDb.CreateAsync();
        string scope = "PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL, Seq_Record int NOT NULL";
        string key = "PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record";
        string destinations = string.Join(",", Enumerable.Range(1, 8).Select(index => $"TransDest_{index:00} int NULL"));
        await db.ExecuteAsync($"""
            CREATE TABLE dbo.Prog_StepTransition ({scope}, SeqStep_Number smallint NOT NULL, Marker int NOT NULL, {destinations}, PRIMARY KEY ({key},SeqStep_Number));
            CREATE TABLE dbo.Prog_StepParam ({scope}, Step_Number smallint NOT NULL, Marker int NOT NULL, PRIMARY KEY ({key},Step_Number,Marker));
            CREATE TABLE dbo.Prog_Recipes ({scope}, Step_Number smallint NOT NULL, Marker int NOT NULL, PRIMARY KEY ({key},Step_Number,Marker));
            INSERT dbo.Prog_StepTransition ({key},SeqStep_Number,Marker,TransDest_01) VALUES (1,10,3,1,1,101,2),(1,10,3,1,2,102,1),(1,10,3,1,3,103,1);
            INSERT dbo.Prog_StepParam VALUES (1,10,3,1,1,10),(1,10,3,1,2,20);
            INSERT dbo.Prog_Recipes VALUES (1,10,3,1,1,111),(1,10,3,1,2,222);
            """);
        return db;
    }

    private static ConfiguratorProject CreateOfflineMoveProject()
    {
        var project = new ConfiguratorProject();
        project.Manifest.ProjectId = "program-move-offline";
        project.Manifest.Name = "Program move test";
        var scope = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record" };
        var transitions = AddSchema(project, ProgramManagementRepository.TransitionTable,
            scope.Concat(new[] { "SeqStep_Number" }), Enumerable.Range(1, 8).Select(index => $"TransDest_{index:00}").Append("Marker"));
        var parameters = AddSchema(project, ProgramManagementRepository.ParameterTable,
            scope.Concat(new[] { "Step_Number", "Marker" }), new[] { "Step_Number", "Marker" });
        var recipes = AddSchema(project, "dbo.Prog_Recipes",
            scope.Concat(new[] { "Step_Number", "Marker" }), new[] { "Step_Number", "Marker" });

        for (int number = 1; number <= 3; number++)
        {
            var values = new Dictionary<string, object> { ["PLC"] = 1, ["PLC_Class_Prog_Number"] = 10,
                ["Prog_Record"] = 3, ["Seq_Record"] = 1, ["SeqStep_Number"] = number, ["Marker"] = 100 + number };
            for (int index = 1; index <= 8; index++) values[$"TransDest_{index:00}"] = index == 1 ? 3 - number : 0;
            AddRecord(project, transitions, values);
        }
        foreach (var (schema, marker) in new[] { (parameters, 10), (parameters, 20), (recipes, 111), (recipes, 222) })
        {
            var step = marker is 10 or 111 ? 1 : 2;
            AddRecord(project, schema, new Dictionary<string, object> { ["PLC"] = 1, ["PLC_Class_Prog_Number"] = 10,
                ["Prog_Record"] = 3, ["Seq_Record"] = 1, ["Step_Number"] = step, ["Marker"] = marker });
        }
        return project;
    }

    private static ProjectTableSchema AddSchema(ConfiguratorProject project, string table, IEnumerable<string> keyColumns,
        IEnumerable<string> columns)
    {
        var keys = keyColumns.ToArray();
        var schema = new ProjectTableSchema { TableName = table, HasStableKey = true, KeyColumns = keys.ToList() };
        foreach (string column in columns.Concat(keys).Distinct(StringComparer.OrdinalIgnoreCase))
            schema.Columns.Add(new ProjectColumnSchema { Name = column, DataType = "int", IsNullable = !keys.Contains(column, StringComparer.OrdinalIgnoreCase) });
        project.TableSchemas.Add(schema);
        return schema;
    }

    private static void AddRecord(ConfiguratorProject project, ProjectTableSchema schema, IReadOnlyDictionary<string, object> values)
    {
        var recordValues = new JsonObject();
        foreach (var pair in values) recordValues[pair.Key] = System.Text.Json.JsonSerializer.SerializeToNode(pair.Value);
        string key = ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => ProjectKeyCodec.ToNode(values[name]));
        project.LocalRecords.Add(new ProjectRecord { TableName = schema.TableName, Key = key, Values = recordValues });
    }

    private static ChangeJournalEntry MoveEntry(ProgramStepMoveJournalResult move, string projectId = "")
        => new()
        {
            ProjectType = string.IsNullOrWhiteSpace(projectId) ? "Online" : "Offline",
            ProjectId = projectId,
            RestoreKind = ChangeJournalRestoreKind.ProgramStepMove,
            Changes = move.Changes.ToList(),
            RestorePayloadJson = move.RestorePayloadJson
        };

    private static ProgramContext Context() => new() { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };

    private static int OfflineValue(ConfiguratorProject project, string table, string identityField, int identity,
        string valueField)
        => Convert.ToInt32(project.LocalRecords.Single(row => row.TableName == table &&
            Convert.ToInt32(row.Values[identityField]?.ToString()) == identity).Values[valueField]?.ToString());
}
