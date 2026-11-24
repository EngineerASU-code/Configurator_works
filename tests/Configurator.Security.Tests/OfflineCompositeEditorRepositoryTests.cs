using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineCompositeEditorRepositoryTests
{
    [Fact]
    public async Task ProgramManagementReadsScopedProgramsFromSnapshot()
    {
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "local" },
            TableSchemas = { Schema("dbo.Prog", "Record", "PLC", "PLC_Class_Number", "Name_L1") },
            LocalRecords =
            {
                Record("dbo.Prog", new("Record", 4), new("PLC", 1), new("PLC_Class_Number", 10), new("Name_L1", "A")),
                Record("dbo.Prog", new("Record", 5), new("PLC", 2), new("PLC_Class_Number", 10), new("Name_L1", "B"))
            }
        };
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);

        var rows = await repository.GetProgramsAsync(1, 10);

        var row = Assert.Single(rows.Rows.Cast<System.Data.DataRow>());
        Assert.Equal(4, row["Record"]);
        Assert.Equal("A", row["Name_L1"]);
        Assert.Equal(System.Data.DataRowState.Unchanged, row.RowState);
    }

    [Fact]
    public async Task MatrixRepositoryUpdatesSnapshotUsingStableKey()
    {
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "local" },
            TableSchemas = { Schema("dbo.Matrix_List", "Record", "PLC", "PLC_Class_Number", "Name_L1") },
            LocalRecords =
            {
                Record("dbo.Matrix_List", new("Record", 7), new("PLC", 1), new("PLC_Class_Number", 20), new("Name_L1", "Before"))
            }
        };
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramSupportRepository(source);
        var data = await repository.LoadMatrixAsync("matrix-list", 1, 20);
        data.Rows.Rows[0]["Name_L1"] = "After";

        await repository.SaveAsync(data, new System.Collections.Generic.Dictionary<string, object>
        {
            ["PLC"] = 1, ["PLC_Class_Number"] = 20
        });

        Assert.Equal("After", source.GetRecords("dbo.Matrix_List").Single().Values["Name_L1"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1, 2, true)]  // Another step in the same sequence points to the target.
    [InlineData(2, 2, false)] // A different sequence is independent.
    [InlineData(1, 1, false)] // A self-reference disappears with the target.
    public async Task OfflineStepDeletionChecksOnlyIncomingDestinations(int sourceSequence, int sourceStep, bool blocked)
    {
        const string table = "dbo.Prog_StepTransition";
        string[] keys = { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number" };
        var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
        schema.KeyColumns.AddRange(keys);
        foreach (string name in keys.Concat(new[] { "TransDest_01" }))
            schema.Columns.Add(new ProjectColumnSchema { Name = name, DataType = "int", IsNullable = true });
        var project = new ConfiguratorProject { Manifest = new ConfiguratorProjectManifest { Name = "local" } };
        project.TableSchemas.Add(schema);
        project.LocalRecords.Add(Record(table, new("PLC", 1), new("PLC_Class_Prog_Number", 10),
            new("Prog_Record", 3), new("Seq_Record", 1), new("SeqStep_Number", 1),
            new("TransDest_01", sourceStep == 1 ? 1 : 0)));
        if (sourceStep != 1)
            project.LocalRecords.Add(Record(table, new("PLC", 1), new("PLC_Class_Prog_Number", 10),
                new("Prog_Record", 3), new("Seq_Record", sourceSequence), new("SeqStep_Number", sourceStep),
                new("TransDest_01", 1)));
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };
        var expected = RowValues((await repository.GetTransitionsAsync(context)).Rows.Cast<System.Data.DataRow>().Single(r => (int)r["SeqStep_Number"] == 1));

        if (blocked)
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteStepFromSequenceAsync(context, 1, expected: expected));
        else
            await repository.DeleteStepFromSequenceAsync(context, 1, expected: expected);

        Assert.Equal(blocked, source.GetRecords(table).Any(row =>
            row.Values["Seq_Record"]!.GetValue<int>() == 1 && row.Values["SeqStep_Number"]!.GetValue<int>() == 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineMoveSwapsAllReferencesAndRollsBackOnInvalidDestination(bool invalid)
    {
        string[] scope = { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record" };
        var project = new ConfiguratorProject { Manifest = new ConfiguratorProjectManifest { Name = "move" } };
        foreach (string table in new[] { "dbo.Prog_StepTransition", "dbo.Prog_StepParam", "dbo.Prog_Recipes" })
        {
            bool transition = table.EndsWith("Transition");
            string step = transition ? "SeqStep_Number" : "Step_Number";
            var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
            schema.KeyColumns.AddRange(scope.Concat(new[] { step }));
            foreach (string name in scope.Concat(new[] { step }).Concat(transition
                         ? Enumerable.Range(1, 8).Select(i => $"TransDest_{i:00}") : new[] { "Marker" }))
                schema.Columns.Add(new ProjectColumnSchema { Name = name, DataType = "int", IsNullable = true });
            project.TableSchemas.Add(schema);
            for (int number = 1; number <= 2; number++)
            {
                var values = new System.Collections.Generic.List<(string Name, object Value)>
                {
                    ("PLC", 1), ("PLC_Class_Prog_Number", 10), ("Prog_Record", 3), ("Seq_Record", 1), (step, number)
                };
                if (transition)
                    for (int i = 1; i <= 8; i++)
                        values.Add(($"TransDest_{i:00}", i == 1 ? 3 - number : i == 2 && invalid && number == 2 ? "bad" : 0));
                else values.Add(("Marker", number));
                project.LocalRecords.Add(Record(table, values.ToArray()));
            }
        }
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };
        var rows = await repository.GetTransitionsAsync(context);
        var original = rows.Rows[0].Table.Columns.Cast<System.Data.DataColumn>()
            .ToDictionary(c => c.ColumnName, c => rows.Rows[0][c.ColumnName], StringComparer.OrdinalIgnoreCase);
        string before = string.Join("|", project.LocalRecords.Select(x => x.Values.ToJsonString()));

        if (invalid)
        {
            await Assert.ThrowsAsync<FormatException>(() => repository.MoveOfflineStepAsync(context, original, 1));
            Assert.Equal(before, string.Join("|", project.LocalRecords.Select(x => x.Values.ToJsonString())));
        }
        else
        {
            Assert.Equal(2, await repository.MoveOfflineStepAsync(context, original, 1));
            var transitions = source.GetRecords("dbo.Prog_StepTransition");
            Assert.Contains(transitions, r => r.Values["SeqStep_Number"]!.GetValue<int>() == 2
                && r.Values["TransDest_01"]!.GetValue<int>() == 1);
            foreach (string table in new[] { "dbo.Prog_StepParam", "dbo.Prog_Recipes" })
            {
                var records = source.GetRecords(table);
                Assert.Contains(records, r => r.Values["Step_Number"]!.GetValue<int>() == 2 && r.Values["Marker"]!.GetValue<int>() == 1);
                Assert.Contains(records, r => r.Values["Step_Number"]!.GetValue<int>() == 1 && r.Values["Marker"]!.GetValue<int>() == 2);
            }
        }
    }

    [Fact]
    public async Task ProgramParameterCrudKeepsOccurrenceScopeAndPreservesRecipeValues()
    {
        const string parameter = "dbo.Prog_StepParam";
        const string recipe = "dbo.Prog_Recipes";
        string[] key = { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Step_Number", "Type", "Record" };
        var project = new ConfiguratorProject { Manifest = new ConfiguratorProjectManifest { Name = "parameters" } };
        foreach (string table in new[] { parameter, recipe })
        {
            var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
            schema.KeyColumns.AddRange(table == parameter ? key : key.Append("Recipe_Record"));
            foreach (string field in schema.KeyColumns)
                schema.Columns.Add(new ProjectColumnSchema { Name = field, DataType = "int" });
            project.TableSchemas.Add(schema);
        }
        project.LocalRecords.Add(Record(recipe, new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 3),
            new("Seq_Record", 1), new("Step_Number", 2), new("Type", 0), new("Record", 1), new("Recipe_Record", 4)));
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };
        var values = new System.Collections.Generic.Dictionary<string, object> { ["Type"] = 0, ["Step_Number"] = 99 };

        Assert.Equal(1, await repository.AddParameterAsync(context, 2, values));
        Assert.Equal(1, await repository.AddParameterAsync(context, 0, values));
        Assert.Empty((await repository.GetParametersAsync(context, 99)).Rows.Cast<System.Data.DataRow>());
        var expected2 = RowValues(Assert.Single((await repository.GetParametersAsync(context, 2)).Rows.Cast<System.Data.DataRow>()));
        var expected0 = RowValues(Assert.Single((await repository.GetParametersAsync(context, 0)).Rows.Cast<System.Data.DataRow>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteParameterAsync(context, 2, 0, 1, expected: expected2));
        await repository.DeleteParameterAsync(context, 0, 0, 1, expected: expected0);

        Assert.Single(source.GetRecords(parameter));
        Assert.Single(source.GetRecords(recipe));
    }

    [Fact]
    public async Task SequenceDeletionRequiresConfirmationToRemoveOrphanParameters()
    {
        const string sequence = ProgramManagementRepository.SequenceTable;
        const string parameter = ProgramManagementRepository.ParameterTable;
        string[] scope = { "PLC", "PLC_Class_Prog_Number", "Prog_Record" };
        var project = new ConfiguratorProject { Manifest = new ConfiguratorProjectManifest { Name = "sequence" } };
        foreach (string table in new[] { sequence, parameter })
        {
            var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
            schema.KeyColumns.AddRange(scope.Append("Record"));
            if (table == parameter) schema.KeyColumns.AddRange(new[] { "Seq_Record", "Step_Number", "Type" });
            foreach (string field in schema.KeyColumns)
                schema.Columns.Add(new ProjectColumnSchema { Name = field, DataType = "int" });
            schema.Columns.Add(new ProjectColumnSchema { Name = "Value", DataType = "int", IsNullable = true });
            project.TableSchemas.Add(schema);
        }
        project.LocalRecords.Add(Record(sequence, new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 3), new("Record", 2), new("Value", 10)));
        project.LocalRecords.Add(Record(parameter, new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 3),
            new("Record", 1), new("Seq_Record", 2), new("Step_Number", 99), new("Type", 0), new("Value", 10)));
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 2 };

        Assert.Empty((await repository.GetParametersAsync(context, 0)).Rows.Cast<System.Data.DataRow>());
        var orphan = Assert.Single((await repository.GetAllSequenceParametersAsync(context)).Rows.Cast<System.Data.DataRow>());
        Assert.Equal(99, orphan["Step_Number"]);
        var expectedSequence = RowValues(Assert.Single((await repository.GetSequencesAsync(context)).Rows.Cast<System.Data.DataRow>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteSequenceAsync(context, 2, expected: expectedSequence));
        Assert.Single(source.GetRecords(sequence));
        Assert.Single(source.GetRecords(parameter));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteSequenceAsync(context, 3, deleteDependents: true));
        Assert.Single(source.GetRecords(parameter));
        project.LocalRecords.Add(Record(parameter, new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 3),
            new("Record", 1), new("Seq_Record", 7), new("Step_Number", 99), new("Type", 0)));
        source.InvalidateIndex();
        var preview = await repository.PrepareSequenceDeletionAsync(context, 2);
        var existingChild = source.GetRecords(parameter).Single(record => record.Values["Seq_Record"]!.GetValue<int>() == 2);
        existingChild.Values["Value"] = 11;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteSequenceAsync(context, 2, deleteDependents: true, expected: expectedSequence, preview: preview));
        Assert.Equal(11, existingChild.Values["Value"]!.GetValue<int>());
        existingChild.Values["Value"] = 10;
        var parent = Assert.Single(source.GetRecords(sequence));
        parent.Values["Value"] = 11;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteSequenceAsync(context, 2, deleteDependents: true, expected: expectedSequence, preview: preview));
        Assert.Equal(11, parent.Values["Value"]!.GetValue<int>());
        parent.Values["Value"] = 10;
        var added = Record(parameter, new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 3),
            new("Record", 2), new("Seq_Record", 2), new("Step_Number", 99), new("Type", 0));
        project.LocalRecords.Add(added);
        source.InvalidateIndex();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteSequenceAsync(context, 2, deleteDependents: true, expected: expectedSequence, preview: preview));
        Assert.Equal(3, source.GetRecords(parameter).Count);
        project.LocalRecords.Remove(added);
        source.InvalidateIndex();
        preview = await repository.PrepareSequenceDeletionAsync(context, 2);
        var deleted = await repository.DeleteSequenceAsync(context, 2, deleteDependents: true, expected: expectedSequence, preview: preview);
        Assert.Equal(new[] { parameter, sequence }, deleted.Select(row => row.TableName));
        Assert.Empty(source.GetRecords(sequence));
        Assert.Empty((await repository.GetAllSequenceParametersAsync(context)).Rows.Cast<System.Data.DataRow>());
        Assert.Single(source.GetRecords(parameter));
        var entry = new ChangeJournalEntry
        {
            ProjectType = "Offline",
            ProjectId = project.Manifest.ProjectId,
            Changes = deleted.ToList()
        };
        Assert.True(entry.CanRestore);
        new ChangeJournalService(System.IO.Path.GetTempPath()).RestoreOffline(entry, project, source.InvalidateIndex);
        Assert.Single(source.GetRecords(sequence));
        Assert.Equal(2, source.GetRecords(parameter).Count);
    }

    [Fact]
    public async Task QueueItemCanOnlyBeSavedUnderAQueueFromItsProgram()
    {
        string[] scope = { "PLC", "PLC_Class_Prog_Number", "Prog_Record" };
        const string queues = "dbo.Prog_Queue";
        const string items = "dbo.Prog_QueueSelections";
        var project = new ConfiguratorProject();
        foreach (string table in new[] { queues, items })
        {
            var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
            schema.KeyColumns.Add("Record");
            schema.KeyColumns.AddRange(scope);
            if (table == items) schema.KeyColumns.AddRange(new[] { "Seq_Record", "Queue_Record" });
            foreach (string column in schema.KeyColumns)
                schema.Columns.Add(new ProjectColumnSchema { Name = column, DataType = "int" });
            project.TableSchemas.Add(schema);
        }
        project.LocalRecords.Add(Record(queues, new("Record", 7), new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 3)));
        project.LocalRecords.Add(Record(queues, new("Record", 8), new("PLC", 1), new("PLC_Class_Prog_Number", 10), new("Prog_Record", 4)));

        var repository = new ProgramSupportRepository(new OfflineProjectDataSource(project));
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };
        var tableData = await repository.LoadProgramAsync("queue-selections", context);
        var scopeValues = ProgramSupportRepository.GetProgramScope(tableData.Definition, context);
        var row = tableData.Rows.NewRow();
        foreach (var pair in scopeValues) row[pair.Key] = pair.Value;
        row["Record"] = 1;
        row["Queue_Record"] = 8;
        tableData.Rows.Rows.Add(row);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(tableData, scopeValues));
        row["Queue_Record"] = 7;
        await repository.SaveAsync(tableData, scopeValues);

        var stored = Assert.Single((await repository.LoadProgramAsync("queue-selections", context)).Rows.Rows.Cast<System.Data.DataRow>());
        Assert.Equal(7, stored["Queue_Record"]);
    }

    private static ProjectTableSchema Schema(string table, params string[] columns)
    {
        var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
        schema.KeyColumns.AddRange(columns.TakeWhile(name => name == "Record" || name == "PLC" || name == "PLC_Class_Number"));
        foreach (string name in columns)
            schema.Columns.Add(new ProjectColumnSchema { Name = name, DataType = name is "Record" or "PLC" or "PLC_Class_Number" ? "int" : "nvarchar", IsNullable = true });
        return schema;
    }

    private static Dictionary<string, object> RowValues(System.Data.DataRow row) =>
        row.Table.Columns.Cast<System.Data.DataColumn>().ToDictionary(column => column.ColumnName, column => row[column], StringComparer.OrdinalIgnoreCase);

    private static ProjectRecord Record(string table, params (string Name, object Value)[] values)
    {
        var schema = Schema(table, values.Select(item => item.Name).ToArray());
        var json = new JsonObject();
        foreach (var pair in values) json[pair.Name] = ProjectKeyCodec.ToNode(pair.Value);
        return new ProjectRecord
        {
            TableName = table,
            Key = ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => json[name]),
            Values = json
        };
    }
}
