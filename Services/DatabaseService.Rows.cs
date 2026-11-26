using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Configurator.Core.Compare;
using Configurator.Core.Projects;

namespace Configurator
{
    public partial class DatabaseService
    {
            // === P1-11: Async ===
#nullable enable
            internal static async Task<string> JournalSnapshotProjectionAsync(SqlConnection connection, SqlTransaction? transaction,
                string quotedTable, CancellationToken ct)
            {
                using var command = new SqlCommand("""
                    SELECT QUOTENAME(name), system_type_id FROM sys.columns
                    WHERE object_id=OBJECT_ID(@table) AND is_hidden=0 ORDER BY column_id
                    """, connection, transaction);
                command.Parameters.AddWithValue("@table", quotedTable);
                var expressions = new List<string>();
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(0);
                    expressions.Add(reader.GetByte(1) == 189 ? $"CONVERT(binary(8), {name}) AS {name}" : name);
                }
                if (expressions.Count == 0) throw new InvalidOperationException($"Нет доступных полей таблицы {quotedTable}.");
                return string.Join(", ", expressions);
            }

            public async Task InsertRowAsync(string tableName, Dictionary<string, object> values, CancellationToken ct = default)
                => await InsertRowCoreAsync(tableName, values, null, ct).ConfigureAwait(false);

            public async Task<IReadOnlyDictionary<string, object?>> InsertRowAndGetKeyAsync(string tableName,
                Dictionary<string, object> values, IReadOnlyList<string> keyColumns, CancellationToken ct = default)
            {
                if (keyColumns == null || keyColumns.Count == 0)
                    throw new InvalidOperationException("Не удалось определить первичный ключ созданной записи.");
                return await InsertRowCoreAsync(tableName, values, keyColumns, ct).ConfigureAwait(false);
            }

            internal async Task<Dictionary<string, object>> InsertRowAndGetSnapshotAsync(string tableName,
                Dictionary<string, object> values, CancellationToken ct = default)
            {
                var snapshot = await InsertRowCoreAsync(tableName, values, null, ct, fullSnapshot: true).ConfigureAwait(false);
                return snapshot.ToDictionary(pair => pair.Key, pair => pair.Value ?? DBNull.Value, StringComparer.OrdinalIgnoreCase);
            }

