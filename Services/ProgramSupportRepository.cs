#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Configurator;

public sealed class ProgramSupportRepository
{
    private readonly string _connectionString;

    public ProgramSupportRepository(string connectionString) =>
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Не задана строка подключения.", nameof(connectionString))
            : connectionString;

    public Task<ProgramSupportTableData> LoadProgramAsync(string tableId, ProgramContext context) =>
        LoadAsync(Find(ProgramSupportCatalog.ProgramTables, tableId), GetProgramScope(Find(ProgramSupportCatalog.ProgramTables, tableId), context));

    public Task<ProgramSupportTableData> LoadMatrixAsync(string tableId, object plcValue, object matrixClassValue, object? matrixRecord = null)
    {
        var definition = Find(ProgramSupportCatalog.MatrixTables, tableId);
        var scope = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = Required(plcValue, "PLC"),
            ["PLC_Class_Number"] = Required(matrixClassValue, "Класс Matrix")
        };
        if (definition.ScopeColumns.Contains("Record", StringComparer.OrdinalIgnoreCase))
            scope["Record"] = Required(matrixRecord, "Матрица");
        return LoadAsync(definition, scope);
    }

    public async Task SaveAsync(ProgramSupportTableData tableData, IReadOnlyDictionary<string, object> scope)
    {
        var definition = tableData?.Definition ?? throw new ArgumentNullException(nameof(tableData));
        if (!tableData.CanWrite || definition.ReadOnly)
            throw new InvalidOperationException(tableData.WriteBlockReason ?? definition.ReadOnlyReason ?? "Запись для этой таблицы заблокирована.");

        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
        using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var metadata = await GetMetadataAsync(connection, transaction, definition);
            EnsureWriteKey(definition, metadata.UniqueKeys);
            ValidateScope(definition, scope, metadata.Columns);

            foreach (DataRow row in tableData.Rows.Rows)
            {
                if (row.RowState == DataRowState.Unchanged || row.RowState == DataRowState.Detached) continue;
                switch (row.RowState)
                {
                    case DataRowState.Added:
                        await InsertAsync(connection, transaction, tableData, row, scope, metadata);
                        break;
                    case DataRowState.Modified:
                        await UpdateAsync(connection, transaction, tableData, row, scope, metadata);
                        break;
                    case DataRowState.Deleted:
                        await DeleteAsync(connection, transaction, tableData, row, scope, metadata);
                        break;
                }
            }
            await transaction.CommitAsync();
        }
        catch
        {
            try { await transaction.RollbackAsync(); } catch { }
            throw;
        }
    }

    private async Task<ProgramSupportTableData> LoadAsync(ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope)
    {
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
        var metadata = await GetMetadataAsync(connection, null, definition);
        ValidateScope(definition, scope, metadata.Columns);

        string where = string.Join(" AND ", definition.ScopeColumns.Select((column, index) => $"{Quote(column)}=@s{index}"));
        string order = definition.KeyColumns.Count == 0 ? "" : " ORDER BY " + string.Join(", ", definition.KeyColumns.Select(Quote));
        using var command = new SqlCommand($"SELECT * FROM {QuoteTable(definition.TableName)} WHERE {where}{order};", connection);
        AddScopeParameters(command, definition, scope);
        var rows = new DataTable(definition.TableName);
        using (var reader = await command.ExecuteReaderAsync()) rows.Load(reader);

        bool keyMatches = definition.KeyColumns.Count > 0 && metadata.UniqueKeys.Any(key => SameColumns(key, definition.KeyColumns));
        bool canWrite = !definition.ReadOnly && keyMatches;
        string? blockReason = definition.ReadOnlyReason;
        if (!definition.ReadOnly && !keyMatches)
            blockReason = "Запись отключена: текущий уникальный ключ таблицы не совпадает с проверенной схемой.";
        if (keyMatches)
            rows.PrimaryKey = definition.KeyColumns.Select(name => rows.Columns[name] ?? throw new InvalidOperationException($"В таблице отсутствует ключевое поле {name}." )).ToArray();

        return new ProgramSupportTableData
        {
            Definition = definition,
            Rows = rows,
            Columns = metadata.Columns.Keys.ToArray(),
            IdentityColumns = metadata.Columns.Values.Where(column => column.Identity).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ComputedColumns = metadata.Columns.Values.Where(column => column.Computed).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            DefaultColumns = metadata.Columns.Values.Where(column => column.HasDefault).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            CanWrite = canWrite,
            WriteBlockReason = blockReason,
            ProgMatrixRows = definition.Id == "program-matrix" ? rows.Rows.Cast<DataRow>().Select(ToProgMatrixRow).ToArray() : null,
            MatrixListRows = definition.Id == "matrix-list" ? rows.Rows.Cast<DataRow>().Select(ToMatrixListRow).ToArray() : null
        };
    }

    private static ProgMatrixRow ToProgMatrixRow(DataRow row) => new(
        new ProgMatrixKey(Convert.ToInt32(row["Record"]), Convert.ToInt32(row["PLC"]), Convert.ToInt32(row["PLC_Class_Number"])),
        row["Type"] == DBNull.Value ? null : Convert.ToInt32(row["Type"]),
        NullableText(row["Name_L1"]), NullableText(row["Name_L2"]), NullableText(row["DescriptionL1"]), NullableText(row["DescriptionL2"]));

    private static MatrixListRow ToMatrixListRow(DataRow row) => new(
        new MatrixListKey(Convert.ToInt32(row["Record"]), Convert.ToInt32(row["PLC"]), Convert.ToInt32(row["PLC_Class_Number"])),
        Convert.ToString(row["Name_L1"]) ?? "", Convert.ToString(row["Name_L2"]) ?? "", Convert.ToInt32(row["DB_Number"]));

    private static string? NullableText(object value) => value == null || value == DBNull.Value ? null : Convert.ToString(value);

    internal static IReadOnlyDictionary<string, object> GetProgramScope(ProgramSupportTableDefinition definition, ProgramContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        object Value(string column) => column switch
        {
            "PLC" => Required(context.PlcValue, "PLC"),
            "PLC_Class_Prog_Number" => Required(context.ClassNumberValue, "Класс программы"),
            "Prog_Record" => Required(context.RecordValue, "Программа"),
            "Seq_Record" => Required(context.SeqRecordValue, "Последовательность"),
            _ => throw new InvalidOperationException($"Неизвестное поле контекста {column}.")
        };
        return definition.ScopeColumns.ToDictionary(column => column, Value, StringComparer.OrdinalIgnoreCase);
    }

    private static object Required(object? value, string label)
    {
        if (value == null || value == DBNull.Value) throw new InvalidOperationException($"Сначала выберите значение «{label}».");
        return value;
    }

    private async Task InsertAsync(SqlConnection connection, SqlTransaction transaction, ProgramSupportTableData tableData,
        DataRow row, IReadOnlyDictionary<string, object> scope, TableMetadata metadata)
    {
        var definition = tableData.Definition;
        if (!definition.AllowInsert) throw new InvalidOperationException($"Добавление строк в {definition.Caption} отключено до проверки зависимостей.");
        foreach (var pair in scope) row[pair.Key] = pair.Value;
        if (definition.KeyColumns.Contains("Record", StringComparer.OrdinalIgnoreCase) && !definition.ScopeColumns.Contains("Record", StringComparer.OrdinalIgnoreCase)
            && !metadata.Columns["Record"].Identity)
            row["Record"] = await NextRecordAsync(connection, transaction, definition, scope, metadata.Columns["Record"].Type);

        foreach (string key in definition.KeyColumns)
            if (row[key] == DBNull.Value || row[key] == null)
                throw new InvalidOperationException($"Укажите поле ключа «{key}» перед сохранением.");

        foreach (var column in metadata.Columns.Values.Where(column => !column.Nullable && !column.Identity && !column.Computed && !column.HasDefault))
        {
            if (!row.Table.Columns.Contains(column.Name) || row[column.Name] == DBNull.Value || row[column.Name] == null)
                throw new InvalidOperationException($"Заполните обязательное поле «{column.Name}».");
            if (row[column.Name] is string text && string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"Заполните обязательное поле «{column.Name}».");
        }

        var insertColumns = new List<string>();
        var insertValues = new List<string>();
        using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        foreach (DataColumn column in row.Table.Columns)
        {
            string name = column.ColumnName;
            if (metadata.Columns[name].Identity || metadata.Columns[name].Computed) continue;
            bool included = definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                || definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                || definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                || definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
            if (!included) continue;
            if ((row[name] == DBNull.Value || row[name] == null) && metadata.Columns[name].HasDefault && !definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            insertColumns.Add(Quote(name));
            string parameter = "@v" + insertValues.Count;
            insertValues.Add(parameter);
            command.Parameters.AddWithValue(parameter, row[name] ?? DBNull.Value);
        }
        command.CommandText = $"DECLARE @affected TABLE (Marker int NOT NULL); INSERT INTO {QuoteTable(definition.TableName)} ({string.Join(", ", insertColumns)}) OUTPUT 1 INTO @affected(Marker) VALUES ({string.Join(", ", insertValues)}); SELECT COUNT(*) FROM @affected;";
        if (await ExecuteAffectedCountAsync(command) != 1)
            throw new InvalidOperationException("Добавление не затронуло ровно одну строку; транзакция отменена.");
    }

    private async Task UpdateAsync(SqlConnection connection, SqlTransaction transaction, ProgramSupportTableData tableData,
        DataRow row, IReadOnlyDictionary<string, object> scope, TableMetadata metadata)
    {
        var definition = tableData.Definition;
        var changed = definition.EditableColumns
            .Where(name => row.Table.Columns.Contains(name)
                && !definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !ValuesEqual(row[name, DataRowVersion.Original], row[name, DataRowVersion.Current]))
            .ToList();
        var forbidden = row.Table.Columns.Cast<DataColumn>()
            .Where(column => !ValuesEqual(row[column, DataRowVersion.Original], row[column, DataRowVersion.Current]))
            .Select(column => column.ColumnName)
            .Where(name => !changed.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (forbidden.Length > 0)
            throw new InvalidOperationException($"Изменение ключей, связей или полей без разрешённой записи заблокировано: {string.Join(", ", forbidden)}.");
        if (changed.Count == 0) return;

        using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        var assignments = new List<string>();
        for (int i = 0; i < changed.Count; i++)
        {
            assignments.Add($"{Quote(changed[i])}=@n{i}");
            command.Parameters.AddWithValue("@n" + i, row[changed[i]] ?? DBNull.Value);
        }
        var predicates = AddKeyAndOriginalPredicates(command, definition, row, scope, changed);
        command.CommandText = $"DECLARE @affected TABLE (Marker int NOT NULL); UPDATE {QuoteTable(definition.TableName)} SET {string.Join(", ", assignments)} OUTPUT 1 INTO @affected(Marker) WHERE {predicates}; SELECT COUNT(*) FROM @affected;";
        if (await ExecuteAffectedCountAsync(command) != 1)
            throw new InvalidOperationException("Строка изменилась после загрузки либо ключ не уникален. Обновите список; транзакция отменена.");
    }

    private async Task DeleteAsync(SqlConnection connection, SqlTransaction transaction, ProgramSupportTableData tableData,
        DataRow row, IReadOnlyDictionary<string, object> scope, TableMetadata metadata)
    {
        var definition = tableData.Definition;
        if (!definition.AllowDelete) throw new InvalidOperationException($"Удаление строк из {definition.Caption} отключено.");
        var dependencies = await CountKnownDependentsAsync(connection, transaction, definition, row);
        if (dependencies > 0)
            throw new InvalidOperationException($"Удаление заблокировано: найдены зависимые строки ({dependencies}). Сначала измените или удалите связи.");
        await EnsureNoForeignKeyDependentsAsync(connection, transaction, definition, row);

        using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        string predicates = AddKeyAndOriginalPredicates(command, definition, row, scope, Array.Empty<string>(), DataRowVersion.Original);
        command.CommandText = $"DECLARE @affected TABLE (Marker int NOT NULL); DELETE FROM {QuoteTable(definition.TableName)} OUTPUT 1 INTO @affected(Marker) WHERE {predicates}; SELECT COUNT(*) FROM @affected;";
        if (await ExecuteAffectedCountAsync(command) != 1)
            throw new InvalidOperationException("Удаление не затронуло ровно одну строку; транзакция отменена.");
    }

    private static async Task<int> ExecuteAffectedCountAsync(SqlCommand command)
    {
        object? result = await command.ExecuteScalarAsync();
        return result == null || result == DBNull.Value
            ? 0
            : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountKnownDependentsAsync(SqlConnection connection, SqlTransaction transaction,
        ProgramSupportTableDefinition definition, DataRow row)
    {
        string? childTable = null;
        string? childField = null;
        string? targetField = null;
        switch (definition.TableName)
        {
            case "dbo.Prog_Recipe": childTable = "dbo.Prog_Recipes"; childField = "Recipe_Record"; targetField = "Record"; break;
            case "dbo.Prog_Queue": childTable = "dbo.Prog_QueueSelections"; childField = "Queue_Record"; targetField = "Record"; break;
        }
        if (childTable == null || childField == null || targetField == null) return 0;
        string scopePredicate = definition.ScopeColumns.Select((name, i) => $"{Quote(name)}=@s{i}").Aggregate((a, b) => a + " AND " + b);
        string sql = $"SELECT COUNT_BIG(1) FROM {QuoteTable(childTable)} WHERE {scopePredicate} AND {Quote(childField)}=@target;";
        using var command = new SqlCommand(sql, connection, transaction);
        for (int i = 0; i < definition.ScopeColumns.Count; i++)
            command.Parameters.AddWithValue("@s" + i, row[definition.ScopeColumns[i], DataRowVersion.Original]);
        command.Parameters.AddWithValue("@target", row[targetField, DataRowVersion.Original]);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task EnsureNoForeignKeyDependentsAsync(SqlConnection connection, SqlTransaction transaction,
        ProgramSupportTableDefinition definition, DataRow row)
    {
        const string fkSql = @"
SELECT fk.object_id, s.name AS SchemaName, t.name AS TableName, cc.name AS ChildColumn, pc.name AS ParentColumn,
       fkc.constraint_column_id
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables t ON fkc.parent_object_id = t.object_id
JOIN sys.schemas s ON t.schema_id = s.schema_id
JOIN sys.columns cc ON cc.object_id = fkc.parent_object_id AND cc.column_id = fkc.parent_column_id
JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id AND pc.column_id = fkc.referenced_column_id
WHERE fkc.referenced_object_id = OBJECT_ID(@table) AND fkc.parent_object_id <> fkc.referenced_object_id
ORDER BY fk.object_id, fkc.constraint_column_id;";
        using var metadataCommand = new SqlCommand(fkSql, connection, transaction);
        metadataCommand.Parameters.AddWithValue("@table", definition.TableName);
        using var reader = await metadataCommand.ExecuteReaderAsync();
        var groups = new Dictionary<string, List<(string Schema, string Table, string Child, string Parent)>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            string fk = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (!groups.TryGetValue(fk, out var columns)) groups[fk] = columns = new();
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        await reader.CloseAsync();

        foreach (var relation in groups.Values)
        {
            if (relation.Any(column => !row.Table.Columns.Contains(column.Parent))) continue;
            string where = string.Join(" AND ", relation.Select((column, index) => $"{Quote(column.Child)}=@r{index}"));
            string childTable = Quote(relation[0].Schema) + "." + Quote(relation[0].Table);
            using var count = new SqlCommand($"SELECT TOP (1) 1 FROM {childTable} WHERE {where};", connection, transaction);
            for (int i = 0; i < relation.Count; i++)
                count.Parameters.AddWithValue("@r" + i, row[relation[i].Parent, DataRowVersion.Original]);
            if (await count.ExecuteScalarAsync() != null)
                throw new InvalidOperationException($"Удаление заблокировано внешним ключом: {relation[0].Table} содержит зависимые строки.");
        }
    }

    private static string AddKeyAndOriginalPredicates(SqlCommand command, ProgramSupportTableDefinition definition,
        DataRow row, IReadOnlyDictionary<string, object> scope, IReadOnlyList<string> changed,
        DataRowVersion rowVersion = DataRowVersion.Current)
    {
        int index = 0;
        var predicates = new List<string>();
        foreach (string name in definition.KeyColumns.Concat(definition.ScopeColumns).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var value = row[name, rowVersion];
            predicates.Add(AddPredicate(command, name, value, "k" + index++));
        }
        foreach (string name in changed)
        {
            var value = row[name, DataRowVersion.Original];
            predicates.Add(AddPredicate(command, name, value, "o" + index++));
        }
        return string.Join(" AND ", predicates);
    }

    private static string AddPredicate(SqlCommand command, string column, object value, string parameter)
    {
        if (value == null || value == DBNull.Value) return $"{Quote(column)} IS NULL";
        command.Parameters.AddWithValue("@" + parameter, value);
        return $"{Quote(column)}=@{parameter}";
    }

    private static async Task<object> NextRecordAsync(SqlConnection connection, SqlTransaction transaction,
        ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope, Type recordType)
    {
        string where = string.Join(" AND ", definition.ScopeColumns.Select((column, index) => $"{Quote(column)}=@s{index}"));
        using var command = new SqlCommand($"SELECT COALESCE(MAX(TRY_CONVERT(bigint,{Quote("Record")})),0)+1 FROM {QuoteTable(definition.TableName)} WITH (UPDLOCK,HOLDLOCK) WHERE {where};", connection, transaction);
        AddScopeParameters(command, definition, scope);
        object next = await command.ExecuteScalarAsync() ?? 1L;
        return Convert.ChangeType(next, recordType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<TableMetadata> GetMetadataAsync(SqlConnection connection, SqlTransaction? transaction,
        ProgramSupportTableDefinition definition)
    {
        const string columnSql = @"
SELECT c.name, t.name AS TypeName, c.is_nullable, c.is_identity, c.is_computed,
       CASE WHEN dc.object_id IS NULL THEN 0 ELSE 1 END AS HasDefault
FROM sys.columns c
JOIN sys.types t ON c.user_type_id=t.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
WHERE c.object_id=OBJECT_ID(@table);";
        using var columnCommand = new SqlCommand(columnSql, connection, transaction);
        columnCommand.Parameters.AddWithValue("@table", definition.TableName);
        using var columnReader = await columnCommand.ExecuteReaderAsync();
        var columns = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
        while (await columnReader.ReadAsync())
        {
            var name = columnReader.GetString(0);
            columns[name] = new ColumnMetadata(name, columnReader.GetString(1), columnReader.GetBoolean(2),
                columnReader.GetBoolean(3), columnReader.GetBoolean(4), columnReader.GetBoolean(5));
        }
        await columnReader.CloseAsync();
        if (columns.Count == 0) throw new InvalidOperationException($"Таблица {definition.TableName} отсутствует в текущей базе.");

        const string keySql = @"
SELECT i.index_id, c.name, ic.key_ordinal
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.object_id=OBJECT_ID(@table) AND i.is_unique=1 AND i.is_hypothetical=0 AND i.has_filter=0 AND ic.key_ordinal>0
ORDER BY i.index_id, ic.key_ordinal;";
        using var keyCommand = new SqlCommand(keySql, connection, transaction);
        keyCommand.Parameters.AddWithValue("@table", definition.TableName);
        using var keyReader = await keyCommand.ExecuteReaderAsync();
        var keyGroups = new Dictionary<int, List<string>>();
        while (await keyReader.ReadAsync())
        {
            int indexId = keyReader.GetInt32(0);
            if (!keyGroups.TryGetValue(indexId, out var key)) keyGroups[indexId] = key = new();
            key.Add(keyReader.GetString(1));
        }
        await keyReader.CloseAsync();
        return new TableMetadata(columns, keyGroups.Values.Select(key => (IReadOnlyList<string>)key).ToArray());
    }

    private static void EnsureWriteKey(ProgramSupportTableDefinition definition, IReadOnlyList<IReadOnlyList<string>> actualKeys)
    {
        if (definition.ReadOnly || definition.KeyColumns.Count == 0 || !actualKeys.Any(key => SameColumns(key, definition.KeyColumns)))
            throw new InvalidOperationException(definition.ReadOnlyReason ?? $"Запись в {definition.TableName} остановлена: уникальный ключ отличается от схемы backup.");
    }

    private static bool SameColumns(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static void ValidateScope(ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope,
        IReadOnlyDictionary<string, ColumnMetadata> columns)
    {
        foreach (string name in definition.ScopeColumns)
        {
            if (!columns.ContainsKey(name)) throw new InvalidOperationException($"В {definition.TableName} отсутствует контекстный столбец {name}; операция остановлена.");
            if (!scope.TryGetValue(name, out var value) || value == null || value == DBNull.Value)
                throw new InvalidOperationException($"Контекст «{name}» не выбран; операция остановлена.");
        }
        foreach (string name in definition.KeyColumns)
            if (!columns.ContainsKey(name)) throw new InvalidOperationException($"В {definition.TableName} отсутствует ключевой столбец {name}; операция остановлена.");
    }

    private static void AddScopeParameters(SqlCommand command, ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope)
    {
        for (int i = 0; i < definition.ScopeColumns.Count; i++)
            command.Parameters.AddWithValue("@s" + i, scope[definition.ScopeColumns[i]]);
    }

    private static ProgramSupportTableDefinition Find(IReadOnlyList<ProgramSupportTableDefinition> catalog, string id) =>
        catalog.FirstOrDefault(table => table.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Неизвестен раздел рабочих данных.", nameof(id));

    private static bool ValuesEqual(object left, object right) =>
        left == DBNull.Value ? right == DBNull.Value : right != DBNull.Value && Equals(left, right);

    private static string QuoteTable(string tableName)
    {
        var parts = tableName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new InvalidOperationException("Разрешены только таблицы схемы dbo.");
        return Quote(parts[0]) + "." + Quote(parts[1]);
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private sealed record ColumnMetadata(string Name, string TypeName, bool Nullable, bool Identity, bool Computed, bool HasDefault)
    {
        public Type Type => TypeName.ToLowerInvariant() switch
        {
            "int" => typeof(int), "bigint" => typeof(long), "smallint" => typeof(short), "tinyint" => typeof(byte),
            "bit" => typeof(bool), "real" => typeof(float), "float" => typeof(double), "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
            "date" => typeof(DateTime), "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
            _ => typeof(string)
        };
    }

    private sealed record TableMetadata(Dictionary<string, ColumnMetadata> Columns, IReadOnlyList<IReadOnlyList<string>> UniqueKeys);
}
