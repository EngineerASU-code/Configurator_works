using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator;

/// <summary>Destructive-operation guard: plan → replace → validate → delete, all inside one transaction.</summary>
public sealed class DeletionProtectionService
{
    private readonly string _connectionString;
    private readonly DatabaseSchema _schema;
    private readonly LookupOptionsService _lookups;

    public DeletionProtectionService(string connectionString, DatabaseSchema schema)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _lookups = new LookupOptionsService(new DatabaseService(connectionString));
    }

    public async Task<DeletionPlan> BuildPlanAsync(DeletionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProjectWriteGuard.EnsureDataTableWritable(request.TableName);
        var plan = new DeletionPlan { Request = request };
        if (_schema.Relations.Any(relation => relation == null))
            plan.ValidationErrors.Add("Удаление заблокировано: карта зависимостей содержит пустую связь, которую невозможно проверить.");
        foreach (var relation in _schema.Relations.Where(r =>
                     r != null && r.TargetTable.Equals(request.TableName, StringComparison.OrdinalIgnoreCase)))
        {
            if (!relation.IsUsableForDeletionProtection)
            {
                plan.ValidationErrors.Add($"Удаление заблокировано: связь {relation.SourceTable} → {relation.TargetTable} не подтверждена для защиты зависимостей.");
                continue;
            }

            var sourceTable = _schema.FindTable(relation.SourceTable);
            var targetTable = _schema.FindTable(relation.TargetTable);
            if (sourceTable == null || targetTable == null || relation.SourceColumns.Count == 0 ||
                relation.SourceColumns.Count != relation.TargetColumns.Count ||
                relation.SourceColumns.Any(column => !sourceTable.Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase))) ||
                relation.TargetColumns.Any(column => !targetTable.Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase))))
            {
                plan.ValidationErrors.Add($"Удаление заблокировано: связь {relation.SourceTable} → {relation.TargetTable} имеет неполную или неизвестную схему столбцов.");
                continue;
            }

            if (relation.TargetColumns.Any(column => request.Key == null || !request.Key.ContainsKey(column)))
            {
                plan.ValidationErrors.Add($"Удаление заблокировано: для проверки связи {relation.SourceTable} → {relation.TargetTable} не хватает ключевых значений строки.");
                continue;
            }

            // Self-referencing relations are checked too; a self-link must not silently
            // bypass dependency protection.
            var hits = await FindDependentsAsync(relation, request, targetTable, cancellationToken);
            if (hits.Count == 0) continue;

            plan.Dependencies.AddRange(hits);

            if (hits.Any(hit => !HasStableIdentity(sourceTable, hit.SourceKey.Keys)))
                plan.ValidationErrors.Add($"Удаление заблокировано: для зависимых строк в {relation.SourceTable} нет подтверждённого первичного или уникального ключа.");

            if (!RelationCanBeWritten(relation))
            {
                var reason = string.Join(", ", relation.SourceColumns.Select(column =>
                {
                    var source = _schema.FindTable(relation.SourceTable);
                    var meta = source?.Columns.FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
                    if (meta?.IsComputed == true) return $"{relation.SourceTable}.{column} (computed)";
                    if (meta?.IsIdentity == true) return $"{relation.SourceTable}.{column} (identity)";
                    return $"{relation.SourceTable}.{column}";
                }));
                plan.ValidationErrors.Add($"Найдены зависимые строки: {reason}. Автоматическая замена невозможна, потому что поле связи недоступно для записи.");
            }
        }
        return plan;
    }

    public async Task ExecuteAsync(DeletionPlan plan, IReadOnlyList<ReferenceReplacement> replacements, Func<SqlConnection, SqlTransaction, CancellationToken, Task<bool>> validateAfterReplacement, CancellationToken cancellationToken = default)
        => await ExecuteBatchAsync(new[] { new DeletionExecutionItem(plan, replacements) }, validateAfterReplacement, cancellationToken);

    /// <summary>Executes all selected deletions and their reference replacements in one transaction.</summary>
    public async Task ExecuteBatchAsync(IReadOnlyList<DeletionExecutionItem> items, Func<SqlConnection, SqlTransaction, CancellationToken, Task<bool>> validateAfterReplacement, CancellationToken cancellationToken = default)
    {
        if (items == null || items.Count == 0) throw new ArgumentException("Не выбраны строки для удаления.", nameof(items));
        if (validateAfterReplacement == null) throw new ArgumentNullException(nameof(validateAfterReplacement));
        foreach (var item in items)
        {
            if (item?.Plan == null) throw new ArgumentException("Пакет содержит пустой план удаления.", nameof(items));
            if (!item.Plan.CanCommit) throw new InvalidOperationException("Удаление заблокировано: план содержит ошибки или неподдерживаемые зависимости.");
        }

        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            foreach (var item in items)
            {
                ProjectWriteGuard.EnsureDataTableWritable(item.Plan.Request.TableName);
                await EnsureSnapshotAsync(connection, transaction, item.Plan.Request.TableName, item.Plan.Request.Key,
                    item.Plan.Request.ExpectedValues, cancellationToken);
                foreach (var hit in item.Plan.Dependencies)
                {
                    ProjectWriteGuard.EnsureDataTableWritable(hit.SourceTable);
                    await EnsureSnapshotAsync(connection, transaction, hit.SourceTable, hit.SourceKey, hit.SourceValues, cancellationToken);
                }
            }
            foreach (var item in items)
            {
                foreach (var replacement in item.Replacements ?? Array.Empty<ReferenceReplacement>())
                {
                    var matches = item.Plan.Dependencies.Where(hit => hit.SourceTable.Equals(replacement.SourceTable, StringComparison.OrdinalIgnoreCase) &&
                        hit.Relation.SourceColumns.SequenceEqual(replacement.SourceColumns, StringComparer.OrdinalIgnoreCase) &&
                        hit.SourceKey.Count == replacement.KeyColumns.Count && replacement.KeyColumns.Select((key, index) =>
                            hit.SourceKey.TryGetValue(key, out var value) && SnapshotValueEquals(value, replacement.KeyValues[index])).All(equal => equal)).ToList();
                    if (matches.Count == 0 || matches.Select(hit => hit.TargetTable + "|" + string.Join("|", hit.Relation.TargetColumns)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                        throw new InvalidOperationException("Замена не соответствует одной подготовленной зависимости.");
                    var relation = matches[0].Relation;
                    if (replacement.NewValues.Count != relation.TargetColumns.Count || replacement.TargetValues == null || replacement.TargetValues.Count == 0)
                        throw new InvalidOperationException("Нет исходного состояния выбранной замены. Повторите выбор.");
                    var targetKey = relation.TargetColumns.Select((key, index) => (key, index)).ToDictionary(x => x.key, x => replacement.NewValues[x.index], StringComparer.OrdinalIgnoreCase);
                    await EnsureSnapshotAsync(connection, transaction, relation.TargetTable, targetKey, replacement.TargetValues, cancellationToken);
                    await ApplyReplacementAsync(connection, transaction, replacement, cancellationToken);
                }
                if (!await validateAfterReplacement(connection, transaction, cancellationToken))
                    throw new InvalidOperationException("Проверка после переинициализации не пройдена. Изменения пакета удалений откатываются.");
                await EnsureNoCurrentDependentsAsync(connection, transaction, item.Plan.Request, cancellationToken);
                await DeleteTargetAsync(connection, transaction, item.Plan.Request, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* Preserve the original failure. */ }
            throw;
        }
    }

    internal async Task DeleteUnreferencedInTransactionAsync(SqlConnection connection, SqlTransaction transaction,
        string tableName, IReadOnlyDictionary<string, object> identity, IReadOnlyDictionary<string, object> expected, CancellationToken ct)
    {
        ProjectWriteGuard.EnsureDataTableWritable(tableName);
        if (_schema.Relations.Any(relation => relation == null))
            throw new InvalidOperationException("Карта зависимостей неполна. Удаление отменено.");
        await EnsureSnapshotAsync(connection, transaction, tableName, identity, expected, ct);
        var request = new DeletionRequest { TableName = tableName, Key = expected, ExpectedValues = expected };
        await EnsureNoCurrentDependentsAsync(connection, transaction, request, ct, identity);
        await DeleteTargetAsync(connection, transaction, new DeletionRequest { TableName = tableName, Key = identity }, ct);
    }

    private async Task EnsureSnapshotAsync(SqlConnection connection, SqlTransaction transaction, string tableName,
        IReadOnlyDictionary<string, object> identity, IReadOnlyDictionary<string, object> expected, CancellationToken ct)
    {
        var table = _schema.FindTable(tableName);
        if (table == null || identity == null || identity.Count == 0 || expected == null ||
            expected.Count != table.Columns.Count || table.Columns.Any(c => !expected.ContainsKey(c.Name)))
            throw new InvalidOperationException("Нет полного исходного состояния строки. Обновите данные и повторите удаление.");
        await using var cmd = new SqlCommand { Connection = connection, Transaction = transaction };
        var predicates = new List<string>(); int i = 0;
        foreach (var pair in identity)
        {
            if (!table.Columns.Any(c => c.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Ключ содержит неизвестный столбец.");
            var col = QuoteColumn(tableName, pair.Key);
            if (pair.Value == null || pair.Value == DBNull.Value) predicates.Add(col + " IS NULL");
            else { var parameter = "@key" + i++; predicates.Add(col + " = " + parameter); cmd.Parameters.AddWithValue(parameter, pair.Value); }
        }
        cmd.CommandText = $"SELECT * FROM {QuoteTable(tableName)} WITH (UPDLOCK, HOLDLOCK) WHERE {string.Join(" AND ", predicates)}";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.FieldCount != expected.Count)
            throw new InvalidOperationException("Исходная строка изменилась или удалена. Удаление отменено.");
        for (int n = 0; n < reader.FieldCount; n++)
        {
            if (!expected.TryGetValue(reader.GetName(n), out var value) || !SnapshotValueEquals(value, reader.IsDBNull(n) ? null : reader.GetValue(n)))
                throw new InvalidOperationException("Исходная или зависимая строка изменилась. Удаление отменено.");
        }
        if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Идентификатор строки неоднозначен. Удаление отменено.");
    }

    private static bool SnapshotValueEquals(object a, object b)
    {
        if (a == DBNull.Value) a = null;
        if (b == DBNull.Value) b = null;
        if (a is byte[] left && b is byte[] right) return left.SequenceEqual(right);
        return Equals(a, b);
    }

    private async Task EnsureNoCurrentDependentsAsync(SqlConnection connection, SqlTransaction transaction, DeletionRequest request, CancellationToken ct, IReadOnlyDictionary<string, object> deletedIdentity = null)
    {
        foreach (var relation in _schema.Relations.Where(r => r != null && r.TargetTable.Equals(request.TableName, StringComparison.OrdinalIgnoreCase)))
        {
            if (!relation.IsUsableForDeletionProtection || relation.SourceColumns.Count == 0 || relation.SourceColumns.Count != relation.TargetColumns.Count)
                throw new InvalidOperationException("Связь невозможно повторно проверить. Удаление отменено.");
            await using var cmd = new SqlCommand { Connection = connection, Transaction = transaction };
            var where = new List<string>();
            for (int i = 0; i < relation.SourceColumns.Count; i++)
            {
                if (!request.Key.TryGetValue(relation.TargetColumns[i], out var value))
                    throw new InvalidOperationException("Нет значения для проверки зависимости. Удаление отменено.");
                if (value == null || value == DBNull.Value) { where.Clear(); break; }
                var p = "@ref" + i;
                where.Add(QuoteColumn(relation.SourceTable, relation.SourceColumns[i]) + " = " + p);
                cmd.Parameters.AddWithValue(p, value);
            }
            if (where.Count == 0) continue;
            // A self-reference disappears together with the inserted row.
            if (deletedIdentity != null && relation.SourceTable.Equals(request.TableName, StringComparison.OrdinalIgnoreCase))
            {
                var sameRow = new List<string>();
                foreach (var pair in deletedIdentity)
                {
                    var column = QuoteColumn(request.TableName, pair.Key);
                    if (pair.Value == null || pair.Value == DBNull.Value)
                        sameRow.Add(column + " IS NULL");
                    else
                    {
                        var parameter = "@self" + sameRow.Count;
                        sameRow.Add($"({column} IS NOT NULL AND {column} = {parameter})");
                        cmd.Parameters.AddWithValue(parameter, pair.Value);
                    }
                }
                where.Add("NOT (" + string.Join(" AND ", sameRow) + ")");
            }
            cmd.CommandText = $"SELECT TOP (1) 1 FROM {QuoteTable(relation.SourceTable)} WITH (UPDLOCK, HOLDLOCK) WHERE {string.Join(" AND ", where)}";
            if (await cmd.ExecuteScalarAsync(ct) != null)
                throw new InvalidOperationException($"На строку {request.TableName} ссылается запись в {relation.SourceTable} (поля {string.Join(", ", relation.SourceColumns)}). Сначала измените или удалите эту ссылку.");
        }
    }

    private bool RelationCanBeWritten(DatabaseRelation relation)
    {
        var source = _schema.FindTable(relation.SourceTable);
        if (source == null || relation.SourceColumns.Count == 0 || relation.SourceColumns.Count != relation.TargetColumns.Count)
            return false;

        // Computed columns are derived values and cannot be updated directly.
        // They may participate in a SELECT-based logical relation, but they must
        // never become replacement targets during destructive operations.
        return relation.SourceColumns.All(column =>
        {
            var meta = source.Columns.FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
            return meta != null && !meta.IsComputed && !meta.IsIdentity;
        });
    }

    private async Task<List<DependencyHit>> FindDependentsAsync(DatabaseRelation relation, DeletionRequest request, DatabaseTable targetTable, CancellationToken ct)
    {
        var targetKey = GetDisplayKey(targetTable, request.Key, relation.TargetColumns);
        var targetName = DependencyRecordLabel.Name(request.ExpectedValues);
        if (targetName.Length == 0) targetName = DependencyRecordLabel.Name(request.Key);
        if (relation.TargetColumns.Count == 0 || relation.TargetColumns.Count != relation.SourceColumns.Count)
            throw new InvalidOperationException($"Не удалось проверить зависимость {relation.SourceTable} → {relation.TargetTable}: столбцы связи не сопоставлены.");
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        var where = new List<string>();
        await using var command = new SqlCommand { Connection = connection };
        for (var i = 0; i < relation.TargetColumns.Count; i++)
        {
            if (!request.Key.TryGetValue(relation.TargetColumns[i], out var value))
                throw new InvalidOperationException($"Не удалось проверить зависимость {relation.SourceTable} → {relation.TargetTable}: отсутствует значение ключа «{relation.TargetColumns[i]}».");
            var p = "@p" + i;
            where.Add($"{QuoteColumn(relation.SourceTable, relation.SourceColumns[i])} = {p}");
            command.Parameters.AddWithValue(p, value ?? DBNull.Value);
        }
        command.CommandText = $"SELECT * FROM {QuoteTable(relation.SourceTable)} WHERE {string.Join(" AND ", where)}";
        var hits = new List<DependencyHit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        var schemaTable = reader.GetSchemaTable();
        var available = schemaTable?.Rows.Cast<DataRow>().Select(r => Convert.ToString(r["ColumnName"])).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
        {
            var sourceKey = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var sourceTable = _schema.FindTable(relation.SourceTable);
            var keyColumns = sourceTable?.Keys.FirstOrDefault(k => k.IsPrimary)?.Columns
                             ?? sourceTable?.Keys.FirstOrDefault(k => k.IsUnique)?.Columns
                             ?? new List<string>();
            foreach (var keyColumn in keyColumns)
                if (available.Contains(keyColumn)) sourceKey[keyColumn] = reader[keyColumn] == DBNull.Value ? null : reader[keyColumn];
            if (sourceKey.Count == 0)
            {
                foreach (var keyColumn in new[] { "PLC", "PLC_Class_Number", "Record" })
                    if (available.Contains(keyColumn)) sourceKey[keyColumn] = reader[keyColumn] == DBNull.Value ? null : reader[keyColumn];
            }
            // Legacy tables frequently have no PK/unique key at all. We still need
            // to be able to repair a logical dependency. In that case the dependent
            // relation columns themselves are a safe predicate: all rows pointing to
            // the deleted target value are intentionally updated together.
            if (sourceKey.Count == 0)
            {
                foreach (var sourceColumn in relation.SourceColumns)
                    if (available.Contains(sourceColumn))
                        sourceKey[sourceColumn] = reader[sourceColumn] == DBNull.Value ? null : reader[sourceColumn];
            }
            var sourceReferenceValues = relation.SourceColumns
                .Where(available.Contains)
                .ToDictionary(column => column, column => reader[column] == DBNull.Value ? null : reader[column], StringComparer.OrdinalIgnoreCase);
            var sourceValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++) sourceValues[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            hits.Add(new DependencyHit { SourceTable = relation.SourceTable, SourceKey = sourceKey, SourceValues = sourceValues, SourceReferenceValues = sourceReferenceValues, SourceColumnDescription = string.Join(", ", relation.SourceColumns), TargetTable = relation.TargetTable, TargetName = targetName, TargetKey = targetKey, Relation = relation });
        }
        return hits;
    }

    public async Task<List<ReplacementCandidate>> GetReplacementCandidatesAsync(DependencyHit dependency, IReadOnlyDictionary<string, object> deletedTargetKey, CancellationToken ct = default,
        IReadOnlyList<IReadOnlyDictionary<string, object>> excludedTargets = null)
    {
        if (dependency?.Relation == null || dependency.Relation.TargetColumns.Count == 0 ||
            dependency.Relation.TargetColumns.Count != dependency.Relation.SourceColumns.Count) return new List<ReplacementCandidate>();

        var columns = dependency.Relation.TargetColumns;
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);

        var sourceTable = _schema.FindTable(dependency.SourceTable);
        if (sourceTable == null) return new List<ReplacementCandidate>();
        if (!HasStableIdentity(sourceTable, dependency.SourceKey.Keys))
            throw new InvalidOperationException($"Безопасная замена для {dependency.SourceTable} невозможна: не найден подтверждённый первичный или уникальный ключ зависимой строки.");
        var sourceRow = await LoadSourceRowAsync(connection, dependency, sourceTable, ct);
        if (sourceRow.Count == 0) return new List<ReplacementCandidate>();

        var plcLabels = dependency.Relation.SourceColumns.Contains("PLC", StringComparer.OrdinalIgnoreCase)
            ? ReplacementCandidate.IndexPlcLabels(await _lookups.TryGetAsync(null, dependency.SourceTable, "PLC", ct))
            : null;
        var targetTable = _schema.FindTable(dependency.TargetTable);
        var sql = $"SELECT * FROM {QuoteTable(dependency.TargetTable)}";
        var candidateValues = new List<(List<object> Values, string DisplayName, Dictionary<string, object> Snapshot)>();
        await using (var command = new SqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var snapshot = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++) snapshot[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                var values = columns.Select(column => snapshot[column] == DBNull.Value ? null : snapshot[column]).ToList();
                bool skip = columns.Select((column, index) => deletedTargetKey.TryGetValue(column, out var oldValue) && ValuesEqual(oldValue, values[index])).All(equal => equal);
                if (!skip && !(excludedTargets?.Any(target => columns.All(column => target.TryGetValue(column, out var deletedValue) && ValuesEqual(deletedValue, snapshot[column]))) ?? false))
                {
                    candidateValues.Add((values, DependencyRecordLabel.Name(snapshot), snapshot));
                }
            }
        }

        // The reader must be fully disposed before conflict analysis starts.
        // AnalyzeCandidateConflictsAsync issues additional commands on the same
        // connection; leaving the candidate reader open causes the SQL Server
        // "open DataReader" exception when MARS is disabled.
        var result = new List<ReplacementCandidate>();
        foreach (var candidateValue in candidateValues)
        {
            var candidate = new ReplacementCandidate
            {
                Values = candidateValue.Values,
                PlcLabels = plcLabels,
                TargetValues = candidateValue.Snapshot,
                Display = candidateValue.DisplayName,
                DisplayKey = GetDisplayKey(targetTable, candidateValue.Snapshot, columns)
            };
            await AnalyzeCandidateConflictsAsync(connection, dependency, sourceTable, sourceRow, candidate, ct);
            result.Add(candidate);
        }
        return result;
    }

    private async Task<Dictionary<string, object>> LoadSourceRowAsync(SqlConnection connection, DependencyHit dependency, DatabaseTable sourceTable, CancellationToken ct)
    {
        if (dependency.SourceKey.Count == 0) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var where = new List<string>();
        await using var command = new SqlCommand { Connection = connection };
        var i = 0;
        foreach (var pair in dependency.SourceKey)
        {
            var p = "@src" + i++;
            where.Add(QuoteColumn(dependency.SourceTable, pair.Key) + " = " + p);
            command.Parameters.AddWithValue(p, pair.Value ?? DBNull.Value);
        }
        command.CommandText = $"SELECT TOP (1) * FROM {QuoteTable(dependency.SourceTable)} WHERE {string.Join(" AND ", where)}";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < reader.FieldCount; c++) row[reader.GetName(c)] = reader.IsDBNull(c) ? null : reader.GetValue(c);
        return row;
    }

    private async Task AnalyzeCandidateConflictsAsync(SqlConnection connection, DependencyHit dependency, DatabaseTable sourceTable, Dictionary<string, object> sourceRow, ReplacementCandidate candidate, CancellationToken ct)
    {
        var predicted = new Dictionary<string, object>(sourceRow, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < dependency.Relation.SourceColumns.Count; i++)
            predicted[dependency.Relation.SourceColumns[i]] = candidate.Values[i];

        var conflicts = new List<DatabaseKey>();
        foreach (var key in sourceTable.Keys.Where(k => k.IsUnique))
        {
            if (key.Columns.Count == 0 || key.Columns.Any(c => !predicted.ContainsKey(c))) continue;
            if (await UniqueKeyExistsAsync(connection, dependency, key, predicted, ct)) conflicts.Add(key);
        }

        if (conflicts.Count == 0) return;

        candidate.IsConflict = true;
        candidate.ConflictDescription = "Конфликт уникальности: " + string.Join(", ", conflicts.Select(k => k.Name));

        // If a unique key contains Record and Record is writable, try the next
        // free Record automatically. This is the normal legacy-object repair
        // path for combinations such as Record+PLC+Class.
        var recordColumn = sourceTable.Columns.FirstOrDefault(c =>
            c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase) &&
            !c.IsIdentity && !c.IsComputed &&
            !sourceTable.Keys.Any(k => k.IsPrimary && k.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)));
        if (recordColumn == null || !conflicts.Any(k => k.Columns.Any(c => c.Equals("Record", StringComparison.OrdinalIgnoreCase)))) return;

        if (!TryConvertInt(sourceRow.TryGetValue(recordColumn.Name, out var oldRecord) ? oldRecord : null, out var currentRecord)) return;
        var next = Math.Max(1, currentRecord + 1);
        for (var attempts = 0; attempts < 10000; attempts++, next++)
        {
            predicted[recordColumn.Name] = next;
            var stillConflicts = false;
            foreach (var key in sourceTable.Keys.Where(k => k.IsUnique))
            {
                if (key.Columns.Any(c => !predicted.ContainsKey(c))) continue;
                if (await UniqueKeyExistsAsync(connection, dependency, key, predicted, ct)) { stillConflicts = true; break; }
            }
            if (stillConflicts) continue;

            candidate.IsConflict = false;
            candidate.AutoAssignments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [recordColumn.Name] = next
            };
            candidate.ConflictDescription = $"Конфликт уникальности будет устранён автоматически: Record → {next}.";
            return;
        }
    }

    private async Task<bool> UniqueKeyExistsAsync(SqlConnection connection, DependencyHit dependency, DatabaseKey key, IReadOnlyDictionary<string, object> values, CancellationToken ct)
    {
        var where = new List<string>();
        await using var command = new SqlCommand { Connection = connection };
        var i = 0;
        foreach (var column in key.Columns)
        {
            if (!values.TryGetValue(column, out var value)) return false;
            var p = "@u" + i++;
            where.Add(QuoteColumn(dependency.SourceTable, column) + " = " + p);
            command.Parameters.AddWithValue(p, value ?? DBNull.Value);
        }

        // Exclude the dependent row itself; otherwise every candidate would
        // appear to conflict with its own current unique key.
        var exclude = new List<string>();
        foreach (var pair in dependency.SourceKey)
        {
            var p = "@old" + i++;
            exclude.Add("NOT (" + QuoteColumn(dependency.SourceTable, pair.Key) + " = " + p + ")");
            command.Parameters.AddWithValue(p, pair.Value ?? DBNull.Value);
        }
        command.CommandText = $"SELECT TOP (1) 1 FROM {QuoteTable(dependency.SourceTable)} WHERE {string.Join(" AND ", where)} AND ({string.Join(" OR ", exclude)})";
        return await command.ExecuteScalarAsync(ct) != null;
    }

    private static bool TryConvertInt(object value, out int result)
    {
        if (value == null || value == DBNull.Value) { result = 0; return false; }
        return int.TryParse(Convert.ToString(value), out result);
    }

    private static IReadOnlyDictionary<string, object> GetDisplayKey(DatabaseTable table,
        IReadOnlyDictionary<string, object> values, IReadOnlyList<string> linkColumns)
    {
        var keyColumns = table?.Keys.FirstOrDefault(key => key.IsPrimary)?.Columns
            ?? table?.Keys.FirstOrDefault(key => key.IsUnique)?.Columns;
        return (keyColumns != null && keyColumns.Count > 0 && keyColumns.All(values.ContainsKey) ? keyColumns : linkColumns)
            .ToDictionary(column => column, column => values[column], StringComparer.OrdinalIgnoreCase);
    }

    private static bool ValuesEqual(object a, object b)
    {
        if (a == null || a == DBNull.Value) return b == null || b == DBNull.Value;
        if (b == null || b == DBNull.Value) return false;
        return string.Equals(Convert.ToString(a), Convert.ToString(b), StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyReplacementAsync(SqlConnection connection, SqlTransaction transaction, ReferenceReplacement replacement, CancellationToken ct)
    {
        ProjectWriteGuard.EnsureDataTableWritable(replacement.SourceTable);
        if (replacement.SourceColumns.Count != replacement.NewValues.Count || replacement.KeyColumns.Count != replacement.KeyValues.Count)
            throw new ArgumentException("Количество полей и значений в замене не совпадает.");
        if (replacement.ExpectedOldValues == null || replacement.SourceColumns.Any(column => !replacement.ExpectedOldValues.ContainsKey(column)))
            throw new InvalidOperationException("Безопасная замена невозможна: отсутствуют исходные значения связанных полей. Повторно загрузите зависимости.");
        var sourceTable = _schema.FindTable(replacement.SourceTable);
        if (sourceTable == null || !HasStableIdentity(sourceTable, replacement.KeyColumns))
            throw new InvalidOperationException($"Безопасная замена в {replacement.SourceTable} невозможна: условие не использует подтверждённый первичный или уникальный ключ.");

        var set = new List<string>(); var where = new List<string>();
        await using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        for (var i = 0; i < replacement.SourceColumns.Count; i++)
        {
            var p = "@new" + i; set.Add($"{QuoteColumn(replacement.SourceTable, replacement.SourceColumns[i])} = {p}");
            command.Parameters.AddWithValue(p, replacement.NewValues[i] ?? DBNull.Value);
        }
        var offset = replacement.SourceColumns.Count;
        foreach (var assignment in replacement.AdditionalAssignments)
        {
            var p = "@extra" + offset++;
            set.Add($"{QuoteColumn(replacement.SourceTable, assignment.Key)} = {p}");
            command.Parameters.AddWithValue(p, assignment.Value ?? DBNull.Value);
        }
        for (var i = 0; i < replacement.KeyColumns.Count; i++)
        {
            var column = QuoteColumn(replacement.SourceTable, replacement.KeyColumns[i]);
            if (replacement.KeyValues[i] == null || replacement.KeyValues[i] == DBNull.Value)
                where.Add($"{column} IS NULL");
            else
            {
                var p = "@key" + i;
                where.Add($"{column} = {p}");
                command.Parameters.AddWithValue(p, replacement.KeyValues[i]);
            }
        }
        for (var i = 0; i < replacement.SourceColumns.Count; i++)
        {
            var column = QuoteColumn(replacement.SourceTable, replacement.SourceColumns[i]);
            var value = replacement.ExpectedOldValues[replacement.SourceColumns[i]];
            if (value == null || value == DBNull.Value)
                where.Add($"{column} IS NULL");
            else
            {
                var p = "@expected" + i;
                where.Add($"{column} = {p}");
                command.Parameters.AddWithValue(p, value);
            }
        }
        if (set.Count == 0 || where.Count == 0) throw new ArgumentException("Пустая операция замены.");

        var table = QuoteTable(replacement.SourceTable);
        var whereClause = string.Join(" AND ", where);
        await using (var count = new SqlCommand($"SELECT COUNT_BIG(1) FROM {table} WITH (UPDLOCK, HOLDLOCK) WHERE {whereClause}", connection, transaction))
        {
            foreach (SqlParameter parameter in command.Parameters)
                if (parameter.ParameterName.StartsWith("@key", StringComparison.Ordinal) ||
                    parameter.ParameterName.StartsWith("@expected", StringComparison.Ordinal))
                    count.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
            var matches = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            if (matches != 1)
                throw new InvalidOperationException(matches == 0
                    ? $"Зависимая строка {replacement.SourceTable} изменилась или удалена. Удаление отменено."
                    : $"Условие замены в {replacement.SourceTable} соответствует нескольким строкам. Удаление отменено.");
        }

        var updatedParameter = command.Parameters.Add("@updated", SqlDbType.BigInt);
        updatedParameter.Direction = ParameterDirection.Output;
        command.CommandText = $"DECLARE @affected TABLE (RowMarker int NOT NULL); UPDATE {table} SET {string.Join(", ", set)} OUTPUT 1 INTO @affected (RowMarker) WHERE {whereClause}; SET @updated = (SELECT COUNT_BIG(*) FROM @affected);";
        await command.ExecuteNonQueryAsync(ct);
        var updated = Convert.ToInt64(updatedParameter.Value);
        if (updated != 1)
            throw new InvalidOperationException($"Замена в {replacement.SourceTable} затронула {updated} строк вместо одной. Удаление отменено.");
    }

    private static bool HasStableIdentity(DatabaseTable table, IEnumerable<string> columns)
    {
        var requested = columns?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requested?.Count > 0 && table.Keys.Any(key => (key.IsPrimary || key.IsUnique) &&
            key.Columns.Count == requested.Count && key.Columns.All(requested.Contains));
    }

    private static async Task DeleteTargetAsync(SqlConnection connection, SqlTransaction transaction, DeletionRequest request, CancellationToken ct)
    {
        if (request.Key == null || request.Key.Count == 0)
            throw new InvalidOperationException("Не удалось определить строку для безопасного удаления.");

        var where = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        var i = 0;
        foreach (var pair in request.Key)
        {
            var column = QuoteColumn(request.TableName, pair.Key);
            if (pair.Value == null || pair.Value == DBNull.Value)
                where.Add($"{column} IS NULL");
            else
            {
                var parameter = "@key" + i++;
                where.Add($"{column} = {parameter}");
                parameters.Add((parameter, pair.Value));
            }
        }

        var table = QuoteTable(request.TableName);
        var whereClause = string.Join(" AND ", where);
        await using (var count = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table} WITH (UPDLOCK, HOLDLOCK) WHERE {whereClause}", connection, transaction))
        {
            foreach (var parameter in parameters)
                count.Parameters.AddWithValue(parameter.Name, parameter.Value);
            var matches = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            if (matches != 1)
                throw new InvalidOperationException(matches == 0
                    ? "Исходная строка больше не найдена. Удаление отменено."
                    : "Исходное состояние соответствует нескольким строкам; безопасное удаление отменено.");
        }

        await using var command = new SqlCommand($"DELETE FROM {table} WHERE {whereClause}", connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var deleted = await command.ExecuteNonQueryAsync(ct);
        if (deleted != 1)
            throw new InvalidOperationException("Удаление не затронуло ровно одну строку; транзакция отменена.");
    }

    private static string QuoteTable(string fullName)
    {
        var parts = fullName.Split('.', 2); var schema = parts.Length == 2 ? parts[0] : "dbo"; var table = parts.Length == 2 ? parts[1] : parts[0];
        return $"[{schema.Replace("]", "]]" )}].[{table.Replace("]", "]]" )}]";
    }
    private static string QuoteColumn(string table, string column) => $"{QuoteTable(table)}.[{column.Replace("]", "]]" )}]";
    private static string QuoteIdentifier(string identifier) => $"[{(identifier ?? "").Replace("]", "]]" )}]";
}

