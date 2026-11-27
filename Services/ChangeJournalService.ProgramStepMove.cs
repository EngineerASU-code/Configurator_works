using System.Data;
using Microsoft.Data.SqlClient;
using Configurator.Core.Projects;

namespace Configurator;

public static class ChangeJournalRestoreKind
{
    public const string ProgramStepMove = "ProgramStepMoveV1";
}

public sealed partial class ChangeJournalService
{
    private sealed record ProgramStepMoveChange(ChangeJournalRowChange Entry, string Table, string NumberField,
        Dictionary<string, object> Before, Dictionary<string, object> After,
        Dictionary<string, object> IdentityAfter, IReadOnlyList<string> ChangedFields);

    private static void RestoreOfflineProgramStepMove(ChangeJournalEntry entry,
        List<Configurator.Core.Projects.ProjectRecord> records,
        List<Configurator.Core.Projects.ProjectTableSchema> schemas)
    {
        ProgramStepMoveRestorePayload payload;
        try { payload = Newtonsoft.Json.JsonConvert.DeserializeObject<ProgramStepMoveRestorePayload>(entry.RestorePayloadJson); }
        catch (Newtonsoft.Json.JsonException ex) { throw new InvalidOperationException("Снимок состава Offline-перемещения повреждён.", ex); }
        if (payload?.Version != 1 || payload.Scope == null ||
            ProgramStepMoveScopeColumns.Any(column => !payload.Scope.ContainsKey(column)) ||
            ProgramStepMoveTables.Any(table => payload.Tables?.Count(item => string.Equals(item.TableName, table, StringComparison.OrdinalIgnoreCase)) != 1))
            throw new InvalidOperationException("Снимок состава Offline-перемещения неполон или несовместим.");

        var scope = Deserialize(payload.Scope);
        foreach (string table in ProgramStepMoveTables)
        {
            var expected = payload.Tables.Single(item => string.Equals(item.TableName, table, StringComparison.OrdinalIgnoreCase))
                .Rows.Select(Deserialize).ToList();
            var actual = records.Where(row => row.TableName.Equals(table, StringComparison.OrdinalIgnoreCase) &&
                ProgramStepMoveScopeColumns.All(column => row.Values.TryGetPropertyValue(column, out var node) &&
                    OfflineValueEquals(node?.ToString(), scope[column]))).ToList();
            if (actual.Count != expected.Count)
                throw new InvalidOperationException($"Конфликт восстановления {table}: состав связанных Offline-строк изменился.");
            foreach (var row in actual)
            {
                var matches = expected.Where(snapshot => OfflineSnapshotMatches(row, snapshot)).ToList();
                if (matches.Count != 1)
                    throw new InvalidOperationException($"Конфликт восстановления {table}: связанная Offline-строка изменена или неоднозначна.");
                expected.Remove(matches[0]);
            }
        }

        var pending = new List<(Configurator.Core.Projects.ProjectRecord Row,
            Configurator.Core.Projects.ProjectTableSchema Schema, string Table,
            Dictionary<string, object> Before, Dictionary<string, object> After, IReadOnlyList<string> Fields)>();
        foreach (var change in entry.Changes)
        {
            if (change == null || change.Kind != ChangeJournalOperationKind.Update ||
                !ProgramStepMoveTables.Contains(change.TableName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Журнал Offline-перемещения содержит неподдерживаемую таблицу или действие.");
            var table = ResolveOfflineTable(records, schemas, change.TableName);
            ProjectWriteGuard.EnsureDataTableWritable(table);
            var schema = schemas.FirstOrDefault(item => item.TableName.Equals(table, StringComparison.OrdinalIgnoreCase));
            var before = Deserialize(change.Before ?? new());
            var after = Deserialize(change.After ?? new());
            var identity = Deserialize(change.IdentityAfter ?? new());
            string field = table.Equals("dbo.Prog_StepTransition", StringComparison.OrdinalIgnoreCase) ? "SeqStep_Number" : "Step_Number";
            var keys = schema?.KeyColumns ?? new List<string>();
            var stableKeys = keys.Where(key => !key.Equals(field, StringComparison.OrdinalIgnoreCase)).ToArray();
            var differing = before.Where(pair => !after.TryGetValue(pair.Key, out var value) || !JournalValuesEqual(pair.Value, value))
                .Select(pair => pair.Key).ToArray();
            var allowed = field == "SeqStep_Number"
                ? ProgramStepMoveDestinationColumns.Append(field).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { field };
            var changed = differing.Where(allowed.Contains).ToArray();
            if (schema == null || !schema.HasStableKey || !keys.Contains(field, StringComparer.OrdinalIgnoreCase) || stableKeys.Length == 0 ||
                ProgramStepMoveScopeColumns.Any(name => !identity.ContainsKey(name)) || !identity.ContainsKey(field))
                throw new InvalidOperationException($"Для Offline-перемещения {table} отсутствует полный стабильный снимок.");

            var matches = FindOfflineRows(records.Where(row => row.TableName.Equals(table, StringComparison.OrdinalIgnoreCase)).ToList(), identity, after);
            if (matches.Count != 1)
                throw new InvalidOperationException($"В {table} не удалось однозначно найти строку перемещения для восстановления.");
            EnsureOfflineValuesMatch(matches[0], after, table, "Строка перемещения изменилась после операции");
            pending.Add((matches[0], schema, table, before, after, changed));
        }

        if (!pending.Any(item => item.Table.Equals("dbo.Prog_StepTransition", StringComparison.OrdinalIgnoreCase) &&
                                 !JournalValuesEqual(item.Before["SeqStep_Number"], item.After["SeqStep_Number"])))
            throw new InvalidOperationException("Журнал Offline-перемещения не содержит изменённой строки последовательности.");

        foreach (var item in pending)
        {
            if (item.Fields.Count == 0) continue;
            foreach (var field in item.Fields)
                item.Row.Values[field] = item.Before[field] == null || item.Before[field] == DBNull.Value
                    ? null
                    : System.Text.Json.JsonSerializer.SerializeToNode(item.Before[field]);
            item.Row.Key = BuildOfflineRecordKey(item.Schema, item.Schema.KeyColumns, item.Before);
            if (string.IsNullOrWhiteSpace(item.Row.Key))
                throw new InvalidOperationException($"Не удалось восстановить ключ строки {item.Table}.");
        }
    }

    private static bool OfflineSnapshotMatches(Configurator.Core.Projects.ProjectRecord row, IReadOnlyDictionary<string, object> expected)
        => row.Values.Count == expected.Count && expected.All(pair =>
            row.Values.TryGetPropertyValue(pair.Key, out var value) && OfflineValueEquals(value?.ToString(), pair.Value));

    private static readonly string[] ProgramStepMoveTables =
    {
        "dbo.Prog_StepTransition", "dbo.Prog_StepParam", "dbo.Prog_Recipes"
    };

    private static readonly string[] ProgramStepMoveScopeColumns =
    {
        "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record"
    };

    private static readonly string[] ProgramStepMoveDestinationColumns =
        Enumerable.Range(1, 8).Select(index => $"TransDest_{index:00}").ToArray();

    private async Task RestoreProgramStepMoveAsync(ChangeJournalEntry entry, string connectionString, CancellationToken ct)
    {
        if (entry.Changes == null || entry.Changes.Count < 2)
            throw new InvalidOperationException("В журнале нет полного снимка перемещения последовательности.");
        ProgramStepMoveRestorePayload payload;
        try { payload = Newtonsoft.Json.JsonConvert.DeserializeObject<ProgramStepMoveRestorePayload>(entry.RestorePayloadJson); }
        catch (Newtonsoft.Json.JsonException ex) { throw new InvalidOperationException("Снимок состава перемещения повреждён.", ex); }
        if (payload?.Version != 1 || payload.Scope == null ||
            ProgramStepMoveScopeColumns.Any(column => !payload.Scope.ContainsKey(column)) ||
            ProgramStepMoveTables.Any(table => payload.Tables?.Count(item => string.Equals(item.TableName, table, StringComparison.OrdinalIgnoreCase)) != 1))
            throw new InvalidOperationException("Снимок состава перемещения неполон или несовместим.");

        var changes = new List<ProgramStepMoveChange>();
        foreach (var row in entry.Changes)
        {
            if (row == null || !ProgramStepMoveTables.Contains(row.TableName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Журнал перемещения содержит неподдерживаемую таблицу.");
            ProjectWriteGuard.EnsureDataTableWritable(row.TableName);
            if (row.Kind != ChangeJournalOperationKind.Update || row.Before == null || row.After == null || row.IdentityAfter == null)
                throw new InvalidOperationException("Снимок перемещения неполон.");

            string field = string.Equals(row.TableName, "dbo.Prog_StepTransition", StringComparison.OrdinalIgnoreCase)
                ? "SeqStep_Number" : "Step_Number";
            var before = Deserialize(row.Before);
            var after = Deserialize(row.After);
            var identity = Deserialize(row.IdentityAfter);
            var differingFields = before.Where(pair => !after.TryGetValue(pair.Key, out var value) || !JournalValuesEqual(pair.Value, value))
                .Select(pair => pair.Key).ToArray();
            var allowed = string.Equals(row.TableName, "dbo.Prog_StepTransition", StringComparison.OrdinalIgnoreCase)
                ? ProgramStepMoveDestinationColumns.Append(field).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { field };
            var changedFields = differingFields.Where(allowed.Contains).ToArray();
            if (differingFields.Length == 0 ||
                !before.ContainsKey(field) || !after.ContainsKey(field) || !identity.ContainsKey(field) ||
                ProgramStepMoveScopeColumns.Any(scope => !identity.ContainsKey(scope)))
                throw new InvalidOperationException("Снимок перемещения не содержит безопасных ключей и полных изменённых значений.");

            changes.Add(new ProgramStepMoveChange(row, row.TableName, field, before, after, identity, changedFields));
        }

        if (!changes.Any(change => string.Equals(change.Table, "dbo.Prog_StepTransition", StringComparison.OrdinalIgnoreCase) &&
                                   !JournalValuesEqual(change.Before[change.NumberField], change.After[change.NumberField])))
            throw new InvalidOperationException("Снимок перемещения не включает изменённые строки последовательности.");

        await new DatabaseArchitectureAnalyzer(connectionString).AnalyzeAsync(ct);
        await using var connection = SqlProviderGuard.CreateConnection(connectionString);
        await SqlProviderGuard.OpenConnectionAsync(connection, ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await EnsureStepMoveRestoreStillSafeAsync(connection, transaction, ct);
            await EnsureProgramStepMoveSnapshotSetAsync(connection, transaction, payload, ct);
            EnsureProgramStepMoveChangesMatchPayload(changes, payload);

            var staged = await StageProgramStepMoveKeysAsync(connection, transaction, changes, ct);
            foreach (var change in changes)
            {
                var identity = new Dictionary<string, object>(change.IdentityAfter, StringComparer.OrdinalIgnoreCase);
                if (staged.TryGetValue(change, out int temporary)) identity[change.NumberField] = temporary;
                await RestoreProgramStepMoveRowAsync(connection, transaction, change, identity, ct);
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task EnsureStepMoveRestoreStillSafeAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
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
                throw new InvalidOperationException($"Восстановление перемещения заблокировано: не подтверждены ссылки в {unknown}.");
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
                throw new InvalidOperationException($"Восстановление перемещения заблокировано: активен UPDATE-триггер {trigger}.");
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
                throw new InvalidOperationException($"Восстановление перемещения заблокировано: поле используется внешним ключом {key}.");
        }
    }

    private static async Task EnsureProgramStepMoveSnapshotSetAsync(SqlConnection connection, SqlTransaction transaction,
        ProgramStepMoveRestorePayload payload, CancellationToken ct)
    {
        var scope = Deserialize(payload.Scope);
        foreach (string table in ProgramStepMoveTables)
        {
            var expected = payload.Tables.Single(item => string.Equals(item.TableName, table, StringComparison.OrdinalIgnoreCase))
                .Rows.Select(Deserialize).ToList();
            var where = ProgramStepMoveWhere(scope, out var parameters);
            using var command = new SqlCommand($"SELECT * FROM {QuoteTable(table)} WITH (UPDLOCK,HOLDLOCK) WHERE {where}", connection, transaction);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
            var actualTable = new DataTable();
            await using (var reader = await command.ExecuteReaderAsync(ct)) actualTable.Load(reader);
            if (actualTable.Rows.Count != expected.Count)
                throw new InvalidOperationException($"Конфликт восстановления {table}: состав связанных строк изменился после перемещения.");

            foreach (DataRow row in actualTable.Rows)
            {
                var actual = actualTable.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName,
                    column => row[column], StringComparer.OrdinalIgnoreCase);
                var matches = expected.Where(snapshot => SnapshotRowsEqual(actual, snapshot)).ToList();
                if (matches.Count != 1)
                    throw new InvalidOperationException($"Конфликт восстановления {table}: связанная строка изменена или неоднозначна.");
                expected.Remove(matches[0]);
            }
        }
    }

    private static void EnsureProgramStepMoveChangesMatchPayload(IReadOnlyList<ProgramStepMoveChange> changes,
        ProgramStepMoveRestorePayload payload)
    {
        foreach (var change in changes)
        {
            var table = payload.Tables.Single(item => string.Equals(item.TableName, change.Table, StringComparison.OrdinalIgnoreCase));
            var matches = table.Rows.Where(snapshot => SnapshotRowsEqual(change.After, Deserialize(snapshot)) &&
                change.IdentityAfter.All(pair => snapshot.TryGetValue(pair.Key, out var value) &&
                    JournalValuesEqual(pair.Value, value?.ToObject()))).ToList();
            if (matches.Count != 1 || !change.Before.Any(pair =>
                    !change.After.TryGetValue(pair.Key, out var value) || !JournalValuesEqual(pair.Value, value)))
                throw new InvalidOperationException("Снимок изменённых строк не совпадает с составом перемещения.");
        }
    }

    private static bool SnapshotRowsEqual(IReadOnlyDictionary<string, object> actual, IReadOnlyDictionary<string, object> expected)
        => actual.Count == expected.Count && actual.All(pair => expected.TryGetValue(pair.Key, out var value) && SnapshotValueEquals(pair.Value, value));

    private static bool SnapshotRowsEqual(IReadOnlyDictionary<string, ChangeJournalValue> actual,
        IReadOnlyDictionary<string, object> expected)
        => actual.Count == expected.Count && actual.All(pair => expected.TryGetValue(pair.Key, out var value) &&
            SnapshotValueEquals(pair.Value?.ToObject(), value));

    private static bool SnapshotValueEquals(object left, object right)
    {
        if (left is byte[] leftBytes && right is byte[] rightBytes) return leftBytes.SequenceEqual(rightBytes);
        return JournalValuesEqual(left, right);
    }

    private static async Task<Dictionary<ProgramStepMoveChange, int>> StageProgramStepMoveKeysAsync(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<ProgramStepMoveChange> changes, CancellationToken ct)
    {
        var staged = new Dictionary<ProgramStepMoveChange, int>();
        foreach (var group in changes.Where(change =>
                     !JournalValuesEqual(change.Before[change.NumberField], change.After[change.NumberField]))
                 .GroupBy(change => (change.Table, change.NumberField)))
        {
            var first = group.First();
            var scope = ProgramStepMoveScopeColumns.ToDictionary(name => name, name => first.IdentityAfter[name], StringComparer.OrdinalIgnoreCase);
            var occupied = await ReadProgramStepNumbersAsync(connection, transaction, first.Table, first.NumberField, scope, ct);
            var type = await ReadProgramStepNumberTypeAsync(connection, transaction, first.Table, first.NumberField, ct);
            var temporaryValues = FindTemporaryNumbers(occupied, group.Count(), type);
            int index = 0;
            foreach (var change in group)
            {
                int temporary = temporaryValues[index++];
                var where = ProgramStepMoveWhere(change.IdentityAfter, out var parameters);
                using var command = new SqlCommand
                {
                    Connection = connection,
                    Transaction = transaction,
                    CommandText = $"UPDATE {QuoteTable(change.Table)} SET {QuoteColumn(change.NumberField)}=@temporary WHERE {where}"
                };
                command.Parameters.AddWithValue("@temporary", temporary);
                foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
                if (await command.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException($"Конфликт восстановления {change.Table}: строка перемещения больше не существует.");
                staged.Add(change, temporary);
            }
        }
        return staged;
    }

    private static async Task<List<int>> ReadProgramStepNumbersAsync(SqlConnection connection, SqlTransaction transaction,
        string table, string field, IReadOnlyDictionary<string, object> scope, CancellationToken ct)
    {
        var where = ProgramStepMoveWhere(scope, out var parameters);
        using var command = new SqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = $"SELECT {QuoteColumn(field)} FROM {QuoteTable(table)} WITH (UPDLOCK,HOLDLOCK) WHERE {where}"
        };
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        var numbers = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) numbers.Add(Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        return numbers;
    }

    private static async Task<string> ReadProgramStepNumberTypeAsync(SqlConnection connection, SqlTransaction transaction,
        string table, string field, CancellationToken ct)
    {
        var parts = table.Split('.', 2);
        using var command = new SqlCommand("""
            SELECT TYPE_NAME(c.system_type_id) FROM sys.columns c
            WHERE c.object_id=OBJECT_ID(QUOTENAME(@schema)+'.'+QUOTENAME(@table)) AND c.name=@column;
            """, connection, transaction);
        command.Parameters.AddWithValue("@schema", parts.Length == 2 ? parts[0] : "dbo");
        command.Parameters.AddWithValue("@table", parts.Length == 2 ? parts[1] : parts[0]);
        command.Parameters.AddWithValue("@column", field);
        return Convert.ToString(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static List<int> FindTemporaryNumbers(IReadOnlyCollection<int> occupied, int count, string type)
    {
        var limits = type.ToLowerInvariant() switch
        {
            "tinyint" => (Min: 1, Max: byte.MaxValue),
            "smallint" => (Min: 1, Max: short.MaxValue),
            "int" => (Min: 1, Max: int.MaxValue),
            _ => throw new InvalidOperationException("Тип поля номера шага не поддерживает безопасную отмену.")
        };
        var unavailable = occupied.ToHashSet();
        var found = new List<int>();
        var searchCount = (long)occupied.Count + count;
        long value = limits.Max;
        long last = Math.Max(limits.Min, (long)limits.Max - searchCount + 1);
        for (; value >= last && found.Count < count; value--)
            if (!unavailable.Contains((int)value)) found.Add((int)value);
        value = limits.Min;
        last = Math.Min(limits.Max, (long)limits.Min + searchCount - 1);
        for (; value <= last && found.Count < count; value++)
            if (!unavailable.Contains((int)value) && !found.Contains((int)value)) found.Add((int)value);
        if (found.Count < count)
            throw new InvalidOperationException("Недостаточно свободных номеров для безопасного восстановления перемещения.");
        return found;
    }

    private static async Task RestoreProgramStepMoveRowAsync(SqlConnection connection, SqlTransaction transaction,
        ProgramStepMoveChange change, IReadOnlyDictionary<string, object> identity, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var set = new List<string>();
        int index = 0;
        foreach (var field in change.ChangedFields)
        {
            var parameter = "@restore" + index++;
            set.Add(QuoteColumn(field) + "=" + parameter);
            parameters.Add(parameter, change.Before[field] ?? DBNull.Value);
        }
        var where = ProgramStepMoveWhere(identity, out var keyParameters);
        using var command = new SqlCommand($"UPDATE {QuoteTable(change.Table)} SET {string.Join(",", set)} WHERE {where}", connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        foreach (var parameter in keyParameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException($"Конфликт восстановления {change.Table}: данные изменились во время отмены.");
    }

    private static string ProgramStepMoveWhere(IReadOnlyDictionary<string, object> identity, out Dictionary<string, object> parameters)
    {
        if (identity == null || identity.Count == 0) throw new InvalidOperationException("Для восстановления перемещения отсутствует ключ строки.");
        parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var predicates = new List<string>();
        int index = 0;
        foreach (var pair in identity)
        {
            if (pair.Value == null || pair.Value == DBNull.Value) predicates.Add(QuoteColumn(pair.Key) + " IS NULL");
            else
            {
                var parameter = "@key" + index++;
                predicates.Add(QuoteColumn(pair.Key) + "=" + parameter);
                parameters.Add(parameter, pair.Value);
            }
        }
        return string.Join(" AND ", predicates);
    }
}
