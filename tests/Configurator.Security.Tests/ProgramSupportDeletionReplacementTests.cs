#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProgramSupportDeletionReplacementTests
{
    [Fact]
    public async Task OfflineRecipeDeletionStagesReplacementAndJournalRestoresIt()
    {
        var project = new ConfiguratorProject { Manifest = new ConfiguratorProjectManifest { Name = "recipes" } };
        var recipes = AddSchema("recipes", "Type");
        var values = AddSchema("recipe-values", "Setpoint");
        project.LocalRecords.AddRange(new[]
        {
            Row(recipes, 1, 1, 10, 3, 1, 0),
            Row(recipes, 2, 1, 10, 3, 1, 0),
            Row(recipes, 3, 1, 10, 3, 2, 0),
            Row(values, 7, 1, 10, 3, 1, 1, 0, 0, 125)
        });
        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramSupportRepository(source);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3, SeqRecordValue = 1 };
        var data = await repository.LoadProgramAsync("recipes", context);
        var scope = ProgramSupportRepository.GetProgramScope(data.Definition, context);
        var target = data.Rows.Rows.Cast<DataRow>().Single(row => (int)row["Record"] == 1);
        ChangeJournalEntry? entry = null;
        repository.ChangeJournalRequested += saved => entry = saved;

        await StageAsync(repository, data, target, scope);
        Assert.True(ProgramSupportRepository.HasStagedDeletionReplacements(data));
        Assert.Equal(3, source.GetRecords(recipes.TableName).Count);
        Assert.Equal(1, (int)Assert.Single(source.GetRecords(values.TableName)).Values["Recipe_Record"]!);
        Assert.Null(entry);

        await repository.SaveAsync(data, scope);
        Assert.DoesNotContain(source.GetRecords(recipes.TableName), row => (int)row.Values["Record"]! == 1);
        var moved = Assert.Single(source.GetRecords(values.TableName));
        Assert.Equal(2, (int)moved.Values["Recipe_Record"]!);
        Assert.Equal(125, (int)moved.Values["Setpoint"]!);

        Assert.NotNull(entry);
        Assert.Equal(ChangeJournalOperationKind.Composite, entry.OperationKind);
        Assert.Equal(new[] { ChangeJournalOperationKind.Update, ChangeJournalOperationKind.Delete }, entry.Changes.Select(change => change.Kind));
        new ChangeJournalService().RestoreOffline(entry, project, source.InvalidateIndex);
        Assert.Equal(3, source.GetRecords(recipes.TableName).Count);
        Assert.Equal(1, (int)Assert.Single(source.GetRecords(values.TableName)).Values["Recipe_Record"]!);

        ProjectTableSchema AddSchema(string id, string extraColumn)
        {
            var definition = ProgramSupportCatalog.ProgramTables.Single(item => item.Id == id);
            var schema = new ProjectTableSchema { TableName = definition.TableName, HasStableKey = true };
            schema.KeyColumns.AddRange(definition.KeyColumns);
            foreach (var name in definition.KeyColumns.Append(extraColumn))
                schema.Columns.Add(new ProjectColumnSchema { Name = name, DataType = "int" });
            project.TableSchemas.Add(schema);
            return schema;
        }
    }

    [CompareLocalDbFact]
    public async Task OnlineMatrixCommandDeletionRollsBackStaleReplacementAndCanBeRestored()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Matrix_CommandList (
                Record int NOT NULL, PLC int NOT NULL, PLC_Class_Number int NOT NULL, CmdNumber int NOT NULL,
                Name_L1 nvarchar(40) NOT NULL,
                PRIMARY KEY (Record, PLC, PLC_Class_Number, CmdNumber));
            CREATE TABLE dbo.Matrix_Commands (
                Record int NOT NULL, PLC int NOT NULL, PLC_Class_Number int NOT NULL, Position int NOT NULL,
                CmdNumber int NOT NULL, Cmd nvarchar(40) NOT NULL,
                PRIMARY KEY (Record, PLC, PLC_Class_Number, Position, CmdNumber),
                FOREIGN KEY (Record, PLC, PLC_Class_Number, CmdNumber)
                    REFERENCES dbo.Matrix_CommandList (Record, PLC, PLC_Class_Number, CmdNumber));
            INSERT dbo.Matrix_CommandList VALUES (1,1,10,1,N'old'),(1,1,10,2,N'new'),(2,1,10,3,N'unrelated');
            INSERT dbo.Matrix_Commands VALUES (1,1,10,4,1,N'code');
            """);
        var repository = new ProgramSupportRepository(db.ConnectionString);
        var data = await repository.LoadMatrixAsync("matrix-command-list", 1, 10, 1);
        var scope = new Dictionary<string, object> { ["PLC"] = 1, ["PLC_Class_Number"] = 10, ["Record"] = 1 };
        var target = data.Rows.Rows.Cast<DataRow>().Single(row => (int)row["CmdNumber"] == 1);
        ChangeJournalEntry? entry = null;
        repository.ChangeJournalRequested += saved => entry = saved;
        await StageAsync(repository, data, target, scope);

        await db.ExecuteAsync("INSERT dbo.Matrix_Commands VALUES (1,1,10,4,2,N'conflict')");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(data, scope));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Matrix_CommandList WHERE Record=1 AND CmdNumber=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Matrix_Commands WHERE CmdNumber=1 AND Cmd=N'code'"));
        Assert.Null(entry);

        await db.ExecuteAsync("""
            DELETE dbo.Matrix_Commands WHERE CmdNumber=2;
            CREATE TABLE dbo.CommandAuditRef (
                Record int NOT NULL, PLC int NOT NULL, PLC_Class_Number int NOT NULL, CmdNumber int NOT NULL,
                FOREIGN KEY (Record, PLC, PLC_Class_Number, CmdNumber)
                    REFERENCES dbo.Matrix_CommandList (Record, PLC, PLC_Class_Number, CmdNumber));
            INSERT dbo.CommandAuditRef VALUES (1,1,10,1);
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(data, scope));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.CommandAuditRef WHERE CmdNumber=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Matrix_Commands WHERE CmdNumber=1 AND Cmd=N'code'"));
        Assert.Null(entry);

        await db.ExecuteAsync("DELETE dbo.CommandAuditRef");
        await repository.SaveAsync(data, scope);
        Assert.Equal(0m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Matrix_CommandList WHERE Record=1 AND CmdNumber=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Matrix_Commands WHERE CmdNumber=2 AND Cmd=N'code'"));
        Assert.NotNull(entry);
        Assert.Equal(ChangeJournalOperationKind.Composite, entry.OperationKind);

        await new ChangeJournalService().RestoreAsync(entry, db.ConnectionString);
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Matrix_CommandList WHERE Record=1 AND CmdNumber=1"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Matrix_Commands WHERE CmdNumber=1 AND Cmd=N'code'"));
    }

    private static async Task StageAsync(ProgramSupportRepository repository, ProgramSupportTableData data,
        DataRow row, IReadOnlyDictionary<string, object> scope)
    {
        var plan = await repository.PrepareDeletionReplacementAsync(data, row, scope);
        var option = Assert.Single(plan.Options);
        var candidate = Assert.Single(option.Candidates, item => !item.IsConflict);
        var selected = new ReferenceReplacement
        {
            SourceTable = option.Hit.SourceTable,
            SourceColumns = option.Hit.Relation.SourceColumns,
            KeyColumns = option.Hit.SourceKey.Keys.ToArray(),
            KeyValues = option.Hit.SourceKey.Values.ToArray(),
            NewValues = candidate.Values,
            TargetValues = candidate.TargetValues,
            ExpectedOldValues = option.Hit.SourceReferenceValues,
            AdditionalAssignments = candidate.AutoAssignments
        };
        repository.StageDeletionReplacement(data, row, plan, new[] { selected });
        row.Delete();
    }

    private static ProjectRecord Row(ProjectTableSchema schema, params int[] values)
    {
        var json = new JsonObject();
        foreach (var (column, value) in schema.Columns.Zip(values))
            json[column.Name] = value;
        return new ProjectRecord { TableName = schema.TableName, Values = json };
    }
}
