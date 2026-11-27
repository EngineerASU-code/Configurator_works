using System.Data;
using System.Collections;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator;

public sealed partial class ProgramManagementRepository
{
    public async Task<int> MoveStepAsync(ProgramContext context, IReadOnlyDictionary<string, object> original,
        int direction, CancellationToken ct = default)
        => (await MoveStepWithJournalAsync(context, original, direction, ct)).NewStepNumber;

    public Task<ProgramStepMoveJournalResult> MoveStepWithJournalAsync(ProgramContext context,
        IReadOnlyDictionary<string, object> original, int direction, CancellationToken ct = default)
        => _offline != null
            ? MoveOfflineStepWithJournalAsync(context, original, direction, ct)
            : MoveOnlineStepWithJournalAsync(context, original, direction, ct);

    private async Task<ProgramStepMoveJournalResult> MoveOnlineStepWithJournalAsync(ProgramContext context, IReadOnlyDictionary<string, object> original,
        int direction, CancellationToken ct)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var scope = ProgramScope(context, includeSequence: true);
        var tables = new[] { (Name: TransitionTable, Field: "SeqStep_Number"),
            (Name: ParameterTable, Field: "Step_Number"), (Name: "dbo.Prog_Recipes", Field: "Step_Number") };
        var destinations = Enumerable.Range(1, 8).Select(i => $"TransDest_{i:00}").ToArray();
        using var connection = SqlProviderGuard.CreateConnection(_connectionString);
        await SqlProviderGuard.OpenConnectionAsync(connection, ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var where = BuildPredicates(scope, "scope", out var scopeParameters);
        SqlCommand ScopedCommand(string sql)
        {
            var command = new SqlCommand(sql, connection, transaction);
            AddParameters(command, scopeParameters);
            return command;
        }

        using (var metadata = new SqlCommand("""
            SELECT QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)
            FROM sys.tables t
            WHERE (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id=t.object_id
                    AND c.name IN ('PLC','PLC_Class_Prog_Number','Prog_Record','Seq_Record'))=4
              AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=t.object_id
                    AND c.name IN ('SeqStep_Number','Step_Number'))
              AND t.object_id NOT IN (OBJECT_ID('dbo.Prog_StepTransition'),OBJECT_ID('dbo.Prog_StepParam'),OBJECT_ID('dbo.Prog_Recipes'));
            """, connection, transaction))
        {
            if (await metadata.ExecuteScalarAsync(ct) is string unknown)
                throw new InvalidOperationException($"Перемещение заблокировано: не подтверждены ссылки в {unknown}.");
        }

        using (var triggers = new SqlCommand("""
            SELECT TOP (1) tr.name FROM sys.triggers tr
            JOIN sys.trigger_events e ON e.object_id=tr.object_id
            WHERE tr.is_disabled=0 AND e.type_desc='UPDATE'
              AND NOT (tr.object_id=ISNULL(OBJECT_ID('dbo.Prog_Recipes_Upd'),-1)
                       AND tr.parent_id=OBJECT_ID('dbo.Prog_Recipes') AND tr.is_instead_of_trigger=0)
              AND tr.parent_id IN
                (OBJECT_ID('dbo.Prog_StepTransition'),OBJECT_ID('dbo.Prog_StepParam'),OBJECT_ID('dbo.Prog_Recipes'));
            """, connection, transaction))
        {
            if (await triggers.ExecuteScalarAsync(ct) is string trigger)
                throw new InvalidOperationException($"Перемещение заблокировано: активен UPDATE-триггер {trigger}.");
        }

        var schemas = new Dictionary<string, ProjectTableSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, field) in tables)
        {
            var schema = await OnlineProjectDataSource.ReadSchemaAsync(connection, transaction, name, ct);
            if (schema == null || !schema.HasStableKey || !schema.KeyColumns.Contains(field, StringComparer.OrdinalIgnoreCase)
                || scope.Keys.Any(key => !schema.KeyColumns.Contains(key, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Перемещение заблокировано: ключ {name} не подтверждён.");
            schemas[name] = schema;
            var required = name == TransitionTable ? destinations.Append(field) : new[] { field };
            foreach (string columnName in required)
            {
                var column = schema.Columns.FirstOrDefault(item => item.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                if (column == null || column.DataType.ToLowerInvariant() is not ("int" or "smallint" or "tinyint") ||
                    column.IsIdentity || column.IsComputed || column.IsReadOnly)
                    throw new InvalidOperationException($"Перемещение заблокировано: поле {name}.{columnName} не подтверждено.");
            }
        }

        using (var foreignKey = new SqlCommand("""
            SELECT TOP (1) fk.name FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns f ON f.constraint_object_id=fk.object_id
            JOIN sys.columns c ON (c.object_id=f.parent_object_id AND c.column_id=f.parent_column_id)
                               OR (c.object_id=f.referenced_object_id AND c.column_id=f.referenced_column_id)
            WHERE fk.is_disabled=0 AND
              ((c.object_id=OBJECT_ID('dbo.Prog_StepTransition') AND
                c.name IN ('SeqStep_Number','TransDest_01','TransDest_02','TransDest_03','TransDest_04',
                           'TransDest_05','TransDest_06','TransDest_07','TransDest_08'))
               OR (c.object_id IN (OBJECT_ID('dbo.Prog_StepParam'),OBJECT_ID('dbo.Prog_Recipes')) AND c.name='Step_Number'));
            """, connection, transaction))
        {
            if (await foreignKey.ExecuteScalarAsync(ct) is string key)
                throw new InvalidOperationException($"Перемещение заблокировано: поле используется внешним ключом {key}.");
        }

        var numbers = new List<int>();
        var matches = new List<int>();
        using (var read = ScopedCommand($"SELECT * FROM {QuoteTable(TransitionTable)} WITH (UPDLOCK,HOLDLOCK) WHERE {where} ORDER BY [SeqStep_Number]"))
        await using (var rows = await read.ExecuteReaderAsync(ct))
        {
            var columns = Enumerable.Range(0, rows.FieldCount).ToDictionary(rows.GetName, StringComparer.OrdinalIgnoreCase);
            if (original.Count != columns.Count || original.Keys.Any(key => !columns.ContainsKey(key)))
                throw new InvalidOperationException("Нужна полная исходная строка вхождения.");
            while (await rows.ReadAsync(ct))
            {
                int number = Convert.ToInt32(rows.GetValue(columns["SeqStep_Number"]));
                numbers.Add(number);
                if (original.All(pair => StructuralComparisons.StructuralEqualityComparer.Equals(rows.GetValue(columns[pair.Key]), pair.Value ?? DBNull.Value)))
                    matches.Add(number);
            }
        }
        if (matches.Count != 1 || numbers.Any(number => number <= 0) || numbers.Distinct().Count() != numbers.Count)
            throw new InvalidOperationException("Исходное вхождение изменилось, либо номера вхождений неоднозначны.");
        int first = matches[0];
        int adjacent = numbers.IndexOf(first) + direction;
        if (adjacent < 0 || adjacent >= numbers.Count)
            throw new InvalidOperationException("Вхождение уже находится на границе списка.");
        int second = numbers[adjacent];
        var beforeRows = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in tables)
            beforeRows[name] = await ReadLockedRowsAsync(connection, transaction, name, scope, ct);
        int maximum = 0;
        foreach (var (name, field) in tables)
        {
            using var max = ScopedCommand($"SELECT MAX({QuoteIdentifier(field)}) FROM {QuoteTable(name)} WITH (UPDLOCK,HOLDLOCK) WHERE {where}");
            if (await max.ExecuteScalarAsync(ct) is { } value && value != DBNull.Value)
                maximum = Math.Max(maximum, Convert.ToInt32(value));
        }
        int temporary = checked(maximum + 1);

        foreach (var (name, field) in tables)
        {
            if (IsSharedStepParameterSchema(schemas[name])) continue;
            string column = QuoteIdentifier(field);
            using var update = ScopedCommand($"UPDATE {QuoteTable(name)} SET {column}=@to WHERE {where} AND {column}=@from");
            var from = update.Parameters.Add("@from", SqlDbType.Int);
            var to = update.Parameters.Add("@to", SqlDbType.Int);
            foreach (var (oldNumber, newNumber) in new[] { (first, temporary), (second, first), (temporary, second) })
            {
                from.Value = oldNumber;
                to.Value = newNumber;
                await update.ExecuteNonQueryAsync(ct);
            }
        }
        var assignments = string.Join(", ", destinations.Select(QuoteIdentifier)
            .Select(column => $"{column}=CASE {column} WHEN @first THEN @second WHEN @second THEN @first ELSE {column} END"));
        var affected = string.Join(" OR ", destinations.Select(QuoteIdentifier).Select(column => $"{column} IN (@first,@second)"));
        using (var remap = ScopedCommand($"UPDATE {QuoteTable(TransitionTable)} SET {assignments} WHERE {where} AND ({affected})"))
        {
            remap.Parameters.Add("@first", SqlDbType.Int).Value = first;
            remap.Parameters.Add("@second", SqlDbType.Int).Value = second;
            await remap.ExecuteNonQueryAsync(ct);
        }
        var afterRows = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in tables)
            afterRows[name] = await ReadLockedRowsAsync(connection, transaction, name, scope, ct);
        var changes = BuildStepMoveJournalChanges(tables, schemas, beforeRows, afterRows, first, second);
        var restorePayload = BuildStepMoveRestorePayload(tables, afterRows);
        await transaction.CommitAsync(ct);
        return new ProgramStepMoveJournalResult(second, changes, restorePayload);
    }
}
