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
            public async Task InsertRowAsync(string tableName, Dictionary<string, object> values, CancellationToken ct = default)
                => await InsertRowCoreAsync(tableName, values, null, ct).ConfigureAwait(false);

            public async Task<IReadOnlyDictionary<string, object?>> InsertRowAndGetKeyAsync(string tableName,
                Dictionary<string, object> values, IReadOnlyList<string> keyColumns, CancellationToken ct = default)
            {
                if (keyColumns == null || keyColumns.Count == 0)
                    throw new InvalidOperationException("Не удалось определить первичный ключ созданной записи.");
                return await InsertRowCoreAsync(tableName, values, keyColumns, ct).ConfigureAwait(false);
            }

            private async Task<IReadOnlyDictionary<string, object?>> InsertRowCoreAsync(string tableName,
                Dictionary<string, object> values, IReadOnlyList<string>? keyColumns, CancellationToken ct)
            {
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
                if (writableValues.Count == 0) throw new InvalidOperationException("Нет сохраняемых полей: все переданные столбцы вычисляемые.");
                var columns = writableValues.Keys.ToList();
                var columnSql = string.Join(", ", columns.Select(QuoteIdentifier));
                var paramSql = string.Join(", ", columns.Select((_, i) => $"@p{i}"));
                var outputSql = keyColumns == null ? "" : " OUTPUT " + string.Join(", ", keyColumns.Select(key => "INSERTED." + QuoteIdentifier(key))) + " INTO @inserted";
                var captureSql = keyColumns == null ? "" : $"DECLARE @inserted TABLE ({string.Join(", ", keyColumns.Select(key => QuoteIdentifier(key) + " sql_variant"))}); ";
                var selectSql = keyColumns == null ? "" : $"; SELECT {string.Join(", ", keyColumns.Select(QuoteIdentifier))} FROM @inserted";
                var query = $"{captureSql}INSERT INTO {quotedName} ({columnSql}){outputSql} VALUES ({paramSql}){selectSql}";
    
                using var command = new SqlCommand(query, connection);
                for (int i = 0; i < columns.Count; i++)
                {
                    var value = writableValues[columns[i]];
                    command.Parameters.AddWithValue($"@p{i}", value ?? DBNull.Value);
                }
    
                if (keyColumns == null)
                {
                    await command.ExecuteNonQueryAsync(ct);
                    return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                }
                using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Вставка не вернула ключ созданной записи.");
                var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < keyColumns.Count; i++)
                    result[keyColumns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                return result;
            }
