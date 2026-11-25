using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProgramStepDeletionReplacementTests
{
    [Theory]
    [InlineData("replace")]
    [InlineData("stale")]
    [InlineData("child")]
    [InlineData("collision")]
    public async Task OfflineDeletesStepAndReplacesReferencesOnlyWhenPreviewAndDependenciesAllow(string scenario)
    {
        const string transitions = ProgramManagementRepository.TransitionTable;
        const string parameters = ProgramManagementRepository.ParameterTable;
        const string recipes = "dbo.Prog_Recipes";
        string[] scope = ["PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record"];
        var project = new ConfiguratorProject();
        ProjectTableSchema Schema(string name, string step, params string[] columns) => new()
        {
            TableName = name,
            HasStableKey = true,
            KeyColumns = scope.Append(step).ToList(),
            Columns = scope.Append(step).Concat(columns).Select(column =>
                new ProjectColumnSchema { Name = column, DataType = "int", IsNullable = true }).ToList()
        };
        project.TableSchemas.Add(Schema(transitions, "SeqStep_Number", "TransDest_01", "TransDest_02"));
        var parameterSchema = Schema(parameters, "Step_Number", "Type", "Record", "Value");
        parameterSchema.KeyColumns.AddRange(["Type", "Record"]);
        project.TableSchemas.Add(parameterSchema);
        var recipeSchema = Schema(recipes, "Step_Number", "Type", "Record", "Recipe_Record", "Setpoint");
        recipeSchema.KeyColumns.AddRange(["Type", "Record", "Recipe_Record"]);
        project.TableSchemas.Add(recipeSchema);
        ProjectRecord Add(string table, string key, int number, int sequence = 1)
        {
            var record = new ProjectRecord
            {
                TableName = table,
                Values = new() { ["PLC"] = 1, ["PLC_Class_Prog_Number"] = 10, ["Prog_Record"] = 3, ["Seq_Record"] = sequence, [key] = number }
            };
            project.LocalRecords.Add(record);
            return record;
        }
        _ = Add(transitions, "SeqStep_Number", 1);
        var referring = Add(transitions, "SeqStep_Number", 2);
        referring.Values["TransDest_01"] = 1;
        referring.Values["TransDest_02"] = 1;
        _ = Add(transitions, "SeqStep_Number", 3);
        var otherSequence = Add(transitions, "SeqStep_Number", 2, sequence: 2);
        otherSequence.Values["TransDest_01"] = 1;
        if (scenario is "child" or "collision")
        {
            var parameter = Add(parameters, "Step_Number", 1);
            parameter.Values["Type"] = 1;
            parameter.Values["Record"] = 4;
            parameter.Values["Value"] = 59;
            var recipe = Add(recipes, "Step_Number", 1);
            recipe.Values["Type"] = 1;
            recipe.Values["Record"] = 4;
            recipe.Values["Recipe_Record"] = 9;
            recipe.Values["Setpoint"] = 7;
            if (scenario == "collision")
            {
                var occupied = Add(recipes, "Step_Number", 3);
                occupied.Values["Type"] = 1;
                occupied.Values["Record"] = 4;
                occupied.Values["Recipe_Record"] = 9;
                occupied.Values["Setpoint"] = 8;
            }
        }
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };
        var preview = await repository.PrepareStepDeletionAsync(context, 1);
        var expected = preview.Rows.Single(row => (int)row["SeqStep_Number"] == 1);

        Assert.Equal(2, preview.IncomingCount);
        Assert.Equal(3, Assert.Single(preview.Candidates));
        if (scenario == "stale") referring.Values["TransDest_02"] = 3;
        string[] before = project.LocalRecords.Select(record => record.Values.ToJsonString()).ToArray();
        if (scenario is "stale" or "collision")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ReplaceStepReferencesAndDeleteAsync(context, preview, 3, expected));
            Assert.Equal(before, project.LocalRecords.Select(record => record.Values.ToJsonString()));
            return;
        }

        var journal = await repository.ReplaceStepReferencesAndDeleteAsync(context, preview, 3, expected);
        Assert.Equal(scenario == "child" ? 4 : 2, journal.Count);
        var redirect = Assert.Single(journal, change => change.TableName == transitions && change.Kind == ChangeJournalOperationKind.Update);
        Assert.Equal(ChangeJournalOperationKind.Delete, journal[^1].Kind);
        Assert.Equal(1, redirect.Before["TransDest_01"].ToObject());
        Assert.Equal(3, redirect.After["TransDest_01"].ToObject());
        Assert.Equal(3, redirect.After["TransDest_02"].ToObject());
        if (scenario == "child")
        {
            Assert.Equal(3, Assert.Single(source.GetRecords(parameters)).Values["Step_Number"]!.GetValue<int>());
            Assert.Equal(59, Assert.Single(source.GetRecords(parameters)).Values["Value"]!.GetValue<int>());
            Assert.Equal(3, Assert.Single(source.GetRecords(recipes)).Values["Step_Number"]!.GetValue<int>());
            Assert.Equal(7, Assert.Single(source.GetRecords(recipes)).Values["Setpoint"]!.GetValue<int>());
        }
        Assert.DoesNotContain(source.GetRecords(transitions), record => record.Values["SeqStep_Number"]!.GetValue<int>() == 1);
        Assert.Equal(3, referring.Values["TransDest_01"]!.GetValue<int>());
        Assert.Equal(3, referring.Values["TransDest_02"]!.GetValue<int>());
        Assert.Equal(1, otherSequence.Values["TransDest_01"]!.GetValue<int>());
    }

    [CompareLocalDbFact]
    public async Task OnlineRedirectsIncomingTransitionsAndRejectsStalePreview()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Prog_StepTransition (
                PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL,
                Seq_Record int NOT NULL, SeqStep_Number int NOT NULL, TransDest_01 int NULL, TransDest_02 smallint NULL,
                PRIMARY KEY (PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record, SeqStep_Number));
            INSERT dbo.Prog_StepTransition VALUES (1,10,3,1,1,NULL,NULL), (1,10,3,1,2,1,1),
                (1,10,3,1,3,NULL,NULL), (1,10,3,2,2,1,1);
            CREATE TABLE dbo.Prog_StepParam (
                Record int NOT NULL, PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL,
                Seq_Record int NOT NULL, Step_Number int NOT NULL, Type int NOT NULL, Value int,
                PRIMARY KEY (Record, PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record, Step_Number, Type));
            CREATE TABLE dbo.Prog_Recipes (
                Record int NOT NULL, PLC int NOT NULL, PLC_Class_Prog_Number int NOT NULL, Prog_Record int NOT NULL,
                Seq_Record int NOT NULL, Recipe_Record int NOT NULL, Step_Number int NOT NULL, Type int NOT NULL, Setpoint int,
                PRIMARY KEY (Record, PLC, PLC_Class_Prog_Number, Prog_Record, Seq_Record, Recipe_Record, Step_Number, Type));
            INSERT dbo.Prog_StepParam VALUES (4,1,10,3,1,1,1,59);
            INSERT dbo.Prog_Recipes VALUES (4,1,10,3,1,9,1,1,7);
            """);
        var repository = new ProgramManagementRepository(db.ConnectionString);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };
        var stale = await repository.PrepareStepDeletionAsync(context, 1);
        var expected = stale.Rows.Single(row => (int)row["SeqStep_Number"] == 1);
        await db.ExecuteAsync("UPDATE dbo.Prog_StepTransition SET TransDest_02=NULL WHERE Seq_Record=1 AND SeqStep_Number=2;");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReplaceStepReferencesAndDeleteAsync(context, stale, 3, expected));
        Assert.Equal(1, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_StepTransition WHERE Seq_Record=1 AND SeqStep_Number=1;"));
        Assert.Equal(1, await db.DecimalAsync("SELECT TransDest_01 FROM dbo.Prog_StepTransition WHERE Seq_Record=1 AND SeqStep_Number=2;"));

        await db.ExecuteAsync("UPDATE dbo.Prog_StepTransition SET TransDest_02=1 WHERE Seq_Record=1 AND SeqStep_Number=2;");
        await db.ExecuteAsync("INSERT dbo.Prog_Recipes VALUES (4,1,10,3,1,9,3,1,8);");
        var conflicting = await repository.PrepareStepDeletionAsync(context, 1);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReplaceStepReferencesAndDeleteAsync(context, conflicting, 3, expected));
        Assert.Contains("с таким составным ключом", failure.Message);
        Assert.Equal(59, await db.DecimalAsync("SELECT Value FROM dbo.Prog_StepParam WHERE Step_Number=1;"));
        Assert.Equal(0, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_StepParam WHERE Step_Number=3;"));
        Assert.Equal(8, await db.DecimalAsync("SELECT Setpoint FROM dbo.Prog_Recipes WHERE Step_Number=3;"));

        await db.ExecuteAsync("DELETE dbo.Prog_Recipes WHERE Step_Number=3;");
        var current = await repository.PrepareStepDeletionAsync(context, 1);
        var journal = await repository.ReplaceStepReferencesAndDeleteAsync(context, current, 3, expected);
        Assert.Equal(4, journal.Count);
        var redirect = Assert.Single(journal, change => change.TableName == ProgramManagementRepository.TransitionTable && change.Kind == ChangeJournalOperationKind.Update);
        Assert.Equal(ChangeJournalOperationKind.Delete, journal[^1].Kind);
        Assert.Equal(3, redirect.After["TransDest_01"].ToObject());
        Assert.Equal((short)3, redirect.After["TransDest_02"].ToObject());
        Assert.Equal(59, await db.DecimalAsync("SELECT Value FROM dbo.Prog_StepParam WHERE Step_Number=3;"));
        Assert.Equal(7, await db.DecimalAsync("SELECT Setpoint FROM dbo.Prog_Recipes WHERE Step_Number=3;"));
        Assert.Equal(0, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Prog_StepTransition WHERE Seq_Record=1 AND SeqStep_Number=1;"));
        Assert.Equal(3, await db.DecimalAsync("SELECT TransDest_01 FROM dbo.Prog_StepTransition WHERE Seq_Record=1 AND SeqStep_Number=2;"));
        Assert.Equal(3, await db.DecimalAsync("SELECT TransDest_02 FROM dbo.Prog_StepTransition WHERE Seq_Record=1 AND SeqStep_Number=2;"));
        Assert.Equal(1, await db.DecimalAsync("SELECT TransDest_01 FROM dbo.Prog_StepTransition WHERE Seq_Record=2 AND SeqStep_Number=2;"));
    }

}
