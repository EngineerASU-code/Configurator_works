using Configurator.Core.Projects;

namespace Configurator;

public sealed record ProgramStepMoveJournalResult(int NewStepNumber, IReadOnlyList<ChangeJournalRowChange> Changes, string RestorePayloadJson);

public sealed class ProgramStepMoveRestorePayload
{
    public int Version { get; init; } = 1;
    public Dictionary<string, ChangeJournalValue> Scope { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ProgramStepMoveTableSnapshot> Tables { get; init; } = new();
}

public sealed class ProgramStepMoveTableSnapshot
{
    public string TableName { get; init; } = "";
    public List<Dictionary<string, ChangeJournalValue>> Rows { get; init; } = new();
}

public sealed partial class ProgramManagementRepository
{
    internal static IReadOnlyList<ChangeJournalRowChange> BuildStepMoveJournalChanges(
        IReadOnlyList<(string Name, string Field)> tables,
        IReadOnlyDictionary<string, ProjectTableSchema> schemas,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>> beforeRows,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>> afterRows,
        int first, int second)
    {
        var changes = new List<ChangeJournalRowChange>();
        foreach (var (table, field) in tables)
        {
            var schema = schemas[table];
            var keys = schema.KeyColumns;
            var stableKeys = keys.Where(key => !key.Equals(field, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!schema.HasStableKey || !keys.Contains(field, StringComparer.OrdinalIgnoreCase) || stableKeys.Length == 0)
                throw new InvalidOperationException($"Не удалось сформировать обратимый снимок {table}: ключ не подтверждён.");

            var remaining = afterRows[table].ToList();
            foreach (var before in beforeRows[table])
            {
                if (!before.TryGetValue(field, out var previousNumber))
                    throw new InvalidOperationException($"В снимке {table} отсутствует поле {field}.");
                int prior = Convert.ToInt32(previousNumber, System.Globalization.CultureInfo.InvariantCulture);
                int expectedNumber = prior == first ? second : prior == second ? first : prior;
                var candidates = remaining.Where(after =>
                    after.TryGetValue(field, out var currentNumber) &&
                    Convert.ToInt32(currentNumber, System.Globalization.CultureInfo.InvariantCulture) == expectedNumber &&
                    stableKeys.All(key => before.TryGetValue(key, out var oldValue) && after.TryGetValue(key, out var newValue) && ExactValueEquals(oldValue, newValue)))
                    .ToArray();
                if (candidates.Length != 1)
                    throw new InvalidOperationException($"Не удалось однозначно сопоставить снимки {table} при перемещении.");

                var after = candidates[0];
                remaining.Remove(after);
                if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var value) || !ExactValueEquals(pair.Value, value)))
                {
                    Dictionary<string, ChangeJournalValue> Snapshot(IReadOnlyDictionary<string, object> row)
                        => row.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, ChangeJournalValue> Identity(IReadOnlyDictionary<string, object> row)
                        => keys.ToDictionary(key => key, key => ChangeJournalValue.FromObject(row[key]), StringComparer.OrdinalIgnoreCase);
                    changes.Add(new ChangeJournalRowChange
                    {
                        TableName = table,
                        Kind = ChangeJournalOperationKind.Update,
                        Before = Snapshot(before),
                        After = Snapshot(after),
                        IdentityBefore = Identity(before),
                        IdentityAfter = Identity(after)
                    });
                }
            }
            if (remaining.Count != 0)
                throw new InvalidOperationException($"Состав строк {table} изменился во время перемещения.");
        }
        return changes;
    }

    internal static string BuildStepMoveRestorePayload(
        IReadOnlyList<(string Name, string Field)> tables,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>> afterRows)
    {
        var payload = new ProgramStepMoveRestorePayload
        {
            Scope = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record" }
                .ToDictionary(name => name, name => ChangeJournalValue.FromObject(afterRows[tables[0].Name][0][name]), StringComparer.OrdinalIgnoreCase),
            Tables = tables.Select(table => new ProgramStepMoveTableSnapshot
            {
                TableName = table.Name,
                Rows = afterRows[table.Name].Select(row => row.ToDictionary(pair => pair.Key,
                    pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase)).ToList()
            }).ToList()
        };
        return Newtonsoft.Json.JsonConvert.SerializeObject(payload);
    }
}
