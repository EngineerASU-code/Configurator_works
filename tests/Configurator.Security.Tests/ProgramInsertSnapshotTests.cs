using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Configurator;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProgramInsertSnapshotTests
{
    [Fact]
    public async Task OfflineProgramInsertsCaptureCompleteRowsThatCanBeUndone()
    {
        var project = new ConfiguratorProject { Manifest = new ConfiguratorProjectManifest { ProjectId = "insert-snapshot-test" } };
        project.TableSchemas.Add(Schema(ProgramManagementRepository.ProgramTable,
            ["Record", "PLC", "PLC_Class_Number"], ["Label"]));
        project.TableSchemas.Add(Schema(ProgramManagementRepository.SequenceTable,
            ["Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record"], ["Label"]));
        project.TableSchemas.Add(Schema(ProgramManagementRepository.TransitionTable,
            ["PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number"],
            ["PLC_Class_Step_Number", "Step_Record", "Label"]));
        project.TableSchemas.Add(Schema(ProgramManagementRepository.ParameterTable,
            ["PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Step_Number", "Type", "Record"], ["Label"]));

        var source = new OfflineProjectDataSource(project);
        var repository = new ProgramManagementRepository(source);
        var context = new ProgramContext { PlcValue = 1, ClassNumberValue = 10, RecordValue = 3 };
        var inserted = new List<(string Table, ProgramInsertSnapshot Snapshot)>();
        inserted.Add((ProgramManagementRepository.ProgramTable,
            await repository.AddProgramWithSnapshotAsync(1, 10, new Dictionary<string, object> { ["Label"] = "program" })));
        var sequence = await repository.AddSequenceWithSnapshotAsync(context, new Dictionary<string, object> { ["Label"] = "sequence" });
        inserted.Add((ProgramManagementRepository.SequenceTable, sequence));
        context.SeqRecordValue = sequence.GeneratedValue;
        inserted.Add((ProgramManagementRepository.TransitionTable,
            await repository.AddStepToSequenceWithSnapshotAsync(context, 7, 9, new Dictionary<string, object> { ["Label"] = "step" })));
        inserted.Add((ProgramManagementRepository.ParameterTable,
            await repository.AddParameterWithSnapshotAsync(context, 1,
                new Dictionary<string, object> { ["Type"] = 2, ["Label"] = "parameter" })));

        var journal = new ChangeJournalService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        foreach (var (table, snapshot) in inserted.AsEnumerable().Reverse())
        {
            Assert.True(snapshot.IsCompleteOfflineRecord);
            var identityColumns = project.TableSchemas.Single(schema => schema.TableName == table).KeyColumns;
            var identity = identityColumns.ToDictionary(key => key, key => snapshot.Values[key], StringComparer.OrdinalIgnoreCase);
            var entry = new ChangeJournalEntry
            {
                ProjectType = "Offline",
                ProjectId = project.Manifest.ProjectId,
                OperationKind = ChangeJournalOperationKind.Insert,
                Changes = new List<ChangeJournalRowChange>
                {
                    new()
                    {
                        TableName = table,
                        Kind = ChangeJournalOperationKind.Insert,
                        After = snapshot.Values.ToDictionary(pair => pair.Key,
                            pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase),
                        IdentityAfter = identity.ToDictionary(pair => pair.Key,
                            pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase),
                        AfterIsCompleteOfflineRecord = true
                    }
                }
            };
            Assert.True(entry.CanRestore);
            journal.RestoreOffline(entry, project, source.InvalidateIndex);
            Assert.Empty(source.GetRecords(table));
        }
    }

    private static ProjectTableSchema Schema(string table, string[] keys, string[] fields)
    {
        var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
        schema.KeyColumns.AddRange(keys);
        foreach (var name in keys)
            schema.Columns.Add(new ProjectColumnSchema { Name = name, DataType = "int", IsNullable = false });
        foreach (var name in fields)
            schema.Columns.Add(new ProjectColumnSchema
            {
                Name = name,
                DataType = name == "Label" ? "nvarchar" : "int",
                IsNullable = true,
                MaxLength = 120
            });
        return schema;
    }
}
