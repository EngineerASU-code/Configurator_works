#nullable enable
using System.Data;
using Microsoft.Data.SqlClient;
using Configurator.Core.Projects;

namespace Configurator;

public sealed partial class ProgramManagementRepository
{
    internal sealed record ContextReplacementPreview(
        string Table,
        string ReferenceColumn,
        Dictionary<string, object> SourceKey,
        IReadOnlyDictionary<string, object> Source,
        IReadOnlyList<IReadOnlyDictionary<string, object>> Candidates,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>> Dependencies);

    private static readonly string[] SequenceReplacementTables =
    {
        TransitionTable, ParameterTable, "dbo.Prog_Recipe", "dbo.Prog_Recipes", "dbo.Prog_QueueSelections"
    };

    internal async Task<ContextReplacementPreview> PrepareContextReplacementAsync(ProgramContext context, object? sequenceRecord,
        IReadOnlyDictionary<string, object> expected, CancellationToken ct = default)
    {
        var scope = ProgramScope(context, includeSequence: false);
        bool isSequence = sequenceRecord != null;
        var table = isSequence ? SequenceTable : ProgramTable;
        var record = sequenceRecord ?? context.RecordValue;
        var referenceColumn = isSequence ? "Seq_Record" : "Prog_Record";
        var ownerScope = isSequence ? scope : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Number"] = context.ClassNumberValue
        };
        var sourceKey = new Dictionary<string, object>(ownerScope, StringComparer.OrdinalIgnoreCase) { ["Record"] = record };
        var roots = await ReadContextReplacementRowsAsync(table, ownerScope, ct);
        var source = roots.SingleOrDefault(row => ExactValueEquals(row["Record"], record));
        if (source == null)
            throw new InvalidOperationException("Строка для удаления не найдена в текущем контексте.");
        EnsureSnapshotMatches(new[] { expected }, new[] { source });