public sealed class ReplacementCandidate
{
    internal IReadOnlyDictionary<string, string> PlcLabels { get; init; }

    internal static Dictionary<string, string> IndexPlcLabels(IEnumerable<LookupOption> options) =>
        options.GroupBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Display, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<object> Values { get; init; } = Array.Empty<object>();
    public IReadOnlyDictionary<string, object> TargetValues { get; init; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    public string Display { get; init; } = "";
    internal IReadOnlyDictionary<string, object> DisplayKey { get; init; } = new Dictionary<string, object>();
    public bool IsConflict { get; set; }
    public string ConflictDescription { get; set; } = "";
    public IReadOnlyDictionary<string, object> AutoAssignments { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReferenceReplacement
{
    public string SourceTable { get; init; } = "";
    public IReadOnlyList<string> SourceColumns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<object> NewValues { get; init; } = Array.Empty<object>();
    public IReadOnlyList<string> KeyColumns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<object> KeyValues { get; init; } = Array.Empty<object>();
    public IReadOnlyDictionary<string, object> TargetValues { get; init; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, object> ExpectedOldValues { get; init; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, object> AdditionalAssignments { get; init; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeletionExecutionItem
{
    public DeletionPlan Plan { get; }
    public IReadOnlyList<ReferenceReplacement> Replacements { get; }
    public DeletionExecutionItem(DeletionPlan plan, IReadOnlyList<ReferenceReplacement> replacements)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Replacements = replacements ?? Array.Empty<ReferenceReplacement>();
    }
}
