using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Configurator;

/// <summary>
/// Read and compare-and-swap editing for the first Program/Sequence/Step surfaces.
/// Every edit is matched against the complete original row and committed by
/// DatabaseService inside a serializable transaction; ambiguous rows fail closed.
/// </summary>
public sealed class ProgramManagementRepository
{
    public const string ProgramTable = "dbo.Prog";
    public const string SequenceTable = "dbo.Prog_Seq";
    public const string StepTable = "dbo.Prog_Step";
    public const string TransitionTable = "dbo.Prog_StepTransition";
    public const string ParameterTable = "dbo.Prog_StepParam";

    private static readonly Dictionary<string, string[]> UniqueKeyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProgramTable] = new[] { "Record", "PLC", "PLC_Class_Number" },
        [SequenceTable] = new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record" },
        [StepTable] = new[] { "Record", "PLC", "PLC_Class_Number" },
        [TransitionTable] = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number" },
        [ParameterTable] = new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Step_Number", "Type" }
    };

    private readonly DatabaseService _database;
    private readonly string _connectionString;

    public ProgramManagementRepository(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _database = new DatabaseService(_connectionString);
    }

    public Task<DataTable> GetProgramsAsync(object plc, object programClass, CancellationToken ct = default)
        => GetScopedTableAsync(ProgramTable, Context(plc, programClass), ct);

    public Task<DataTable> GetSequencesAsync(ProgramContext context, CancellationToken ct = default)
        => GetScopedTableAsync(SequenceTable, ProgramScope(context, includeSequence: false), ct);

    public Task<DataTable> GetStepsAsync(ProgramContext context, CancellationToken ct = default)
        => GetReferencedStepsAsync(context, ct);

    public Task<DataTable> GetTransitionsAsync(ProgramContext context, CancellationToken ct = default)
        => GetScopedTableAsync(TransitionTable, ProgramScope(context, includeSequence: true), ct);

    public Task<DataTable> GetParametersAsync(ProgramContext context, int stepNumber, CancellationToken ct = default)
    {
        var scope = ProgramScope(context, includeSequence: true);
        scope["Step_Number"] = stepNumber;
        return GetScopedTableAsync(ParameterTable, scope, ct);
    }

    public async Task<HashSet<string>> GetNonEditableColumnsAsync(string tableName, CancellationToken ct = default)
    {
        var columns = await _database.GetTableColumnsAsync(tableName, ct);
        return columns.Where(column => column.IsIdentity || column.IsReadOnly)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<ColumnInfo>> GetInsertColumnsAsync(string tableName, IEnumerable<string> generatedColumns, CancellationToken ct = default)
    {
        var generated = new HashSet<string>(generatedColumns ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var columns = await _database.GetTableColumnsAsync(tableName, ct);
        return columns.Where(column => !column.IsIdentity && !column.IsReadOnly && !generated.Contains(column.Name) &&
                (!column.IsNullable && !column.HasDefault || column.Name.Equals("Name_L1", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public Task<int> AddProgramAsync(object plc, object programClass, IReadOnlyDictionary<string, object> values, CancellationToken ct = default)
    {
        var scope = Context(plc, programClass);
        return InsertWithGeneratedRecordAsync(ProgramTable, "Record", scope,
            new[] { "Record", "PLC", "PLC_Class_Number" }, values, ct);
    }

    public Task<int> AddSequenceAsync(ProgramContext context, IReadOnlyDictionary<string, object> values, CancellationToken ct = default)
    {
        EnsureProgram(context);
        var scope = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Prog_Number"] = context.ClassNumberValue,
            ["Prog_Record"] = context.RecordValue
        };
        return InsertWithGeneratedRecordAsync(SequenceTable, "Record", scope,
            new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record" }, values, ct);
    }

    public Task<int> AddStepToSequenceAsync(ProgramContext context, object stepClass, object stepRecord,
        IReadOnlyDictionary<string, object> values, CancellationToken ct = default)
    {
        var scope = ProgramScope(context, includeSequence: true);
        var valuesWithStepReference = values?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        valuesWithStepReference["PLC_Class_Step_Number"] = stepClass;
        valuesWithStepReference["Step_Record"] = stepRecord;
        return InsertWithGeneratedRecordAsync(TransitionTable, "SeqStep_Number", scope,
            new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number" }, valuesWithStepReference, ct);
    }

    public Task<DataTable> GetStepChoicesAsync(object plc, CancellationToken ct = default)
        => GetScopedTableAsync(StepTable, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["PLC"] = plc }, ct);

    public async Task DeleteProgramAsync(ProgramContext context, CancellationToken ct = default)
    {
        EnsureProgram(context);
        var childScope = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Prog_Number"] = context.ClassNumberValue,
            ["Prog_Record"] = context.RecordValue
        };
        var dependentTables = new (string Table, IReadOnlyDictionary<string, object> Scope)[]
        {
            ("dbo.Prog_Seq", childScope), (TransitionTable, childScope), (ParameterTable, childScope),
            ("dbo.Prog_Recipe", childScope), ("dbo.Prog_Recipes", childScope), ("dbo.Prog_Queue", childScope),
            ("dbo.Prog_QueueSelections", childScope), ("dbo.Prog_SoftKey", childScope), ("dbo.Prog_Msg", childScope)
        };
        await DeleteIfUnreferencedAsync(ProgramTable, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Number"] = context.ClassNumberValue,
            ["Record"] = context.RecordValue
        }, new[] { "Record", "PLC", "PLC_Class_Number" }, dependentTables, ct);
    }

    public async Task DeleteSequenceAsync(ProgramContext context, object sequenceRecord, CancellationToken ct = default)
    {
        EnsureProgram(context);
        var scope = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Prog_Number"] = context.ClassNumberValue,
            ["Prog_Record"] = context.RecordValue,
            ["Seq_Record"] = sequenceRecord
        };
        var dependentTables = new (string Table, IReadOnlyDictionary<string, object> Scope)[]
        {
            (TransitionTable, scope), (ParameterTable, scope), ("dbo.Prog_Recipe", scope),
            ("dbo.Prog_Recipes", scope), ("dbo.Prog_QueueSelections", scope)
        };
        await DeleteIfUnreferencedAsync(SequenceTable, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Prog_Number"] = context.ClassNumberValue,
            ["Prog_Record"] = context.RecordValue,
            ["Record"] = sequenceRecord
        }, new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record" }, dependentTables, ct);
    }

    public async Task DeleteStepFromSequenceAsync(ProgramContext context, object sequenceStepNumber, CancellationToken ct = default)
    {
        var scope = ProgramScope(context, includeSequence: true);
        var parameterScope = new Dictionary<string, object>(scope, StringComparer.OrdinalIgnoreCase)
        {
            ["Step_Number"] = sequenceStepNumber
        };
        var dependentTables = new (string Table, IReadOnlyDictionary<string, object> Scope)[]
        {
            (ParameterTable, parameterScope), ("dbo.Prog_Recipes", parameterScope)
        };
        await DeleteIfUnreferencedAsync(TransitionTable, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Prog_Number"] = context.ClassNumberValue,
            ["Prog_Record"] = context.RecordValue,
            ["Seq_Record"] = context.SeqRecordValue,
            ["SeqStep_Number"] = sequenceStepNumber
        }, new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number" }, dependentTables, ct,
            stepNumberForDestinations: sequenceStepNumber);
    }

    public Task SaveRowAsync(string tableName, IReadOnlyDictionary<string, object> original,
        IReadOnlyDictionary<string, object> updated, CancellationToken ct = default)
    {
        if (original == null || original.Count == 0)
            throw new InvalidOperationException("Не удалось определить исходное состояние строки.");
        if (updated == null || updated.Count == 0)
            throw new InvalidOperationException("Нет изменений для сохранения.");

        var changes = updated
            .Where(pair => original.TryGetValue(pair.Key, out var oldValue) && !ValuesEqual(oldValue, pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (changes.Count == 0)
            throw new InvalidOperationException("Строка не изменена.");

        return UpdateCompareAndSwapAsync(tableName, original, changes, ct);
    }

    private async Task UpdateCompareAndSwapAsync(string tableName, IReadOnlyDictionary<string, object> original,
        IReadOnlyDictionary<string, object> changes, CancellationToken ct)
    {
        if (!UniqueKeyColumns.TryGetValue(tableName, out var uniqueKey))
            throw new InvalidOperationException("Редактирование этой таблицы не поддерживается.");
        var columns = await _database.GetTableColumnsAsync(tableName, ct);
        RequireColumns(columns, original.Keys.Concat(changes.Keys).Concat(uniqueKey), tableName);
        foreach (var field in changes.Keys)
        {
            var column = columns.First(item => item.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (uniqueKey.Contains(field) || column.IsIdentity || column.IsReadOnly)
                throw new InvalidOperationException($"Поле {field} защищено и не может быть изменено.");
        }

        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await EnsureUniqueIndexAsync(connection, transaction, tableName, uniqueKey, ct);
            if (await CountRowsAsync(connection, transaction, tableName, original, ct) != 1)
                throw new InvalidOperationException("Исходное состояние не указывает ровно на одну строку; сохранение отменено.");

            string setClause = string.Join(", ", changes.Keys.Select((name, index) => QuoteIdentifier(name) + " = @value" + index));
            string where = BuildPredicates(original, "old", out var originalParameters);
            using (var command = new Microsoft.Data.SqlClient.SqlCommand(
                $"UPDATE {QuoteTable(tableName)} WITH (UPDLOCK, HOLDLOCK) SET {setClause} WHERE {where}", connection, transaction))
            {
                int index = 0;
                foreach (var value in changes.Values)
                    command.Parameters.AddWithValue("@value" + index++, value ?? DBNull.Value);
                AddParameters(command, originalParameters);
                await command.ExecuteNonQueryAsync(ct);
            }

            var expected = original.Where(pair =>
            {
                var column = columns.First(item => item.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                return !column.IsComputed && !column.IsReadOnly;
            }).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes) expected[change.Key] = change.Value;
            if (await CountRowsAsync(connection, transaction, tableName, expected, ct) != 1)
                throw new InvalidOperationException("После сохранения состояние строки нельзя подтвердить однозначно; изменение отменено.");

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<long> CountRowsAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        Microsoft.Data.SqlClient.SqlTransaction transaction, string tableName,
        IReadOnlyDictionary<string, object> values, CancellationToken ct)
    {
        var where = BuildPredicates(values, "row", out var parameters);
        using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT COUNT_BIG(*) FROM {QuoteTable(tableName)} WITH (UPDLOCK, HOLDLOCK) WHERE {where}", connection, transaction);
        AddParameters(command, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task<int> InsertWithGeneratedRecordAsync(string tableName, string generatedColumn,
        IReadOnlyDictionary<string, object> scope, IReadOnlyList<string> uniqueKey,
        IReadOnlyDictionary<string, object> suppliedValues, CancellationToken ct)
    {
        var columns = await _database.GetTableColumnsAsync(tableName, ct);
        RequireColumns(columns, uniqueKey.Concat(scope.Keys).Concat(suppliedValues?.Keys ?? Array.Empty<string>()), tableName);
        var byName = columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        if (!byName.TryGetValue(generatedColumn, out var generatedInfo) || generatedInfo.IsIdentity || generatedInfo.IsComputed)
            throw new InvalidOperationException($"Не удалось безопасно сформировать {tableName}.{generatedColumn}.");

        var values = scope.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        values[generatedColumn] = null;
        foreach (var pair in suppliedValues ?? new Dictionary<string, object>())
        {
            if (uniqueKey.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || scope.ContainsKey(pair.Key) || pair.Key.Equals(generatedColumn, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Поле {pair.Key} задаётся приложением и не может быть передано из формы.");
            if (!byName.TryGetValue(pair.Key, out var column) || column.IsIdentity || column.IsReadOnly)
                throw new InvalidOperationException($"Поле {pair.Key} нельзя вставить в {tableName}.");
            values[pair.Key] = pair.Value ?? DBNull.Value;
        }

        var missing = columns.Where(column => !column.IsNullable && !column.HasDefault && !column.IsIdentity &&
                !column.IsReadOnly && !values.ContainsKey(column.Name))
            .Select(column => column.Name).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("Для создания заполните обязательные поля: " + string.Join(", ", missing) + ".");

        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await EnsureUniqueIndexAsync(connection, transaction, tableName, uniqueKey, ct);
            var predicates = BuildPredicates(scope, "scope", out var parameters);
            using (var command = new Microsoft.Data.SqlClient.SqlCommand(
                $"SELECT MAX({QuoteIdentifier(generatedColumn)}) FROM {QuoteTable(tableName)} WITH (UPDLOCK, HOLDLOCK) WHERE {predicates}", connection, transaction))
            {
                AddParameters(command, parameters);
                var current = await command.ExecuteScalarAsync(ct);
                int next = current == null || current == DBNull.Value ? 1 : checked(Convert.ToInt32(current, CultureInfo.InvariantCulture) + 1);
                values[generatedColumn] = next;
            }

            var insertColumns = values.Keys.ToList();
            string columnSql = string.Join(", ", insertColumns.Select(QuoteIdentifier));
            string parameterSql = string.Join(", ", insertColumns.Select((_, index) => "@v" + index));
            using (var command = new Microsoft.Data.SqlClient.SqlCommand(
                $"INSERT INTO {QuoteTable(tableName)} ({columnSql}) VALUES ({parameterSql})", connection, transaction))
            {
                for (int index = 0; index < insertColumns.Count; index++)
                    command.Parameters.AddWithValue("@v" + index, values[insertColumns[index]] ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(ct);
            }

            var generatedKey = uniqueKey.ToDictionary(key => key, key => values[key], StringComparer.OrdinalIgnoreCase);
            if (await CountRowsAsync(connection, transaction, tableName, generatedKey, ct) != 1)
                throw new InvalidOperationException("Вставленную строку не удалось подтвердить по уникальному ключу; транзакция отменена.");

            await transaction.CommitAsync(ct);
            return Convert.ToInt32(values[generatedColumn], CultureInfo.InvariantCulture);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task DeleteIfUnreferencedAsync(string tableName, IReadOnlyDictionary<string, object> keyValues,
        IReadOnlyList<string> uniqueKey,
        IReadOnlyList<(string Table, IReadOnlyDictionary<string, object> Scope)> dependencies,
        CancellationToken ct, object stepNumberForDestinations = null)
    {
        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await EnsureUniqueIndexAsync(connection, transaction, tableName, uniqueKey, ct);
            if (stepNumberForDestinations != null)
                await EnsureNoDestinationReferencesAsync(connection, transaction, keyValues, stepNumberForDestinations, ct);

            foreach (var dependency in dependencies)
                if (await HasScopedRowsAsync(connection, transaction, dependency.Table, dependency.Scope, ct))
                    throw new InvalidOperationException($"Удаление заблокировано: найдены зависимые строки в {dependency.Table}. Сначала перенесите или удалите их штатным действием.");

            var where = BuildPredicates(keyValues, "key", out var parameters);
            if (await CountRowsAsync(connection, transaction, tableName, keyValues, ct) != 1)
                throw new InvalidOperationException("Строка не найдена однозначно по ключу; удаление отменено.");
            using var command = new Microsoft.Data.SqlClient.SqlCommand(
                $"DELETE FROM {QuoteTable(tableName)} WITH (UPDLOCK, HOLDLOCK) WHERE {where}", connection, transaction);
            AddParameters(command, parameters);
            await command.ExecuteNonQueryAsync(ct);
            if (await CountRowsAsync(connection, transaction, tableName, keyValues, ct) != 0)
                throw new InvalidOperationException("Удаление не подтвердилось по уникальному ключу; транзакция отменена.");

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> HasScopedRowsAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        Microsoft.Data.SqlClient.SqlTransaction transaction, string tableName,
        IReadOnlyDictionary<string, object> scope, CancellationToken ct)
    {
        var columns = await _database.GetTableColumnsAsync(tableName, ct);
        if (columns.Count == 0) return false;
        RequireColumns(columns, scope.Keys, tableName);
        var where = BuildPredicates(scope, "dep", out var parameters);
        using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT TOP (1) 1 FROM {QuoteTable(tableName)} WITH (UPDLOCK, HOLDLOCK) WHERE {where}", connection, transaction);
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync(ct) != null;
    }

    private async Task EnsureNoDestinationReferencesAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        Microsoft.Data.SqlClient.SqlTransaction transaction, IReadOnlyDictionary<string, object> target,
        object stepNumber, CancellationToken ct)
    {
        var columns = await _database.GetTableColumnsAsync(TransitionTable, ct);
        var destinations = columns.Where(column => column.Name.StartsWith("TransDest_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (destinations.Count == 0)
            throw new InvalidOperationException("Удаление шага заблокировано: поля назначения переходов не подтверждены в текущей схеме.");

        var scope = target.Where(pair => !pair.Key.Equals("SeqStep_Number", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        RequireColumns(columns, scope.Keys.Append("SeqStep_Number"), TransitionTable);
        var where = BuildPredicates(scope, "dest", out var parameters);
        var destinationPredicates = new List<string>();
        foreach (var column in destinations)
        {
            if (!IsNumericOrText(column.DataType))
                throw new InvalidOperationException($"Удаление шага заблокировано: тип {TransitionTable}.{column.Name} не поддерживает безопасную проверку ссылок.");
            destinationPredicates.Add($"TRY_CONVERT(int, {QuoteIdentifier(column.Name)}) = @targetStep");
        }

        using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT TOP (1) 1 FROM {QuoteTable(TransitionTable)} WITH (UPDLOCK, HOLDLOCK) WHERE {where} AND {QuoteIdentifier("SeqStep_Number")} <> @targetSequenceStep AND ({string.Join(" OR ", destinationPredicates)})",
            connection, transaction);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@targetStep", stepNumber);
        command.Parameters.AddWithValue("@targetSequenceStep", stepNumber);
        if (await command.ExecuteScalarAsync(ct) != null)
            throw new InvalidOperationException("Удаление шага заблокировано: другой переход указывает на этот шаг.");
    }

    private static async Task EnsureUniqueIndexAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        Microsoft.Data.SqlClient.SqlTransaction transaction, string tableName,
        IReadOnlyList<string> expectedColumns, CancellationToken ct)
    {
        var (schema, table) = SplitTableName(tableName);
        const string sql = @"
SELECT i.index_id, c.name
FROM sys.indexes AS i
JOIN sys.tables AS t ON t.object_id = i.object_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE s.name = @schema AND t.name = @table
  AND i.is_unique = 1 AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.has_filter = 0
  AND ic.is_included_column = 0
ORDER BY i.index_id, ic.key_ordinal;";
        using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        var indexes = new Dictionary<int, List<string>>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int indexId = reader.GetInt32(0);
            if (!indexes.TryGetValue(indexId, out var columns)) indexes[indexId] = columns = new List<string>();
            columns.Add(reader.GetString(1));
        }
        bool matches = indexes.Values.Any(columns => columns.SequenceEqual(expectedColumns, StringComparer.OrdinalIgnoreCase));
        if (!matches)
            throw new InvalidOperationException($"Изменение {tableName} заблокировано: уникальный индекс не совпал с подтверждённым ключом ({string.Join(", ", expectedColumns)}).");
    }

    private static void RequireColumns(IReadOnlyList<ColumnInfo> columns, IEnumerable<string> required, string tableName)
    {
        var missing = required.Where(name => !columns.Any(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Операция в {tableName} заблокирована: в текущей схеме отсутствуют поля {string.Join(", ", missing)}.");
    }

    private static string BuildPredicates(IReadOnlyDictionary<string, object> values, string parameterPrefix,
        out List<(string Name, object Value)> parameters)
    {
        parameters = new List<(string, object)>();
        var predicates = new List<string>();
        foreach (var pair in values)
        {
            if (pair.Value == null || pair.Value == DBNull.Value)
                predicates.Add(QuoteIdentifier(pair.Key) + " IS NULL");
            else
            {
                string parameter = "@" + parameterPrefix + parameters.Count.ToString(CultureInfo.InvariantCulture);
                predicates.Add(QuoteIdentifier(pair.Key) + " = " + parameter);
                parameters.Add((parameter, pair.Value));
            }
        }
        return string.Join(" AND ", predicates);
    }

    private static void AddParameters(Microsoft.Data.SqlClient.SqlCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
    }

    private static bool IsNumericOrText(string sqlType) => sqlType?.ToLowerInvariant() is
        "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "float" or "real" or
        "varchar" or "nvarchar" or "char" or "nchar";

    private static (string Schema, string Table) SplitTableName(string tableName)
    {
        var parts = tableName.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    private async Task<DataTable> GetScopedTableAsync(string tableName,
        IReadOnlyDictionary<string, object> requestedScope, CancellationToken ct)
    {
        var columns = await _database.GetTableColumnsAsync(tableName, ct);
        if (columns.Count == 0) return new DataTable();

        var actual = columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var predicates = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        foreach (var requested in requestedScope)
        {
            if (!actual.TryGetValue(requested.Key, out var column)) continue;
            var identifier = QuoteIdentifier(column.Name);
            if (requested.Value == null || requested.Value == DBNull.Value)
                predicates.Add(identifier + " IS NULL");
            else
            {
                var parameter = "@p" + parameters.Count;
                predicates.Add(identifier + " = " + parameter);
                parameters.Add((parameter, requested.Value));
            }
        }

        // A missing context column would otherwise turn a scoped query into a table scan.
        // Require every supplied identifier to exist before returning rows.
        var missing = requestedScope.Keys.Where(key => !actual.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"В {tableName} нет полей контекста: {string.Join(", ", missing)}.");

        string order = string.Join(", ", columns
            .Where(column => requestedScope.ContainsKey(column.Name) ||
                column.Name.Equals("Record", StringComparison.OrdinalIgnoreCase) ||
                column.Name.Equals("Seq_Record", StringComparison.OrdinalIgnoreCase) ||
                column.Name.Equals("Step_Number", StringComparison.OrdinalIgnoreCase))
            .Select(column => QuoteIdentifier(column.Name)));
        if (string.IsNullOrEmpty(order)) order = QuoteIdentifier(columns[0].Name);

        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT TOP (1000) * FROM {QuoteTable(tableName)} WHERE {string.Join(" AND ", predicates)} ORDER BY {order}", connection)
        { CommandTimeout = 120 };
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var result = new DataTable();
        result.Load(reader);
        return result;
    }

    private async Task<DataTable> GetReferencedStepsAsync(ProgramContext context, CancellationToken ct)
    {
        var scope = ProgramScope(context, includeSequence: true);
        var transitions = await _database.GetTableColumnsAsync(TransitionTable, ct);
        var steps = await _database.GetTableColumnsAsync(StepTable, ct);
        foreach (var required in new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "PLC_Class_Step_Number", "Step_Record" })
            if (!transitions.Any(column => column.Name.Equals(required, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"В {TransitionTable} нет поля {required}.");
        foreach (var required in new[] { "PLC", "PLC_Class_Number", "Record" })
            if (!steps.Any(column => column.Name.Equals(required, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"В {StepTable} нет поля {required}.");

        const string sql = @"
SELECT DISTINCT s.*
FROM [dbo].[Prog_StepTransition] AS t
INNER JOIN [dbo].[Prog_Step] AS s
    ON s.[PLC] = t.[PLC]
   AND s.[PLC_Class_Number] = t.[PLC_Class_Step_Number]
   AND s.[Record] = t.[Step_Record]
WHERE t.[PLC] = @plc
  AND t.[PLC_Class_Prog_Number] = @class
  AND t.[Prog_Record] = @program
  AND t.[Seq_Record] = @sequence
ORDER BY s.[Record];";
        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@plc", scope["PLC"]);
        command.Parameters.AddWithValue("@class", scope["PLC_Class_Prog_Number"]);
        command.Parameters.AddWithValue("@program", scope["Prog_Record"]);
        command.Parameters.AddWithValue("@sequence", scope["Seq_Record"]);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var result = new DataTable();
        result.Load(reader);
        return result;
    }

    private static Dictionary<string, object> Context(object plc, object classNumber)
    {
        if (plc == null || classNumber == null)
            throw new InvalidOperationException("Выберите PLC и класс программы.");
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = plc,
            ["PLC_Class_Number"] = classNumber
        };
    }

    private static Dictionary<string, object> ProgramScope(ProgramContext context, bool includeSequence)
    {
        EnsureProgram(context);
        var scope = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Prog_Number"] = context.ClassNumberValue,
            ["Prog_Record"] = context.RecordValue
        };
        if (includeSequence)
        {
            if (context.SeqRecordValue == null || context.SeqRecordValue == DBNull.Value)
                throw new InvalidOperationException("Сначала выберите последовательность.");
            scope["Seq_Record"] = context.SeqRecordValue;
        }
        return scope;
    }

    private static void EnsureProgram(ProgramContext context)
    {
        if (context?.PlcValue == null || context.ClassNumberValue == null || context.RecordValue == null)
            throw new InvalidOperationException("Выберите PLC, класс и программу.");
    }

    private static bool ValuesEqual(object left, object right)
    {
        if (left == null || left == DBNull.Value) return right == null || right == DBNull.Value;
        if (right == null || right == DBNull.Value) return false;
        return Equals(left, right) || string.Equals(Convert.ToString(left), Convert.ToString(right), StringComparison.Ordinal);
    }

    private static string QuoteTable(string tableName)
    {
        var parts = tableName.Split('.', 2);
        return $"{QuoteIdentifier(parts.Length == 2 ? parts[0] : "dbo")}.{QuoteIdentifier(parts.Length == 2 ? parts[1] : parts[0])}";
    }

    private static string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