        var candidates = roots.Where(row => !ExactValueEquals(row["Record"], record)).ToArray();
        var dependentTables = isSequence
            ? SequenceReplacementTables
            : new[] { SequenceTable, TransitionTable, ParameterTable }
                .Concat(ProgramSupportCatalog.ProgramTables.Select(definition => definition.TableName));
        var dependencyScope = new Dictionary<string, object>(scope, StringComparer.OrdinalIgnoreCase) { [referenceColumn] = record };
        var dependencies = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependent in dependentTables)
            dependencies.Add(dependent, await ReadContextReplacementRowsAsync(dependent, dependencyScope, ct));

        return new ContextReplacementPreview(table, referenceColumn, sourceKey, source, candidates, dependencies);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> ReadContextReplacementRowsAsync(
        string table, IReadOnlyDictionary<string, object> scope, CancellationToken ct) =>
        _offline != null ? SnapshotOfflineRows(table, scope) : SnapshotRows(await GetScopedTableAsync(table, scope, ct, readAll: true));

    internal async Task<IReadOnlyList<ChangeJournalRowChange>> ReplaceAndDeleteContextAsync(ContextReplacementPreview preview,
        object? replacementRecord, CancellationToken ct = default)
    {
        bool mustReplace = preview.Dependencies.Values.Any(rows => rows.Count > 0);
        if (mustReplace != (replacementRecord != null))
            throw new InvalidOperationException("Выберите запись для замены зависимостей.");
        var replacement = replacementRecord == null ? null : preview.Candidates.SingleOrDefault(
            row => ExactValueEquals(row["Record"], replacementRecord));
        if (mustReplace && replacement == null)
            throw new InvalidOperationException("Выбранная замена отсутствует в подтверждённом контексте.");
        var source = preview.SourceKey;
        var replacementKey = new Dictionary<string, object>(source, StringComparer.OrdinalIgnoreCase);
        if (replacementRecord != null) replacementKey["Record"] = replacementRecord;
        var dependentScope = preview.Table == SequenceTable
            ? source.Where(pair => pair.Key != "Record").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["PLC"] = source["PLC"],
                ["PLC_Class_Prog_Number"] = source["PLC_Class_Number"]
            };
        dependentScope[preview.ReferenceColumn] = source["Record"];

        if (_offline != null)
        {
            IReadOnlyList<ChangeJournalRowChange> changes = Array.Empty<ChangeJournalRowChange>();
            await _offline.ExecuteMutationTransactionAsync(async () =>
            {
                changes = await ApplyContextReplacementAsync(preview, replacement, replacementKey, dependentScope, null, null, null, ct);
            }, ct);
            return changes;
        }

        var schema = await new DatabaseArchitectureAnalyzer(_connectionString).AnalyzeAsync(ct);
        // The legacy graph rule uses Record alone; the queue scope is verified below.
        if (preview.Table == SequenceTable)
            schema.Relations.RemoveAll(relation => relation is { Kind: Core.Architecture.DatabaseRelationKind.ContextualProgramReference, RuleId: "program-sequence-selection" });
        var protection = new DeletionProtectionService(_connectionString, schema);
        using var connection = SqlProviderGuard.CreateConnection(_connectionString);
        await SqlProviderGuard.OpenConnectionAsync(connection, ct);
        using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var changes = await ApplyContextReplacementAsync(preview, replacement, replacementKey, dependentScope, connection, transaction, protection, ct);
            await transaction.CommitAsync(ct);
            return changes;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* Preserve the original failure. */ }
            throw;
        }
    }

    private async Task<IReadOnlyList<ChangeJournalRowChange>> ApplyContextReplacementAsync(ContextReplacementPreview preview,
        IReadOnlyDictionary<string, object>? replacement, Dictionary<string, object> replacementKey,
        Dictionary<string, object> dependentScope, SqlConnection? connection, SqlTransaction? transaction, DeletionProtectionService? protection, CancellationToken ct)
    {
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> Read(string table, IReadOnlyDictionary<string, object> scope, bool optional = false)
        {
            if (_offline != null) return SnapshotOfflineRows(table, scope);
            if (optional && !await HasScopedRowsAsync(connection!, transaction!, table, scope, ct))
                return Array.Empty<IReadOnlyDictionary<string, object>>();
            return await ReadLockedRowsAsync(connection!, transaction!, table, scope, ct);
        }

        async Task<IReadOnlyList<string>> VerifyKey(string table)
        {
            ProjectWriteGuard.EnsureDataTableWritable(table);
            if (_offline != null)
            {
                var schema = GetOfflineSchema(table);
                if (schema.HasStableKey && schema.KeyColumns.Count > 0) return schema.KeyColumns;
                throw new InvalidOperationException($"В снимке отсутствует стабильный ключ «{table}».");
            }

            IReadOnlyList<string> keys = UniqueKeyColumns.TryGetValue(table, out var known)
                ? known
                : ProgramSupportCatalog.ProgramTables.Single(item => item.TableName == table).KeyColumns;
            await EnsureUniqueIndexAsync(connection!, transaction!, table, keys, ct);
            await EnsureSafeReplacementSqlAsync(connection!, transaction!, table, ct);
            return keys;
        }

        var rootKeys = await VerifyKey(preview.Table);
        EnsureSnapshotMatches(new[] { preview.Source }, await Read(preview.Table, preview.SourceKey));
        if (replacement != null)
            EnsureSnapshotMatches(new[] { replacement }, await Read(preview.Table, replacementKey));
        foreach (var dependent in preview.Dependencies)
            EnsureSnapshotMatches(dependent.Value, await Read(dependent.Key, dependentScope, optional: true));

        var changes = new List<ChangeJournalRowChange>();
        foreach (var dependent in preview.Dependencies.Where(pair => pair.Value.Count > 0))
        {
            var table = dependent.Key;
            var keys = await VerifyKey(table);
            var newRecord = replacementKey["Record"];
            var assignment = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [preview.ReferenceColumn] = newRecord };
            foreach (var before in dependent.Value)
            {
                var key = ContextReplacementKey(before, keys);
                var oldRecord = before[preview.ReferenceColumn];
                var newValue = Convert.ChangeType(newRecord, oldRecord.GetType(), System.Globalization.CultureInfo.InvariantCulture);
                assignment[preview.ReferenceColumn] = newValue;
                var expectedAfter = new Dictionary<string, object>(before, StringComparer.OrdinalIgnoreCase) { [preview.ReferenceColumn] = newValue };
                var newKey = ContextReplacementKey(expectedAfter, keys);
                if (keys.Contains(preview.ReferenceColumn, StringComparer.OrdinalIgnoreCase) && (await Read(table, newKey)).Count != 0)
                    throw new InvalidOperationException($"В «{table}» уже есть строка с таким составным ключом.");
                if (_offline != null)
                    await _offline.UpdateRecordAsync(table, keys, key.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase), assignment, ct);
                else
                    await WriteContextReplacementSqlAsync(connection!, transaction!, table, key, preview.ReferenceColumn, newValue, ct);
                var actualAfter = await Read(table, newKey);
                EnsureSnapshotMatches(new[] { expectedAfter }, actualAfter);
                changes.Add(ContextReplacementJournal(table, keys, before, actualAfter[0]));
            }
        }

        var rootKey = ContextReplacementKey(preview.Source, rootKeys);
        if (_offline != null)
        {
            OfflineObjectDeletionGuard.Check(_offline, preview.Table,
                new[] { preview.Source.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase) },
                relation => !(preview.Table == SequenceTable &&
                    relation.Origin == DatabaseGraphRelationOrigin.Inferred &&
                    relation.Source.Equals("dbo.Prog_QueueSelections", StringComparison.OrdinalIgnoreCase) &&
                    relation.SourceColumns.SequenceEqual(new[] { "Seq_Record" }, StringComparer.OrdinalIgnoreCase) &&
                    relation.TargetColumns.SequenceEqual(new[] { "Record" }, StringComparer.OrdinalIgnoreCase)));
            var key = rootKey.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
            if (await _offline.DeleteRecordsAsync(preview.Table, rootKeys, new[] { key }, ct) != 1)
                throw new InvalidOperationException("Удаление исходной записи не подтверждено.");
        }
        else
            await protection!.DeleteUnreferencedInTransactionAsync(connection!, transaction!, preview.Table, rootKey, preview.Source, ct);
        EnsureSnapshotMatches(Array.Empty<IReadOnlyDictionary<string, object>>(), await Read(preview.Table, preview.SourceKey));
        changes.Add(ContextReplacementJournal(preview.Table, rootKeys, preview.Source, null));
        return changes;
    }

    private static Dictionary<string, object> ContextReplacementKey(IReadOnlyDictionary<string, object> row, IReadOnlyList<string> keys)
        => keys.ToDictionary(key => key, key => row[key], StringComparer.OrdinalIgnoreCase);

    private static ChangeJournalRowChange ContextReplacementJournal(string table, IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, object> before, IReadOnlyDictionary<string, object>? after)
    {
        static Dictionary<string, ChangeJournalValue> Snapshot(IReadOnlyDictionary<string, object> values) =>
            values.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase);
        return new ChangeJournalRowChange
        {
            TableName = table,
            Kind = after == null ? ChangeJournalOperationKind.Delete : ChangeJournalOperationKind.Update,
            Before = Snapshot(before),
            After = after == null ? new(StringComparer.OrdinalIgnoreCase) : Snapshot(after),
            IdentityBefore = Snapshot(ContextReplacementKey(before, keys)),
            IdentityAfter = after == null ? new(StringComparer.OrdinalIgnoreCase) : Snapshot(ContextReplacementKey(after, keys))
        };
    }

    private static async Task WriteContextReplacementSqlAsync(SqlConnection connection, SqlTransaction transaction,
        string table, IReadOnlyDictionary<string, object> key, string column, object? value, CancellationToken ct)
    {
        var where = BuildPredicates(key, "key", out var parameters);
        using var command = new SqlCommand($"UPDATE {QuoteTable(table)} SET {QuoteIdentifier(column)} = @replacement WHERE {where}", connection, transaction);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@replacement", value ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException($"Изменение строки «{table}» не подтверждено.");
    }
}