            private async Task<IReadOnlyDictionary<string, object?>> InsertRowCoreAsync(string tableName,
                Dictionary<string, object> values, IReadOnlyList<string>? keyColumns, CancellationToken ct, bool fullSnapshot = false)
            {
                ProjectWriteGuard.EnsureDataTableWritable(tableName);
                if (values == null || values.Count == 0)
                    throw new InvalidOperationException("Нет данных для вставки.");
    
                // Полностью пустые строки запрещены на уровне сервиса даже если
                // SQL-схема технически позволяет вставить набор NULL.
                // Это защищает БД от случайных "пустых" записей независимо от UI.
                bool hasMeaningfulValue = values.Values.Any(v =>
                    v != null && v != DBNull.Value &&
                    (v is not string text || !string.IsNullOrWhiteSpace(text)));
                if (!hasMeaningfulValue)
                    throw new InvalidOperationException("Нельзя добавить полностью пустую строку. Заполните хотя бы одно поле.");
    
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
    
                var quotedName = QuoteTableName(tableName);
                var computedColumns = await GetComputedColumnsAsync(connection, tableName, ct);
                var writableValues = values.Where(x => !computedColumns.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                if (UsesUnifiedLanguage) UnifiedLanguage.Mirror(writableValues, (await GetTableColumnsAsync(tableName, ct)).Where(c => !c.IsReadOnly).Select(c => c.Name));
                if (writableValues.Count == 0) throw new InvalidOperationException("Нет сохраняемых полей: все переданные столбцы вычисляемые.");
                var columns = writableValues.Keys.ToList();
                var columnSql = string.Join(", ", columns.Select(QuoteIdentifier));
                var paramSql = string.Join(", ", columns.Select((_, i) => $"@p{i}"));
                var outputSql = keyColumns == null ? "" : " OUTPUT " + string.Join(", ", keyColumns.Select(key => "INSERTED." + QuoteIdentifier(key))) + " INTO @inserted";
                var captureSql = keyColumns == null ? "" : $"DECLARE @inserted TABLE ({string.Join(", ", keyColumns.Select(key => QuoteIdentifier(key) + " sql_variant"))}); ";
                var selectSql = keyColumns == null ? "" : $"; SELECT {string.Join(", ", keyColumns.Select(QuoteIdentifier))} FROM @inserted";
                if (fullSnapshot)
                {
                    var projection = await JournalSnapshotProjectionAsync(connection, null, quotedName, ct);
                    captureSql = $"SELECT TOP(0) {projection} INTO #created FROM {quotedName} UNION ALL SELECT TOP(0) {projection} FROM {quotedName}; ";
                    outputSql = " OUTPUT INSERTED.* INTO #created";
                    selectSql = "; SELECT * FROM #created";
                }
                var query = $"{captureSql}INSERT INTO {quotedName} ({columnSql}){outputSql} VALUES ({paramSql}){selectSql}";
    
                using var command = new SqlCommand(query, connection);
                for (int i = 0; i < columns.Count; i++)
                {
                    var value = writableValues[columns[i]];
                    command.Parameters.AddWithValue($"@p{i}", value ?? DBNull.Value);
                }
    
                if (keyColumns == null && !fullSnapshot)
                {
                    await command.ExecuteNonQueryAsync(ct);
                    return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                }
                using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Вставка не вернула ключ созданной записи.");
                var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                    result[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                return result;
            }
#nullable restore
#nullable enable annotations
    
            public async Task UpdateRowAsync(string tableName, List<string> keyColumns, Dictionary<string, object> keyValues, Dictionary<string, object> values, CancellationToken ct = default, Dictionary<string, object> expectedValues = null)
            {
                ProjectWriteGuard.EnsureDataTableWritable(tableName);
                if (keyColumns == null || keyColumns.Count == 0)
                    throw new InvalidOperationException("Невозможно обновить запись без первичного ключа.");
                if (keyValues == null || keyColumns.Any(k => !keyValues.ContainsKey(k)))
                    throw new InvalidOperationException("Не удалось определить значения первичного ключа.");
                if (values == null || values.Count == 0)
                    throw new InvalidOperationException("Нет данных для обновления.");
    
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    
                try
                {
                    var quotedName = QuoteTableName(tableName);
                    var computedColumns = await GetComputedColumnsAsync(connection, tableName, ct, transaction);
                    var writableValues = values.Where(x => !computedColumns.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                    if (UsesUnifiedLanguage) UnifiedLanguage.Mirror(writableValues, (await GetTableColumnsAsync(tableName, ct)).Where(c => !c.IsReadOnly).Select(c => c.Name));
                    if (writableValues.Count == 0) throw new InvalidOperationException("Нет изменяемых полей: все переданные столбцы вычисляемые.");
                    var columns = writableValues.Keys.ToList();
                    var setClause = string.Join(", ", columns.Select((c, i) => $"{QuoteIdentifier(c)} = @p{i}"));
                    var whereParts = keyColumns.Select((c, i) => $"{QuoteIdentifier(c)} = @key{i}").ToList();
                    int expectedParameter = 0;
                    if (expectedValues != null)
                    {
                        foreach (var pair in expectedValues.Where(x => !keyColumns.Contains(x.Key, StringComparer.OrdinalIgnoreCase)))
                        {
                            if (pair.Value == null || pair.Value == DBNull.Value)
                                whereParts.Add($"{QuoteIdentifier(pair.Key)} IS NULL");
                            else
                                whereParts.Add($"{QuoteIdentifier(pair.Key)} = @expected{expectedParameter++}");
                        }
                    }
                    var whereClause = string.Join(" AND ", whereParts);
                    var query = $"DECLARE @affected TABLE (Marker int NOT NULL); UPDATE {quotedName} SET {setClause} OUTPUT 1 INTO @affected(Marker) WHERE {whereClause}; SELECT COUNT(*) FROM @affected;";
    
                    using var command = new SqlCommand(query, connection, transaction);
                    for (int i = 0; i < columns.Count; i++)
                        command.Parameters.AddWithValue($"@p{i}", writableValues[columns[i]] ?? DBNull.Value);
                    for (int i = 0; i < keyColumns.Count; i++)
                        command.Parameters.AddWithValue($"@key{i}", keyValues[keyColumns[i]] ?? DBNull.Value);
                    if (expectedValues != null)
                    {
                        expectedParameter = 0;
                        foreach (var pair in expectedValues.Where(x => !keyColumns.Contains(x.Key, StringComparer.OrdinalIgnoreCase)))
                            if (pair.Value != null && pair.Value != DBNull.Value)
                                command.Parameters.AddWithValue($"@expected{expectedParameter++}", pair.Value);
                    }
    
                    int affected = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
                    if (affected == 0)
                        throw new InvalidOperationException("Запись не найдена — возможно, она была изменена или удалена другим пользователем.");
                    if (affected != 1)
                        throw new InvalidOperationException($"Условие ключа затронуло {affected} записей. Изменение отменено.");
    
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }

            public async Task<ChangeJournalRowChange> UpdateCompareRowAsync(ProjectTableSchema expectedSchema, ProjectRecord? expectedRecord,
                Dictionary<string, object> keyValues, Dictionary<string, object> values, CancellationToken ct = default, bool delete = false)
            {
                ArgumentNullException.ThrowIfNull(expectedSchema);
                ProjectWriteGuard.EnsureDataTableWritable(expectedSchema.TableName);
                bool insert = expectedRecord == null;
                if (!expectedSchema.HasStableKey || expectedSchema.KeyColumns.Count == 0 ||
                    keyValues == null || expectedSchema.KeyColumns.Any(key => !keyValues.ContainsKey(key)))
                    throw new InvalidOperationException("Не подтвержден устойчивый ключ целевой записи.");
                if (values == null || (!delete && values.Count == 0) || (delete && insert))
                    throw new InvalidOperationException("Нет выбранных полей для переноса.");

                var (schemaName, bareTableName) = ParseTableName(expectedSchema.TableName);
                var quotedName = QuoteTableName(expectedSchema.TableName);
                if (UsesUnifiedLanguage && !delete)
                {
                    values = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
                    UnifiedLanguage.Mirror(values, expectedSchema.Columns.Select(c => c.Name));
                }
                var changedColumns = values.Keys.ToArray();
                foreach (string changedColumn in changedColumns)
                {
                    var metadata = expectedSchema.Columns.FirstOrDefault(column => column.Name.Equals(changedColumn, StringComparison.OrdinalIgnoreCase));
                    if (metadata == null || metadata.IsIdentity || metadata.IsComputed || metadata.IsReadOnly ||
                        (!insert && expectedSchema.KeyColumns.Contains(metadata.Name, StringComparer.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"Поле «{changedColumn}» отсутствует, является ключом или доступно только для чтения.");
                }
                if (!insert && (expectedRecord!.Values.Count != expectedSchema.Columns.Count ||
                    !expectedRecord.TableName.Equals(expectedSchema.TableName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Сохранённое состояние Compare не содержит полную целевую запись.");

                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

                try
                {
                    var currentSchema = await OnlineProjectDataSource.ReadSchemaAsync(connection, transaction,
                        expectedSchema.TableName, ct);
                    if (currentSchema is null || !SameCompareSchema(expectedSchema, currentSchema))
                        throw new InvalidOperationException("Схема или ключ таблицы изменились после сравнения. Повторите сравнение.");

                    var columns = expectedSchema.Columns;
                    var selectSql = $"SELECT {string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)))} FROM {quotedName} WITH (UPDLOCK, HOLDLOCK) WHERE {string.Join(" AND ", expectedSchema.KeyColumns.Select((key, index) => $"{QuoteIdentifier(key)} = @key{index}"))}";
                    var actualRows = new List<JsonObject>();
                    var actualSnapshots = new List<Dictionary<string, object>>();
                    await using (var selectCommand = new SqlCommand(selectSql, connection, transaction))
                    {
                        for (int i = 0; i < expectedSchema.KeyColumns.Count; i++)
                            selectCommand.Parameters.AddWithValue($"@key{i}", keyValues[expectedSchema.KeyColumns[i]] ?? DBNull.Value);
                        await using var reader = await selectCommand.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct))
                        {
                            var row = new JsonObject();
                            var snapshot = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < columns.Count; i++)
                            {
                                row[reader.GetName(i)] = OnlineProjectDataSource.ReadValue(reader, i);
                                snapshot[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                            }
                            actualRows.Add(row);
                            actualSnapshots.Add(snapshot);
                            if (actualRows.Count > 1) break;
                        }
                    }

                    if (insert && keyValues.TryGetValue("Record", out var number))
                    {
                        if (actualRows.Count != 0)
                            throw new Configurator.Infrastructure.Projects.CompareRecordConflictException();
                        if (expectedSchema.KeyColumns.Count > 1)
                        {
                            using var occupied = new SqlCommand($@"
                                IF EXISTS (SELECT 1 FROM sys.indexes i
                                    JOIN sys.index_columns k ON k.object_id=i.object_id AND k.index_id=i.index_id
                                    WHERE i.object_id=OBJECT_ID(@table) AND i.is_unique=1 AND i.is_disabled=0
                                      AND i.has_filter=0 AND k.key_ordinal>0
                                    GROUP BY i.index_id
                                    HAVING COUNT(*)=1 AND MAX(COL_NAME(i.object_id,k.column_id))='Record')
                                SELECT TOP (1) 1 FROM {quotedName} WITH (UPDLOCK,HOLDLOCK) WHERE [Record]=@record",
                                connection, transaction);
                            occupied.Parameters.AddWithValue("@table", quotedName);
                            occupied.Parameters.AddWithValue("@record", number ?? DBNull.Value);
                            if (await occupied.ExecuteScalarAsync(ct) != null)
                                throw new Configurator.Infrastructure.Projects.CompareRecordConflictException();
                        }
                    }

                    if (insert ? actualRows.Count != 0 : actualRows.Count != 1 || !CompareRecordValues(expectedRecord!.Values, actualRows[0], columns))
                        throw new InvalidOperationException("Целевая запись изменилась или стала неоднозначной после сравнения. Повторите сравнение.");

                    await EnsureCompareWriteStillSafeAsync(connection, transaction, schemaName, bareTableName,
                        delete ? "DELETE" : insert ? "INSERT" : "UPDATE", ct);

                    if (insert && CompareTransferPreview.IsEquipmentTable(expectedSchema))
                    {
                        foreach (var (table, fields, lookupValues) in CompareTransferPreview.EquipmentLookups(expectedSchema, values))
                        {
                            var lookupWhere = string.Join(" AND ", fields.Select((field, index) => $"{QuoteIdentifier(field)} = @lookup{index}"));
                            var lookupSql = $"SELECT TOP (1) 1 FROM {QuoteTableName(table)} WHERE {lookupWhere}";
                            await using var lookup = new SqlCommand(lookupSql, connection, transaction);
                            for (int index = 0; index < fields.Length; index++)
                                lookup.Parameters.AddWithValue($"@lookup{index}", lookupValues[index] ?? DBNull.Value);
                            if (await lookup.ExecuteScalarAsync(ct) is null)
                                throw new InvalidOperationException($"Для объекта отсутствует связанная запись в {table}.");
                        }
                    }

                    var assignments = string.Join(", ", changedColumns.Select((column, index) => $"{QuoteIdentifier(column)} = @value{index}"));
                    var updateSql = $"UPDATE {quotedName} SET {assignments} WHERE {string.Join(" AND ", expectedSchema.KeyColumns.Select((key, index) => $"{QuoteIdentifier(key)} = @key{index}"))}";
                    if (delete)
                        updateSql = $"DELETE FROM {quotedName} WHERE {string.Join(" AND ", expectedSchema.KeyColumns.Select((key, index) => $"{QuoteIdentifier(key)} = @key{index}"))}";
                    else if (insert)
                        updateSql = $"INSERT INTO {quotedName} ({string.Join(", ", changedColumns.Select(QuoteIdentifier))}) VALUES ({string.Join(", ", changedColumns.Select((_, index) => $"@value{index}"))})";
                    await using (var updateCommand = new SqlCommand(updateSql, connection, transaction))
                    {
                        for (int i = 0; i < changedColumns.Length; i++)
                            updateCommand.Parameters.AddWithValue($"@value{i}", values[changedColumns[i]] ?? DBNull.Value);
                        for (int i = 0; i < expectedSchema.KeyColumns.Count; i++)
                            updateCommand.Parameters.AddWithValue($"@key{i}", keyValues[expectedSchema.KeyColumns[i]] ?? DBNull.Value);
                        var affected = await updateCommand.ExecuteNonQueryAsync(ct);
                        if (affected != 1)
                            throw new InvalidOperationException("Обновление не затронуло ровно одну запись. Транзакция отменена.");
                    }

                    var afterSnapshot = delete ? new Dictionary<string, object>() : await ReadCompareRowSnapshotAsync(connection, transaction, expectedSchema.TableName,
                        columns, expectedSchema.KeyColumns, keyValues, ct);
                    var beforeSnapshot = insert ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) : actualSnapshots[0];
                    var captured = new ChangeJournalRowChange
                    {
                        TableName = expectedSchema.TableName,
                        Kind = delete ? ChangeJournalOperationKind.Delete : insert ? ChangeJournalOperationKind.Insert : ChangeJournalOperationKind.Update,
                        Before = ToCompareJournalValues(beforeSnapshot),
                        After = ToCompareJournalValues(afterSnapshot),
                        IdentityBefore = insert ? new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase)
                            : ToCompareJournalIdentity(beforeSnapshot, expectedSchema.KeyColumns),
                        IdentityAfter = delete ? new() : ToCompareJournalIdentity(afterSnapshot, expectedSchema.KeyColumns)
                    };
                    await transaction.CommitAsync(ct);
                    return captured;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }

            private async Task<Dictionary<string, object>> ReadCompareRowSnapshotAsync(SqlConnection connection,
                SqlTransaction transaction, string tableName, IReadOnlyList<ProjectColumnSchema> columns,
                IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object> keyValues, CancellationToken ct)
            {
                var predicates = keyColumns.Select((key, index) => $"{QuoteIdentifier(key)} = @key{index}");
                var sql = $"SELECT TOP (2) {string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)))} " +
                    $"FROM {QuoteTableName(tableName)} WITH (UPDLOCK, HOLDLOCK) WHERE {string.Join(" AND ", predicates)}";
                await using var command = new SqlCommand(sql, connection, transaction);
                for (int i = 0; i < keyColumns.Count; i++)
                    command.Parameters.AddWithValue($"@key{i}", keyValues[keyColumns[i]] ?? DBNull.Value);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Не удалось подтвердить целевую запись после переноса.");
                var snapshot = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                    snapshot[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                if (await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Ключ целевой записи соответствует нескольким строкам после переноса.");
                if (columns.Any(column => !snapshot.ContainsKey(column.Name)))
                    throw new InvalidOperationException("Снимок целевой записи после переноса неполон.");
                return snapshot;
            }

            private static Dictionary<string, ChangeJournalValue> ToCompareJournalValues(IReadOnlyDictionary<string, object> values)
                => values.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase);

            private static Dictionary<string, ChangeJournalValue> ToCompareJournalIdentity(
                IReadOnlyDictionary<string, object> values, IReadOnlyList<string> keyColumns)
                => keyColumns.ToDictionary(key => key, key => ChangeJournalValue.FromObject(values[key]), StringComparer.OrdinalIgnoreCase);

            private static async Task EnsureCompareWriteStillSafeAsync(SqlConnection connection, SqlTransaction transaction,
                string schemaName, string tableName, string permission, CancellationToken ct)
            {
                const string sql = @"
SELECT CASE WHEN HAS_PERMS_BY_NAME(QUOTENAME(s.name)+'.'+QUOTENAME(t.name), 'OBJECT', @permission)=1
                 AND DATABASEPROPERTYEX(DB_NAME(), 'Updateability')='READ_WRITE'
                 AND NOT EXISTS (SELECT 1 FROM sys.triggers tr WHERE tr.parent_id=t.object_id AND tr.is_disabled=0)
                 AND (@permission <> 'DELETE' OR NOT EXISTS
                     (SELECT 1 FROM sys.foreign_keys fk WHERE fk.referenced_object_id=t.object_id
                      AND fk.delete_referential_action<>0 AND fk.is_disabled=0))
            THEN 1 ELSE 0 END
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name=@schema AND t.name=@table";

                await using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schemaName;
                command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = tableName;
                command.Parameters.Add("@permission", SqlDbType.NVarChar, 16).Value = permission;
                if (!Equals(await command.ExecuteScalarAsync(ct), 1))
                    throw new InvalidOperationException("Запись недоступна. Проверьте права SQL, триггеры и каскадные изменения целевой таблицы.");
            }

            private static bool SameCompareSchema(ProjectTableSchema expected, ProjectTableSchema current)
            {
                if (!expected.HasStableKey || !current.HasStableKey ||
                    !expected.TableName.Equals(current.TableName, StringComparison.OrdinalIgnoreCase) ||
                    expected.Columns.Count != current.Columns.Count ||
                    !expected.KeyColumns.SequenceEqual(current.KeyColumns, StringComparer.OrdinalIgnoreCase)) return false;

                foreach (var column in expected.Columns)
                {
                    var actual = current.Columns.FirstOrDefault(candidate =>
                        candidate.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
                    if (actual == null || !actual.DataType.Equals(column.DataType, StringComparison.OrdinalIgnoreCase) ||
                        actual.IsNullable != column.IsNullable || actual.IsIdentity != column.IsIdentity ||
                        actual.IsComputed != column.IsComputed || actual.IsReadOnly != column.IsReadOnly ||
                        actual.MaxLength != column.MaxLength ||
                        actual.Precision != column.Precision || actual.Scale != column.Scale ||
                        actual.HasDefault != column.HasDefault) return false;
                }

                return true;
            }

            private static bool CompareRecordValues(JsonObject expected, JsonObject actual,
                IReadOnlyList<ProjectColumnSchema> columns)
            {
                foreach (var column in columns)
                {
                    var expectedMatches = expected.Where(pair => pair.Key.Equals(column.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var actualMatches = actual.Where(pair => pair.Key.Equals(column.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (expectedMatches.Length != 1 || actualMatches.Length != 1 ||
                        CompareValues.Compare(expectedMatches[0].Value, actualMatches[0].Value, column.DataType, column.DataType) != CompareStatus.Equal)
                        return false;
                }

                return expected.Count == columns.Count;
            }
    
            public async Task<int> DeleteRowsAsync(string tableName, List<string> keyColumns, List<Dictionary<string, object>> keyValuesList, CancellationToken ct = default)
            {
                ProjectWriteGuard.EnsureDataTableWritable(tableName);
                if (keyValuesList == null || keyValuesList.Count == 0) return 0;
                if (keyColumns == null || keyColumns.Count == 0 || keyValuesList.Any(row => row == null || keyColumns.Any(key => !row.ContainsKey(key))))
                    throw new InvalidOperationException("Для безопасного удаления необходим ключ и полный снимок каждой строки.");
                return await DeleteWithSnapshotsAsync(tableName, keyValuesList, keyColumns, ct);
            }

            private async Task<int> DeleteWithSnapshotsAsync(string tableName, List<Dictionary<string, object>> rows, IReadOnlyList<string> keyColumns, CancellationToken ct)
            {
                var schema = await new DatabaseArchitectureAnalyzer(_connectionString).AnalyzeAsync(ct);
                var table = schema.FindTable(tableName) ?? throw new InvalidOperationException("Таблица отсутствует в схеме.");
                if (rows.Any(row => row == null || row.Count != table.Columns.Count || table.Columns.Any(column => !row.ContainsKey(column.Name))))
                    throw new InvalidOperationException("Для безопасного удаления необходим полный снимок каждой строки.");
                var protection = new DeletionProtectionService(_connectionString, schema);
                keyColumns ??= table.Keys.FirstOrDefault(key => key.IsPrimary)?.Columns
                    ?? table.Keys.FirstOrDefault(key => key.IsUnique)?.Columns;
                using var connection = SqlProviderGuard.CreateConnection(_connectionString);
                await SqlProviderGuard.OpenConnectionAsync(connection, ct);
                using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                try
                {
                    foreach (var row in rows)
                    {
                        var identity = keyColumns == null ? row : keyColumns.ToDictionary(key => key, key => row[key], StringComparer.OrdinalIgnoreCase);
                        await protection.DeleteUnreferencedInTransactionAsync(connection, transaction, tableName, identity, row, ct);
                    }
                    await transaction.CommitAsync(ct);
                    return rows.Count;
                }
                catch { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } throw; }
            }

            public async Task UpdateRowByValuesAsync(string tableName, Dictionary<string, object> originalValues, Dictionary<string, object> values, CancellationToken ct = default)
            {
                ProjectWriteGuard.EnsureDataTableWritable(tableName);
                if (originalValues == null || originalValues.Count == 0)
                    throw new InvalidOperationException("Невозможно определить исходную строку.");
                if (values == null || values.Count == 0)
                    throw new InvalidOperationException("Нет данных для обновления.");
    
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    
                try
                {
                    var quotedName = QuoteTableName(tableName);
                    var schema = await OnlineProjectDataSource.ReadSchemaAsync(connection, transaction, tableName, ct);
                    if (schema == null || schema.Columns.Count != originalValues.Count ||
                        schema.Columns.Any(column => !originalValues.ContainsKey(column.Name) ||
                            !TableViewModel.IsValueIdentityColumn(new ColumnInfo { DataType = column.DataType })))
                        throw new InvalidOperationException("Для безопасного обновления необходим полный сравнимый снимок строки. Обновите таблицу.");
                    var computedColumns = await GetComputedColumnsAsync(connection, tableName, ct, transaction);
                    var writableValues = values.Where(item => !computedColumns.Contains(item.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                    if (UsesUnifiedLanguage) UnifiedLanguage.Mirror(writableValues, (await GetTableColumnsAsync(tableName, ct)).Where(c => !c.IsReadOnly).Select(c => c.Name));
                    if (writableValues.Count == 0) throw new InvalidOperationException("Нет изменяемых полей: все переданные столбцы вычисляемые.");
                    var setParts = new List<string>();
                    var whereParts = new List<string>();
                    int valueParameter = 0;
                    int originalParameter = 0;
    
                    foreach (var item in writableValues)
                        setParts.Add($"{QuoteIdentifier(item.Key)} = @v{valueParameter++}");
    
                    // NULL сравниваем через IS NULL, а не через = @param.
                    // Это исправляет UPDATE строк без PK, содержащих NULL.
                    foreach (var item in originalValues)
                    {
                        if (item.Value == null || item.Value == DBNull.Value)
                            whereParts.Add($"{QuoteIdentifier(item.Key)} IS NULL");
                        else
                            whereParts.Add($"{QuoteIdentifier(item.Key)} = @o{originalParameter++}");
                    }
    
                    var whereClause = string.Join(" AND ", whereParts);
                    var countQuery = $"SELECT COUNT_BIG(*) FROM {quotedName} WITH (UPDLOCK, HOLDLOCK) WHERE {whereClause}";
                    using (var countCommand = new SqlCommand(countQuery, connection, transaction))
                    {
                        originalParameter = 0;
                        foreach (var item in originalValues)
                            if (item.Value != null && item.Value != DBNull.Value)
                                countCommand.Parameters.AddWithValue($"@o{originalParameter++}", item.Value);
                        var matches = Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct));
                        if (matches != 1)
                            throw new InvalidOperationException(matches == 0
                                ? "Исходная строка больше не найдена. Обновление отменено."
                                : "Исходное состояние соответствует нескольким строкам; безопасное обновление отменено.");
                    }
                    var query = $"DECLARE @affected TABLE (Marker int NOT NULL); UPDATE {quotedName} SET {string.Join(", ", setParts)} OUTPUT 1 INTO @affected(Marker) WHERE {whereClause}; SELECT COUNT(*) FROM @affected;";
    
                    using var command = new SqlCommand(query, connection, transaction);
                    valueParameter = 0;
                    foreach (var item in writableValues)
                        command.Parameters.AddWithValue($"@v{valueParameter++}", item.Value ?? DBNull.Value);
    
                    originalParameter = 0;
                    foreach (var item in originalValues)
                    {
                        if (item.Value != null && item.Value != DBNull.Value)
                            command.Parameters.AddWithValue($"@o{originalParameter++}", item.Value);
                    }
    
                    int affected = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
                    if (affected == 0)
                        throw new InvalidOperationException("Исходная строка больше не найдена. Обновление отменено.");
                    if (affected != 1)
                        throw new InvalidOperationException("Обновление затронуло несколько строк. Транзакция отменена.");
    
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
    
            public Task<int> DeleteRowsByValuesAsync(string tableName, List<Dictionary<string, object>> rows, CancellationToken ct = default)
            {
                ProjectWriteGuard.EnsureDataTableWritable(tableName);
                if (rows == null || rows.Count == 0) return Task.FromResult(0);
                return DeleteWithSnapshotsAsync(tableName, rows, null, ct);
            }

            // === P1-11: Async ===

            private static async Task<HashSet<string>> GetComputedColumnsAsync(SqlConnection connection, string tableName, CancellationToken ct, SqlTransaction transaction = null)
            {
                var parts = (tableName ?? "").Split('.', 2);
                var schema = parts.Length == 2 ? parts[0] : "dbo";
                var table = parts.Length == 2 ? parts[1] : parts[0];
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                const string sql = @"SELECT c.name FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@schema AND t.name=@table AND c.is_computed=1";
                using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("@schema", schema);
                command.Parameters.AddWithValue("@table", table);
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
                return result;
            }
    }
}