#nullable disable
    
            public async Task UpdateRowAsync(string tableName, List<string> keyColumns, Dictionary<string, object> keyValues, Dictionary<string, object> values, CancellationToken ct = default, Dictionary<string, object> expectedValues = null)
            {
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
                    var query = $"UPDATE {quotedName} SET {setClause} WHERE {whereClause}";
    
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
    
                    int affected = await command.ExecuteNonQueryAsync(ct);
                    if (affected == 0)
                        throw new InvalidOperationException("Запись не найдена — возможно, она была изменена или удалена другим пользователем.");
                    if (affected > 1)
                        throw new InvalidOperationException($"Условие ключа затронуло {affected} записей. Изменение отменено.");
    
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }

            public async Task UpdateCompareRowAsync(ProjectTableSchema expectedSchema, ProjectRecord expectedRecord,
                Dictionary<string, object> keyValues, Dictionary<string, object> values, CancellationToken ct = default)
            {
                if (expectedSchema == null || expectedRecord == null)
                    throw new ArgumentNullException(expectedSchema == null ? nameof(expectedSchema) : nameof(expectedRecord));
                if (!expectedSchema.HasStableKey || expectedSchema.KeyColumns.Count == 0 ||
                    keyValues == null || expectedSchema.KeyColumns.Any(key => !keyValues.ContainsKey(key)))
                    throw new InvalidOperationException("Не подтвержден устойчивый ключ целевой записи.");
                if (values == null || values.Count != 1)
                    throw new InvalidOperationException("Можно изменить только одно выбранное поле.");

                var (schemaName, bareTableName) = ParseTableName(expectedSchema.TableName);
                var quotedName = QuoteTableName(expectedSchema.TableName);
                string changedColumn = values.Keys.Single();
                var changedMetadata = expectedSchema.Columns.FirstOrDefault(column =>
                    column.Name.Equals(changedColumn, StringComparison.OrdinalIgnoreCase));
                if (changedMetadata == null || changedMetadata.IsIdentity || changedMetadata.IsComputed || changedMetadata.IsReadOnly ||
                    expectedSchema.KeyColumns.Contains(changedMetadata.Name, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Выбранное поле отсутствует, является ключом или доступно только для чтения.");
                if (expectedRecord.Values.Count != expectedSchema.Columns.Count ||
                    !expectedRecord.TableName.Equals(expectedSchema.TableName, StringComparison.OrdinalIgnoreCase))
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
                    await using (var selectCommand = new SqlCommand(selectSql, connection, transaction))
                    {
                        for (int i = 0; i < expectedSchema.KeyColumns.Count; i++)
                            selectCommand.Parameters.AddWithValue($"@key{i}", keyValues[expectedSchema.KeyColumns[i]] ?? DBNull.Value);
                        await using var reader = await selectCommand.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct))
                        {
                            var row = new JsonObject();
                            for (int i = 0; i < columns.Count; i++)
                                row[reader.GetName(i)] = OnlineProjectDataSource.ReadValue(reader, i);
                            actualRows.Add(row);
                            if (actualRows.Count > 1) break;
                        }
                    }

                    if (actualRows.Count != 1 || !CompareRecordValues(expectedRecord.Values, actualRows[0], columns))
                        throw new InvalidOperationException("Целевая запись изменилась или стала неоднозначной после сравнения. Повторите сравнение.");

                    await EnsureCompareWriteStillSafeAsync(connection, transaction, schemaName, bareTableName,
                        changedMetadata.Name, ct);

                    var updateSql = $"UPDATE {quotedName} SET {QuoteIdentifier(changedMetadata.Name)} = @value WHERE {string.Join(" AND ", expectedSchema.KeyColumns.Select((key, index) => $"{QuoteIdentifier(key)} = @key{index}"))}";
                    await using (var updateCommand = new SqlCommand(updateSql, connection, transaction))
                    {
                        updateCommand.Parameters.AddWithValue("@value", values[changedColumn] ?? DBNull.Value);
                        for (int i = 0; i < expectedSchema.KeyColumns.Count; i++)
                            updateCommand.Parameters.AddWithValue($"@key{i}", keyValues[expectedSchema.KeyColumns[i]] ?? DBNull.Value);
                        var affected = await updateCommand.ExecuteNonQueryAsync(ct);
                        if (affected != 1)
                            throw new InvalidOperationException("Обновление не затронуло ровно одну запись. Транзакция отменена.");
                    }

                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }

            private static async Task EnsureCompareWriteStillSafeAsync(SqlConnection connection, SqlTransaction transaction,
                string schemaName, string tableName, string columnName, CancellationToken ct)
            {
                const string sql = @"
SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')=1
                 AND HAS_PERMS_BY_NAME(QUOTENAME(s.name)+'.'+QUOTENAME(t.name), 'OBJECT', 'UPDATE')=1
                 AND DATABASEPROPERTYEX(DB_NAME(), 'Updateability')='READ_WRITE'
                 AND NOT EXISTS (SELECT 1 FROM sys.triggers tr WHERE tr.parent_id=t.object_id AND tr.is_disabled=0)
            THEN 1 ELSE 0 END AS can_update,
       CASE WHEN EXISTS (
           SELECT 1 FROM sys.foreign_key_columns fkc
           WHERE (fkc.parent_object_id=t.object_id AND fkc.parent_column_id=c.column_id)
              OR (fkc.referenced_object_id=t.object_id AND fkc.referenced_column_id=c.column_id)
       ) THEN 1 ELSE 0 END AS is_related
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id
WHERE s.name=@schema AND t.name=@table AND c.name=@column";

                await using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schemaName;
                command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = tableName;
                command.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = columnName;
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || reader.GetInt32(0) != 1)
                    throw new InvalidOperationException("Не подтверждены права записи, доступ к метаданным или отсутствие активных триггеров.");
                if (reader.GetInt32(1) != 0)
                    throw new InvalidOperationException("Изменение столбца, участвующего в физической связи базы данных, заблокировано.");
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
                if (keyValuesList == null || keyValuesList.Count == 0)
                    return 0;
    
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
    
                var quotedName = QuoteTableName(tableName);
                int totalDeleted = 0;
    
                using var transaction = await connection.BeginTransactionAsync(ct);
                try
                {
                    var whereParts = new List<string>();
                    for (int i = 0; i < keyColumns.Count; i++)
                    {
                        whereParts.Add($"{QuoteIdentifier(keyColumns[i])} = @key{i}");
                    }
                    var whereClause = string.Join(" AND ", whereParts);
                    var query = $"DELETE FROM {quotedName} WHERE {whereClause}";
    
                    foreach (var keyValues in keyValuesList)
                    {
                        using var command = new SqlCommand(query, connection, (SqlTransaction)transaction);
                        for (int i = 0; i < keyColumns.Count; i++)
                        {
                            command.Parameters.AddWithValue($"@key{i}", keyValues[keyColumns[i]] ?? DBNull.Value);
                        }
                        totalDeleted += await command.ExecuteNonQueryAsync(ct);
                    }
    
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
    
                return totalDeleted;
            }
    
            public async Task UpdateRowByValuesAsync(string tableName, Dictionary<string, object> originalValues, Dictionary<string, object> values, CancellationToken ct = default)
            {
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
                    var computedColumns = await GetComputedColumnsAsync(connection, tableName, ct, transaction);
                    var writableValues = values.Where(item => !computedColumns.Contains(item.Key)).ToList();
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
                    var query = $"UPDATE {quotedName} SET {string.Join(", ", setParts)} WHERE {whereClause}";
    
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
    
                    int affected = await command.ExecuteNonQueryAsync(ct);
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
    
            public async Task<int> DeleteRowsByValuesAsync(string tableName, List<Dictionary<string, object>> rows, CancellationToken ct = default)
            {
                if (rows == null || rows.Count == 0) return 0;
    
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
                int totalDeleted = 0;
    
                try
                {
                    foreach (var row in rows)
                    {
                        if (row == null || row.Count == 0) continue;
    
                        var whereParts = new List<string>();
                        int parameterIndex = 0;
                        foreach (var item in row)
                        {
                            if (item.Value == null || item.Value == DBNull.Value)
                                whereParts.Add($"{QuoteIdentifier(item.Key)} IS NULL");
                            else
                                whereParts.Add($"{QuoteIdentifier(item.Key)} = @p{parameterIndex++}");
                        }
    
                        var whereClause = string.Join(" AND ", whereParts);
                        var quotedName = QuoteTableName(tableName);
                        var countQuery = $"SELECT COUNT_BIG(*) FROM {quotedName} WITH (UPDLOCK, HOLDLOCK) WHERE {whereClause}";
                        using (var countCommand = new SqlCommand(countQuery, connection, transaction))
                        {
                            parameterIndex = 0;
                            foreach (var item in row)
                                if (item.Value != null && item.Value != DBNull.Value)
                                    countCommand.Parameters.AddWithValue($"@p{parameterIndex++}", item.Value);
                            var matches = Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct));
                            if (matches != 1)
                                throw new InvalidOperationException(matches == 0
                                    ? "Исходная строка больше не найдена. Удаление отменено."
                                    : "Исходное состояние соответствует нескольким строкам; безопасное удаление отменено.");
                        }
                        var query = $"DELETE FROM {quotedName} WHERE {whereClause}";
                        using var command = new SqlCommand(query, connection, transaction);
    
                        parameterIndex = 0;
                        foreach (var item in row)
                        {
                            if (item.Value != null && item.Value != DBNull.Value)
                                command.Parameters.AddWithValue($"@p{parameterIndex++}", item.Value);
                        }
    
                        var deleted = await command.ExecuteNonQueryAsync(ct);
                        if (deleted != 1) throw new InvalidOperationException("Удаление не затронуло ровно одну строку; транзакция отменена.");
                        totalDeleted += deleted;
                    }
    
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
    
                return totalDeleted;
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
