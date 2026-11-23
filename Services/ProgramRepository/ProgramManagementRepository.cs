using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;

namespace Configurator;

/// <summary>
/// Read and compare-and-swap editing for the first Program/Sequence/Step surfaces.
/// Every edit is matched against the complete original row and committed by
/// DatabaseService inside a serializable transaction; ambiguous rows fail closed.
/// </summary>
public sealed partial class ProgramManagementRepository
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
    private readonly OfflineProjectDataSource? _offline;

    public ProgramManagementRepository(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _database = new DatabaseService(_connectionString);
    }

    public ProgramManagementRepository(OfflineProjectDataSource offline)
    {
        _offline = offline ?? throw new ArgumentNullException(nameof(offline));
        _connectionString = string.Empty;
    }

    public Task<DataTable> GetProgramsAsync(object plc, object programClass, CancellationToken ct = default)
        => GetScopedTableAsync(ProgramTable, Context(plc, programClass), ct);

    public Task<DataTable> GetSequencesAsync(ProgramContext context, CancellationToken ct = default)
        => GetScopedTableAsync(SequenceTable, ProgramScope(context, includeSequence: false), ct);

    public Task<DataTable> GetStepsAsync(ProgramContext context, CancellationToken ct = default)
        => GetReferencedStepsAsync(context, ct);

    public Task<DataTable> GetTransitionsAsync(ProgramContext context, CancellationToken ct = default)
        => GetScopedTableAsync(TransitionTable, ProgramScope(context, includeSequence: true), ct);

    public Task<DataTable> GetAllSequenceParametersAsync(ProgramContext context, CancellationToken ct = default)
        => GetScopedTableAsync(ParameterTable, ProgramScope(context, includeSequence: true), ct);

    public Task<DataTable> GetParametersAsync(ProgramContext context, int stepNumber, CancellationToken ct = default)
    {
        var scope = ProgramScope(context, includeSequence: true);
        scope["Step_Number"] = stepNumber;
        return GetScopedTableAsync(ParameterTable, scope, ct);
    }

    public async Task<HashSet<string>> GetNonEditableColumnsAsync(string tableName, CancellationToken ct = default)
    {
        if (_offline != null)
            return GetOfflineSchema(tableName).Columns.Where(column => column.IsIdentity || column.IsComputed || column.IsReadOnly)
                .Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = await _database.GetTableColumnsAsync(tableName, ct);
        return columns.Where(column => column.IsIdentity || column.IsReadOnly)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<ColumnInfo>> GetInsertColumnsAsync(string tableName, IEnumerable<string> generatedColumns, CancellationToken ct = default)
    {
        if (_offline != null)
        {
            var generatedOffline = new HashSet<string>(generatedColumns ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return GetOfflineSchema(tableName).Columns.Where(column => !column.IsIdentity && !column.IsComputed && !column.IsReadOnly && !generatedOffline.Contains(column.Name))
                .Select(column => new ColumnInfo { Name = column.Name, DataType = column.DataType, IsNullable = column.IsNullable,
                    HasDefault = column.HasDefault, IsIdentity = column.IsIdentity, IsComputed = column.IsComputed,
                    MaxLength = column.MaxLength, Precision = column.Precision, Scale = column.Scale }).ToList();
        }
        var generated = new HashSet<string>(generatedColumns ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var columns = await _database.GetTableColumnsAsync(tableName, ct);
        return columns.Where(column => !column.IsIdentity && !column.IsReadOnly && !generated.Contains(column.Name) &&
                (!column.IsNullable && !column.HasDefault || column.Name.Equals("Name_L1", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task<Dictionary<string, object>> GetStepTextDefaultsAsync(DataRow step, CancellationToken ct = default)
    {
        if (_offline != null)
        {
            var defaults = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var schema = GetOfflineSchema(TransitionTable);
            foreach (var column in schema.Columns.Where(column => !column.IsIdentity && !column.IsComputed && !column.IsReadOnly &&
                         (column.Name.StartsWith("Name", StringComparison.OrdinalIgnoreCase) || column.Name.StartsWith("Description", StringComparison.OrdinalIgnoreCase))))
                if (step.Table.Columns.Contains(column.Name) && step[column.Name] != DBNull.Value) defaults[column.Name] = step[column.Name];
            return defaults;
        }
        var columns = await _database.GetTableColumnsAsync(TransitionTable, ct);
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (!column.IsIdentity && !column.IsReadOnly && step.Table.Columns.Contains(column.Name) &&
                (column.Name.StartsWith("Name", StringComparison.OrdinalIgnoreCase) ||
                 column.Name.StartsWith("Description", StringComparison.OrdinalIgnoreCase)) &&
                step[column.Name] != DBNull.Value)
                values[column.Name] = step[column.Name];
        }
        return values;
    }

    public Task<int> AddProgramAsync(object plc, object programClass, IReadOnlyDictionary<string, object> values, CancellationToken ct = default)
    {
        var scope = Context(plc, programClass);
        if (_offline != null) return InsertOfflineAsync(ProgramTable, "Record", scope, values, ct);
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
        if (_offline != null) return InsertOfflineAsync(SequenceTable, "Record", scope, values, ct);
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
        if (_offline != null) return InsertOfflineAsync(TransitionTable, "SeqStep_Number", scope, valuesWithStepReference, ct);
        return InsertWithGeneratedRecordAsync(TransitionTable, "SeqStep_Number", scope,
            new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number" }, valuesWithStepReference, ct);
    }

    public Task<int> AddParameterAsync(ProgramContext context, int stepNumber,
        IReadOnlyDictionary<string, object> values, CancellationToken ct = default)
    {
        var scope = ProgramScope(context, includeSequence: true);
        scope["Step_Number"] = stepNumber;
        if (!values.TryGetValue("Type", out var type) || type == null || type == DBNull.Value)
            throw new InvalidOperationException("Укажите тип параметра.");
        scope["Type"] = type;
        var fields = values.Where(pair => !scope.ContainsKey(pair.Key) && !pair.Key.Equals("Record", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (_offline != null) return InsertOfflineAsync(ParameterTable, "Record", scope, fields, ct);
        return InsertWithGeneratedRecordAsync(ParameterTable, "Record", scope, scope.Keys.Append("Record").ToArray(), fields, ct);
    }

    public Task DeleteParameterAsync(ProgramContext context, int stepNumber, object type, object record, CancellationToken ct = default)
    {
        var key = ProgramScope(context, includeSequence: true);
        key["Step_Number"] = stepNumber;
        key["Type"] = type;
        key["Record"] = record;
        var dependencies = new (string Table, IReadOnlyDictionary<string, object> Scope)[] { ("dbo.Prog_Recipes", key) };
        return _offline != null
            ? _offline.ExecuteMutationTransactionAsync(async () => await DeleteOfflineIfUnreferencedAsync(ParameterTable, key, dependencies, ct))
            : DeleteIfUnreferencedAsync(ParameterTable, key, key.Keys.ToArray(), dependencies, ct);
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
        if (_offline != null)
        {
            await DeleteOfflineIfUnreferencedAsync(ProgramTable, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            { ["PLC"] = context.PlcValue, ["PLC_Class_Number"] = context.ClassNumberValue, ["Record"] = context.RecordValue },
                dependentTables, ct);
            return;
        }
        await DeleteIfUnreferencedAsync(ProgramTable, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = context.PlcValue,
            ["PLC_Class_Number"] = context.ClassNumberValue,
            ["Record"] = context.RecordValue
        }, new[] { "Record", "PLC", "PLC_Class_Number" }, dependentTables, ct);
    }

    public async Task<IReadOnlyList<ChangeJournalRowChange>> DeleteSequenceAsync(ProgramContext context, object sequenceRecord, CancellationToken ct = default, bool deleteDependents = false)
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
            ("dbo.Prog_QueueSelections", scope), ("dbo.Prog_Recipes", scope), ("dbo.Prog_Recipe", scope),
            (ParameterTable, scope), (TransitionTable, scope)
        };
        var key = new Dictionary<string, object>(scope, StringComparer.OrdinalIgnoreCase);
        key.Remove("Seq_Record");
        key["Record"] = sequenceRecord;
        var changes = new List<ChangeJournalRowChange>();
        if (_offline != null)
        {
            await _offline.ExecuteMutationTransactionAsync(async () =>
            {
                if (deleteDependents)
                {
                    foreach (var dependent in dependentTables)
                        await DeleteOfflineSequenceRowsAsync(dependent.Table, dependent.Scope, changes, ct);
                }
                foreach (DataRow row in ReadOfflineTable(SequenceTable, key).Rows)
                    changes.Add(DeletedJournalRow(SequenceTable, row, UniqueKeyColumns[SequenceTable]));
                await DeleteOfflineIfUnreferencedAsync(SequenceTable, key, dependentTables, ct);
            }, ct);
            return changes;
        }
        await DeleteIfUnreferencedAsync(SequenceTable, key,
            new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record" }, dependentTables, ct,
            deleteDependents: deleteDependents, journal: changes);
        return changes;
    }

    private async Task DeleteOfflineSequenceRowsAsync(string tableName, IReadOnlyDictionary<string, object> scope, List<ChangeJournalRowChange> changes, CancellationToken ct)
    {
        if (_offline.GetRecords(tableName).Count == 0) return;
        var schema = GetOfflineSchema(tableName);
        var rows = ReadOfflineTable(tableName, scope);
        if (rows.Rows.Count == 0) return;
        if (!schema.HasStableKey || schema.KeyColumns.Count == 0)
            throw new InvalidOperationException("Связанные строки не удалены: в снимке нет подтверждённого ключа.");
        foreach (DataRow row in rows.Rows)
            changes.Add(DeletedJournalRow(tableName, row, schema.KeyColumns));
        var keys = rows.Rows.Cast<DataRow>().Select(row => (IReadOnlyDictionary<string, object?>)schema.KeyColumns
            .ToDictionary(name => name, name => (object?)row[name], StringComparer.OrdinalIgnoreCase)).ToArray();
        var removed = await ((IProjectCrudDataSource)_offline).DeleteRecordsAsync(tableName, schema.KeyColumns, keys, ct);
        if (removed != keys.Length || ReadOfflineTable(tableName, scope).Rows.Count != 0)
            throw new InvalidOperationException("Удаление связанных строк не подтвердилось. Изменения отменены.");
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
        if (_offline != null)
        {
            EnsureNoOfflineDestinationReferences(scope, sequenceStepNumber);
            await DeleteOfflineIfUnreferencedAsync(TransitionTable, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            { ["PLC"] = context.PlcValue, ["PLC_Class_Prog_Number"] = context.ClassNumberValue, ["Prog_Record"] = context.RecordValue,
                ["Seq_Record"] = context.SeqRecordValue, ["SeqStep_Number"] = sequenceStepNumber }, dependentTables, ct);
            return;
        }
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

    /// <summary>Moves an occurrence in an offline snapshot, preserving references as one mutation.</summary>
    public async Task<int> MoveOfflineStepAsync(ProgramContext context, IReadOnlyDictionary<string, object> original,
        int direction, CancellationToken ct = default)
    {
        if (_offline == null) throw new InvalidOperationException("Перемещение Online пока не поддерживается.");
        if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException(nameof(direction));
        var scope = ProgramScope(context, includeSequence: true);
        var tables = new[] { (Name: TransitionTable, Field: "SeqStep_Number"),
            (Name: ParameterTable, Field: "Step_Number"), (Name: "dbo.Prog_Recipes", Field: "Step_Number") };
        foreach (var (name, field) in tables)
        {
            var schema = GetOfflineSchema(name);
            if (!schema.HasStableKey || !schema.KeyColumns.Contains(field, StringComparer.OrdinalIgnoreCase)
                || scope.Keys.Any(key => !schema.KeyColumns.Contains(key, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Перемещение заблокировано: ключ {name} не подтверждён.");
            var column = schema.Columns.FirstOrDefault(item => item.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (column == null || column.DataType.ToLowerInvariant() is not ("int" or "smallint" or "tinyint")
                || column.IsIdentity || column.IsComputed || column.IsReadOnly)
                throw new InvalidOperationException($"Перемещение заблокировано: поле {name}.{field} недоступно для безопасного изменения.");
        }
        var allowedTables = tables.Select(table => table.Name.Split('.').Last()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in _offline.GetTables())
        {
            var names = schema.Columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (scope.Keys.All(names.Contains) && (names.Contains("Step_Number") || names.Contains("SeqStep_Number"))
                && !allowedTables.Contains(schema.TableName.Split('.').Last()))
                throw new InvalidOperationException($"Перемещение заблокировано: не подтверждены ссылки в {schema.TableName}.");
        }
        var transitionSchema = GetOfflineSchema(TransitionTable);
        var destinations = Enumerable.Range(1, 8).Select(i => $"TransDest_{i:00}").ToArray();
        foreach (string name in destinations)
        {
            var column = transitionSchema.Columns.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (column == null || column.DataType.ToLowerInvariant() is not ("int" or "smallint" or "tinyint")
                || column.IsIdentity || column.IsComputed || column.IsReadOnly)
                throw new InvalidOperationException($"Перемещение заблокировано: поле {TransitionTable}.{name} не подтверждено.");
        }

        bool InScope(ProjectRecord row) => scope.All(pair =>
            row.Values.TryGetPropertyValue(pair.Key, out var value) && OfflineEquals(value, pair.Value));
        int Number(ProjectRecord row, string field)
            => int.Parse(row.Values[field]?.ToString() ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture);
        var transitions = _offline.GetRecords(TransitionTable).Where(InScope).ToArray();
        var selected = transitions.Where(row => original.All(pair =>
            row.Values.TryGetPropertyValue(pair.Key, out var value) && OfflineEquals(value, pair.Value))).Take(2).ToArray();
        if (selected.Length != 1) throw new InvalidOperationException("Исходное вхождение изменилось или неоднозначно.");
        var ordered = transitions.OrderBy(row => Number(row, "SeqStep_Number")).ToArray();
        if (ordered.Any(row => Number(row, "SeqStep_Number") <= 0)
            || ordered.Select(row => Number(row, "SeqStep_Number")).Distinct().Count() != ordered.Length)
            throw new InvalidOperationException("Номера вхождений должны быть положительными и уникальными.");
        int index = Array.IndexOf(ordered, selected[0]);
        int other = index + direction;
        if (other < 0 || other >= ordered.Length) throw new InvalidOperationException("Вхождение уже находится на границе списка.");
        int first = Number(selected[0], "SeqStep_Number");
        int second = Number(ordered[other], "SeqStep_Number");
        if (first <= 0 || second <= 0 || first == second)
            throw new InvalidOperationException("Номера вхождений должны быть положительными и уникальными.");
        int max = tables.SelectMany(t => _offline.GetRecords(t.Name).Where(InScope).Select(row => Number(row, t.Field)))
            .DefaultIfEmpty(0).Max();
        int temporary = checked(max + 1);

        async Task Update(ProjectTableSchema schema, ProjectRecord row, IReadOnlyDictionary<string, object?> values)
        {
            var key = schema.KeyColumns.ToDictionary(name => name, name =>
            {
                var column = schema.Columns.First(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                return (object?)OfflineValue(row.Values[name], OfflineType(column.DataType));
            }, StringComparer.OrdinalIgnoreCase);
            await ((IProjectCrudDataSource)_offline).UpdateRecordAsync(schema.TableName, schema.KeyColumns, key, values, ct);
        }
        async Task ReplaceNumber(string table, string field, int from, int to)
        {
            var schema = GetOfflineSchema(table);
            foreach (var row in _offline.GetRecords(table).Where(InScope).Where(row => Number(row, field) == from).ToArray())
                await Update(schema, row, new Dictionary<string, object?> { [field] = to });
        }

        await _offline.ExecuteMutationTransactionAsync(async () =>
        {
            foreach (var table in tables)
            {
                await ReplaceNumber(table.Name, table.Field, first, temporary);
                await ReplaceNumber(table.Name, table.Field, second, first);
                await ReplaceNumber(table.Name, table.Field, temporary, second);
            }
            foreach (var row in _offline.GetRecords(TransitionTable).Where(InScope).ToArray())
            {
                var changes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (string field in destinations)
                {
                    if (!row.Values.TryGetPropertyValue(field, out var destination))
                        throw new InvalidOperationException($"В записи перехода отсутствует поле {field}.");
                    if (destination == null) continue;
                    int value = Number(row, field);
                    if (value == first) changes[field] = second;
                    else if (value == second) changes[field] = first;
                }
                if (changes.Count > 0) await Update(transitionSchema, row, changes);
            }
        }, ct);
        return second;
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

        if (_offline != null) return UpdateOfflineAsync(tableName, original, changes, ct);

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
        CancellationToken ct, object stepNumberForDestinations = null, bool deleteDependents = false, List<ChangeJournalRowChange> journal = null)
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
            {
                if (!await HasScopedRowsAsync(connection, transaction, dependency.Table, dependency.Scope, ct)) continue;
                if (!deleteDependents) throw new InvalidOperationException(DeletionDependencyMessage(dependency.Table));
                var dependencyWhere = BuildPredicates(dependency.Scope, "child", out var dependencyParameters);
                if (journal != null)
                {
                    var keys = UniqueKeyColumns.TryGetValue(dependency.Table, out var knownKeys)
                        ? knownKeys : ProgramSupportCatalog.ProgramTables.Single(item => item.TableName == dependency.Table).KeyColumns;
                    await CaptureDeletedRowsAsync(connection, transaction, dependency.Table, dependencyWhere, dependencyParameters, keys, journal, ct);
                }
                using var delete = new Microsoft.Data.SqlClient.SqlCommand(
                    $"DELETE FROM {QuoteTable(dependency.Table)} WITH (UPDLOCK, HOLDLOCK) WHERE {dependencyWhere}", connection, transaction);
                AddParameters(delete, dependencyParameters);
                await delete.ExecuteNonQueryAsync(ct);
                if (await HasScopedRowsAsync(connection, transaction, dependency.Table, dependency.Scope, ct))
                    throw new InvalidOperationException("Удаление связанных строк не подтвердилось. Изменения отменены.");
            }

            var where = BuildPredicates(keyValues, "key", out var parameters);
            if (await CountRowsAsync(connection, transaction, tableName, keyValues, ct) != 1)
                throw new InvalidOperationException("Строка не найдена однозначно по ключу; удаление отменено.");
            if (journal != null)
                await CaptureDeletedRowsAsync(connection, transaction, tableName, where, parameters, uniqueKey, journal, ct);
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

    private static async Task CaptureDeletedRowsAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        Microsoft.Data.SqlClient.SqlTransaction transaction, string table, string where,
        IEnumerable<(string Name, object Value)> parameters, IReadOnlyList<string> keys,
        List<ChangeJournalRowChange> journal, CancellationToken ct)
    {
        using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT * FROM {QuoteTable(table)} WITH (UPDLOCK, HOLDLOCK) WHERE {where}", connection, transaction);
        AddParameters(command, parameters);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new DataTable();
        rows.Load(reader);
        foreach (DataRow row in rows.Rows)
            journal.Add(DeletedJournalRow(table, row, keys));
    }

    private static ChangeJournalRowChange DeletedJournalRow(string table, DataRow row, IReadOnlyList<string> keys)
    {
        var values = row.Table.Columns.Cast<DataColumn>().ToDictionary(
            column => column.ColumnName, column => ChangeJournalValue.FromObject(row[column]), StringComparer.OrdinalIgnoreCase);
        return new ChangeJournalRowChange
        {
            TableName = table,
            Kind = ChangeJournalOperationKind.Delete,
            Before = values,
            IdentityBefore = keys.All(values.ContainsKey)
                ? keys.ToDictionary(key => key, key => values[key], StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase)
        };
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
        const string destinationPrefix = "TransDest_";
        var destinations = columns.Where(column => column.Name.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(column.Name.AsSpan(destinationPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToList();
        if (destinations.Count == 0)
            throw new InvalidOperationException("Удаление шага заблокировано: поля назначения переходов не подтверждены в текущей схеме.");

        var scope = target.Where(pair => !pair.Key.Equals("SeqStep_Number", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        RequireColumns(columns, scope.Keys.Append("SeqStep_Number"), TransitionTable);
        var where = BuildPredicates(scope, "dest", out var parameters);
        foreach (var column in destinations)
        {
            if (!IsNumericOrText(column.DataType))
                throw new InvalidOperationException($"Удаление шага заблокировано: тип {TransitionTable}.{column.Name} не поддерживает безопасную проверку ссылок.");
        }

        int targetStep = Convert.ToInt32(stepNumber, CultureInfo.InvariantCulture);
        string destinationColumns = string.Join(", ", destinations.Select(column => QuoteIdentifier(column.Name)));
        using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT {destinationColumns} FROM {QuoteTable(TransitionTable)} WITH (UPDLOCK, HOLDLOCK) WHERE {where} AND {QuoteIdentifier("SeqStep_Number")} <> @targetSequenceStep;",
            connection, transaction);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@targetSequenceStep", stepNumber);
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            for (int index = 0; index < reader.FieldCount; index++)
            {
                if (int.TryParse(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int destination) && destination == targetStep)
                    throw new InvalidOperationException("Удаление шага заблокировано: другой переход указывает на этот шаг.");
            }
        }
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

    private ProjectTableSchema GetOfflineSchema(string tableName) => _offline?.GetSchema(tableName)
        ?? throw new InvalidOperationException($"В локальном проекте отсутствует схема «{tableName}».");

    private DataTable ReadOfflineTable(string tableName, IReadOnlyDictionary<string, object> scope)
    {
        var schema = GetOfflineSchema(tableName);
        foreach (var column in scope.Keys)
            if (!schema.Columns.Any(item => item.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"В {schema.TableName} нет поля контекста «{column}».");
        var table = new DataTable(schema.TableName);
        foreach (var column in schema.Columns) table.Columns.Add(column.Name, OfflineType(column.DataType));
        foreach (var record in _offline!.GetRecords(schema.TableName))
        {
            if (scope.Any(item => !record.Values.TryGetPropertyValue(item.Key, out var actual) || !OfflineEquals(actual, item.Value))) continue;
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
                if (record.Values.TryGetPropertyValue(column.ColumnName, out var value)) row[column] = OfflineValue(value, column.DataType);
            table.Rows.Add(row);
        }
        table.AcceptChanges();
        return table;
    }

    private static Type OfflineType(string type) => (type ?? "").ToLowerInvariant() switch
    {
        "tinyint" => typeof(byte), "smallint" => typeof(short), "int" => typeof(int), "bigint" => typeof(long),
        "bit" => typeof(bool), "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "float" => typeof(double), "real" => typeof(float), "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "uniqueidentifier" => typeof(Guid), _ => typeof(string)
    };

    private static object OfflineValue(JsonNode? value, Type type)
    {
        if (value == null) return DBNull.Value;
        string raw = value.ToString();
        if (type == typeof(string)) return raw;
        try { return Convert.ChangeType(raw, type, CultureInfo.InvariantCulture); }
        catch { return DBNull.Value; }
    }

    private static bool OfflineEquals(JsonNode? actual, object? expected)
        => actual == null ? expected == null || expected == DBNull.Value :
            string.Equals(actual.ToString(), Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private async Task<int> InsertOfflineAsync(string tableName, string generatedColumn,
        IReadOnlyDictionary<string, object> scope, IReadOnlyDictionary<string, object> values, CancellationToken ct)
    {
        var schema = GetOfflineSchema(tableName);
        var record = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in scope) record[pair.Key] = pair.Value;
        foreach (var pair in values ?? new Dictionary<string, object>()) record[pair.Key] = pair.Value;
        bool isIdentity = schema.Columns.FirstOrDefault(column => column.Name.Equals(generatedColumn, StringComparison.OrdinalIgnoreCase))?.IsIdentity == true;
        if (!record.ContainsKey(generatedColumn) && !isIdentity)
        {
            var rows = ReadOfflineTable(schema.TableName, scope);
            long maximum = rows.Rows.Count == 0 ? 0 : rows.Rows.Cast<DataRow>()
                .Where(row => row[generatedColumn] != DBNull.Value)
                .Select(row => Convert.ToInt64(row[generatedColumn], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
            record[generatedColumn] = checked(maximum + 1);
        }
        ct.ThrowIfCancellationRequested();
        var creator = (IProjectCreateWithKeyDataSource)_offline!;
        var key = await creator.CreateRecordAndGetKeyAsync(schema.TableName, record, ct);
        return Convert.ToInt32(key[generatedColumn], CultureInfo.InvariantCulture);
    }

    private async Task UpdateOfflineAsync(string tableName, IReadOnlyDictionary<string, object> original,
        IReadOnlyDictionary<string, object> changes, CancellationToken ct)
    {
        var schema = GetOfflineSchema(tableName);
        if (!schema.HasStableKey || schema.KeyColumns.Count == 0) throw new InvalidOperationException("В локальном снимке нет стабильного ключа для изменения записи.");
        var matches = _offline!.GetRecords(schema.TableName).Where(row => original.All(pair =>
            row.Values.TryGetPropertyValue(pair.Key, out var value) && OfflineEquals(value, pair.Value))).Take(2).ToList();
        if (matches.Count != 1) throw new InvalidOperationException(matches.Count == 0
            ? "Исходная запись больше не найдена в локальном проекте." : "Исходная запись неоднозначна в локальном проекте.");
        var target = matches[0];
        var keyValues = schema.KeyColumns.ToDictionary(name => name,
            name => (object?)OfflineValue(target.Values[name], OfflineType(schema.Columns.First(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).DataType)),
            StringComparer.OrdinalIgnoreCase);
        await ((IProjectCrudDataSource)_offline).UpdateRecordAsync(schema.TableName, schema.KeyColumns, keyValues, changes, ct);
    }

    private void EnsureNoOfflineDestinationReferences(IReadOnlyDictionary<string, object> scope, object stepNumber)
    {
        var schema = GetOfflineSchema(TransitionTable);
        const string prefix = "TransDest_";
        var destinations = schema.Columns.Where(column => column.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(column.Name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToList();
        if (destinations.Count == 0)
            throw new InvalidOperationException("Удаление шага заблокировано: поля назначения переходов не подтверждены в текущей схеме.");
        foreach (var column in destinations)
            if (!IsNumericOrText(column.DataType))
                throw new InvalidOperationException($"Удаление шага заблокировано: тип {TransitionTable}.{column.Name} не поддерживает безопасную проверку ссылок.");

        int target = Convert.ToInt32(stepNumber, CultureInfo.InvariantCulture);
        var rows = ReadOfflineTable(TransitionTable, scope);
        if (!rows.Columns.Contains("SeqStep_Number"))
            throw new InvalidOperationException("Удаление шага заблокировано: в схеме отсутствует SeqStep_Number.");
        foreach (DataRow row in rows.Rows)
        {
            if (int.TryParse(Convert.ToString(row["SeqStep_Number"], CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number == target) continue;
            foreach (var column in destinations)
                if (int.TryParse(Convert.ToString(row[column.Name], CultureInfo.InvariantCulture),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out int destination) && destination == target)
                    throw new InvalidOperationException("Удаление шага заблокировано: другой переход указывает на этот шаг.");
        }
    }

    private static string DeletionDependencyMessage(string table) => table switch
    {
        ParameterTable => "Остались связанные параметры. Проверьте вкладки «Параметры программы» и «Параметры шага» для выбранной последовательности.",
        TransitionTable => "Остались вхождения шагов или переходы. Проверьте вкладку «Шаги и переходы».",
        "dbo.Prog_Recipe" or "dbo.Prog_Recipes" => "Остались связанные рецепты или их значения. Проверьте вкладку «Рецепты».",
        "dbo.Prog_QueueSelections" => "Остались связанные элементы очередей. Проверьте вкладку «Очереди».",
        _ => "Остались связанные записи. Сначала перенесите или удалите их штатным действием."
    };

    private async Task DeleteOfflineIfUnreferencedAsync(string tableName, IReadOnlyDictionary<string, object> key,
        IReadOnlyList<(string Table, IReadOnlyDictionary<string, object> Scope)> dependents, CancellationToken ct)
    {
        var schema = GetOfflineSchema(tableName);
        if (!schema.HasStableKey || schema.KeyColumns.Count == 0) throw new InvalidOperationException("В локальном снимке нет стабильного ключа для удаления записи.");
        foreach (var dependent in dependents)
        {
            var dependentSchema = _offline!.GetSchema(dependent.Table);
            if (dependentSchema == null || dependent.Scope.Keys.Any(column => !dependentSchema.Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))) continue;
            if (ReadOfflineTable(dependent.Table, dependent.Scope).Rows.Count > 0)
                throw new InvalidOperationException(DeletionDependencyMessage(dependent.Table));
        }
        var matches = _offline!.GetRecords(schema.TableName).Where(row => key.All(pair => row.Values.TryGetPropertyValue(pair.Key, out var value) && OfflineEquals(value, pair.Value))).Take(2).ToList();
        if (matches.Count != 1) throw new InvalidOperationException("Запись для удаления не найдена или неоднозначна в локальном проекте.");
        var keyValues = schema.KeyColumns.ToDictionary(name => name,
            name => (object?)OfflineValue(matches[0].Values[name], OfflineType(schema.Columns.First(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).DataType)),
            StringComparer.OrdinalIgnoreCase);
        int removed = await ((IProjectCrudDataSource)_offline).DeleteRecordsAsync(schema.TableName, schema.KeyColumns, new[] { keyValues }, ct);
        if (removed != 1) throw new InvalidOperationException("Состав локального проекта изменился; удаление отменено.");
    }

    private async Task<DataTable> GetScopedTableAsync(string tableName,
        IReadOnlyDictionary<string, object> requestedScope, CancellationToken ct, bool readAll = false)
    {
        if (_offline != null)
        {
            ct.ThrowIfCancellationRequested();
            return ReadOfflineTable(tableName, requestedScope);
        }
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
            $"SELECT {(readAll ? string.Empty : "TOP (1000) ")}* FROM {QuoteTable(tableName)} WHERE {string.Join(" AND ", predicates)} ORDER BY {order}", connection)
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
        if (_offline != null)
        {
            ct.ThrowIfCancellationRequested();
            var offlineTransitions = ReadOfflineTable(TransitionTable, scope);
            var offlineSteps = ReadOfflineTable(StepTable, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["PLC"] = context.PlcValue });
            var keys = offlineTransitions.Rows.Cast<DataRow>().Select(row => (Class: row["PLC_Class_Step_Number"], Record: row["Step_Record"]))
                .ToHashSet();
            var offlineResult = offlineSteps.Clone();
            foreach (DataRow row in offlineSteps.Rows)
                if (keys.Contains((row["PLC_Class_Number"], row["Record"]))) offlineResult.ImportRow(row);
            return offlineResult;
        }
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
