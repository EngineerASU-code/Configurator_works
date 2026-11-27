using System.Data;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator;

public sealed partial class ProgramManagementRepository
{
    internal async Task<IReadOnlyList<ChangeJournalRowChange>> SwapStepAssignmentsAsync(ProgramContext context,
        IReadOnlyDictionary<string, object> first, IReadOnlyDictionary<string, object> second, CancellationToken ct = default)
    {
        var scope = ProgramScope(context, includeSequence: true);
        var changes = new List<ChangeJournalRowChange>();
        if (_offline != null)
            await _offline.ExecuteMutationTransactionAsync(() => Swap(null, null), ct);
        else
        {
            using var connection = SqlProviderGuard.CreateConnection(_connectionString);
            await SqlProviderGuard.OpenConnectionAsync(connection, ct);
            using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Swap(connection, transaction);
            await transaction.CommitAsync(ct);
        }
        return changes;

        async Task Swap(SqlConnection connection, SqlTransaction transaction)
        {
            Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> Read(IReadOnlyDictionary<string, object> filter)
                => _offline != null ? Task.FromResult(SnapshotOfflineRows(TransitionTable, filter))
                    : ReadLockedRowsAsync(connection, transaction, TransitionTable, filter, ct);
            var schema = _offline != null ? GetOfflineSchema(TransitionTable)
                : await OnlineProjectDataSource.ReadSchemaAsync(connection, transaction, TransitionTable, ct);
            if (schema?.HasStableKey != true || !schema.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(UniqueKeyColumns[TransitionTable]))
                throw new InvalidOperationException("Не подтверждён ключ позиции шага.");
            int a = Convert.ToInt32(first["SeqStep_Number"]), b = Convert.ToInt32(second["SeqStep_Number"]);
            var between = (await Read(scope)).Where(row =>
            {
                int n = Convert.ToInt32(row["SeqStep_Number"]);
                return n >= Math.Min(a, b) && n <= Math.Max(a, b);
            }).ToArray();
            if (a == b || between.Length != 2) throw new InvalidOperationException("Порядок шагов изменился. Обновите последовательность.");
            EnsureSnapshotMatches(new[] { first, second }, between);
            foreach (var (before, source) in new[] { (first, second), (second, first) })
            {
                var fields = new[] { "PLC_Class_Step_Number", "Step_Record", "Name_L1", "Name_L2", "Description_L1", "Description_L2",
                    "NameL1", "NameL2", "DescriptionL1", "DescriptionL2" }
                    .Where(name => before.ContainsKey(name) && !ExactValueEquals(before[name], source[name]))
                    .ToDictionary(name => name, name => source[name]);
                if (fields.Count == 0) continue;
                var key = ContextReplacementKey(before, schema.KeyColumns);
                if (_offline != null)
                    await _offline.UpdateRecordAsync(TransitionTable, schema.KeyColumns, key, fields, ct);
                else
                {
                    var where = BuildPredicates(key, "key", out var parameters);
                    var assignments = fields.Keys.Select((name, i) => $"{QuoteIdentifier(name)} = @v{i}");
                    using var cmd = new SqlCommand($"UPDATE {QuoteTable(TransitionTable)} SET {string.Join(", ", assignments)} WHERE {where}", connection, transaction);
                    AddParameters(cmd, parameters);
                    int i = 0;
                    foreach (var value in fields.Values) cmd.Parameters.AddWithValue("@v" + i++, value ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                var after = new Dictionary<string, object>(before);
                foreach (var field in fields) after[field.Key] = field.Value;
                EnsureSnapshotMatches(new[] { after }, await Read(key));
                changes.Add(ContextReplacementJournal(TransitionTable, schema.KeyColumns, before, after));
            }
        }
    }
}
