using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;
using Microsoft.Data.SqlClient;

namespace Configurator;

/// <summary>Destructive-operation guard: plan → replace → validate → delete, all inside one transaction.</summary>
public sealed class DeletionProtectionService
{
    private readonly string _connectionString;
    private readonly DatabaseSchema _schema;

    public DeletionProtectionService(string connectionString, DatabaseSchema schema)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public async Task<DeletionPlan> BuildPlanAsync(DeletionRequest request, CancellationToken cancellationToken = default)
    {
        var plan = new DeletionPlan { Request = request };
        foreach (var relation in _schema.Relations.Where(r =>
                     r.TargetTable.Equals(request.TableName, StringComparison.OrdinalIgnoreCase) &&
                     r.IsUsableForDeletionProtection))
        {
            if (relation.SourceTable.Equals(request.TableName, StringComparison.OrdinalIgnoreCase)) continue;

            // IMPORTANT: do not silently discard a logical dependency merely because
            // its source column cannot be written. Previously such a relation was
            // filtered out here, so deleting dbo.Areas could leave AnaIn/Valve/etc.
            // containing a now-orphaned Area value with no warning at all.
            var hits = await FindDependentsAsync(relation, request.Key, cancellationToken);
            if (hits.Count == 0) continue;

            plan.Dependencies.AddRange(hits);

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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in items)
            {
                foreach (var replacement in item.Replacements ?? Array.Empty<ReferenceReplacement>())
                    await ApplyReplacementAsync(connection, transaction, replacement, cancellationToken);
                if (!await validateAfterReplacement(connection, transaction, cancellationToken))
                    throw new InvalidOperationException("Проверка после переинициализации не пройдена. Изменения пакета удалений откатываются.");
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

    private async Task<List<DependencyHit>> FindDependentsAsync(DatabaseRelation relation, IReadOnlyDictionary<string, object> targetKey, CancellationToken ct)
    {
        if (relation.TargetColumns.Count == 0 || relation.TargetColumns.Count != relation.SourceColumns.Count) return new List<DependencyHit>();
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        var where = new List<string>();
        await using var command = new SqlCommand { Connection = connection };
        for (var i = 0; i < relation.TargetColumns.Count; i++)
        {
            if (!targetKey.TryGetValue(relation.TargetColumns[i], out var value)) return new List<DependencyHit>();
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
            hits.Add(new DependencyHit { SourceTable = relation.SourceTable, SourceKey = sourceKey, SourceColumnDescription = string.Join(", ", relation.SourceColumns), TargetTable = relation.TargetTable, Relation = relation });
        }
        return hits;
    }

    public async Task<List<ReplacementCandidate>> GetReplacementCandidatesAsync(DependencyHit dependency, IReadOnlyDictionary<string, object> deletedTargetKey, CancellationToken ct = default)
    {
        if (dependency?.Relation == null || dependency.Relation.TargetColumns.Count == 0 ||
            dependency.Relation.TargetColumns.Count != dependency.Relation.SourceColumns.Count) return new List<ReplacementCandidate>();

        var columns = dependency.Relation.TargetColumns;
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);

        var sourceTable = _schema.FindTable(dependency.SourceTable);
        if (sourceTable == null) return new List<ReplacementCandidate>();
        var sourceRow = await LoadSourceRowAsync(connection, dependency, sourceTable, ct);
        if (sourceRow.Count == 0) return new List<ReplacementCandidate>();

        var targetColumns = _schema.FindTable(dependency.TargetTable)?.Columns ?? new List<DatabaseColumn>();
        var displayColumn = targetColumns.Select(c => c.Name).FirstOrDefault(n => n.Equals("NameL1", StringComparison.OrdinalIgnoreCase))
            ?? targetColumns.Select(c => c.Name).FirstOrDefault(n => n.Equals("Name_L1", StringComparison.OrdinalIgnoreCase))
            ?? targetColumns.Select(c => c.Name).FirstOrDefault(n => n.Equals("Name", StringComparison.OrdinalIgnoreCase))
            ?? targetColumns.Select(c => c.Name).FirstOrDefault(n => n.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase));
        var selectColumns = columns.Select(QuoteIdentifier).ToList();
        if (!string.IsNullOrWhiteSpace(displayColumn) && !columns.Contains(displayColumn, StringComparer.OrdinalIgnoreCase))
            selectColumns.Add(QuoteIdentifier(displayColumn));
        var sql = $"SELECT DISTINCT {string.Join(", ", selectColumns)} FROM {QuoteTable(dependency.TargetTable)}";
        var candidateValues = new List<(List<object> Values, string DisplayName)>();
        await using (var command = new SqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var values = new List<object>();
                bool skip = true;
                for (int i = 0; i < columns.Count; i++)
                {
                    var value = reader.GetValue(i);
                    values.Add(value == DBNull.Value ? null : value);
                    if (!deletedTargetKey.TryGetValue(columns[i], out var oldValue) || !ValuesEqual(oldValue, value)) skip = false;
                }
                if (!skip)
                {
                    var display = string.Empty;
                    if (!string.IsNullOrWhiteSpace(displayColumn) && !columns.Contains(displayColumn, StringComparer.OrdinalIgnoreCase))
                        display = reader.IsDBNull(columns.Count) ? string.Empty : Convert.ToString(reader.GetValue(columns.Count)) ?? string.Empty;
                    candidateValues.Add((values, display));
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
            var values = candidateValue.Values;
            // В окне замены показываем человекочитаемое имя справочной записи;
            // технические ключи остаются в Values и используются при UPDATE.
            var displayValue = !string.IsNullOrWhiteSpace(candidateValue.DisplayName)
                ? candidateValue.DisplayName
                : string.Join(" · ", values.Select(v => ShortReplacementValue(v)));
            var candidate = new ReplacementCandidate
            {
                Values = values,
                Display = displayValue
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

    private static string ShortReplacementValue(object value)
    {
        if (value == null || value == DBNull.Value) return "NULL";
        var text = Convert.ToString(value) ?? "";
        // UI-only shortening: keep the real value in Values, but display the
        // terminal name when a legacy database stores a qualified object path.
        int dot = text.LastIndexOf('.');
        return dot >= 0 && dot < text.Length - 1 ? text[(dot + 1)..] : text;
    }

    private static bool ValuesEqual(object a, object b)
    {
        if (a == null || a == DBNull.Value) return b == null || b == DBNull.Value;
        if (b == null || b == DBNull.Value) return false;
        return string.Equals(Convert.ToString(a), Convert.ToString(b), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ApplyReplacementAsync(SqlConnection connection, SqlTransaction transaction, ReferenceReplacement replacement, CancellationToken ct)
    {
        if (replacement.SourceColumns.Count != replacement.NewValues.Count || replacement.KeyColumns.Count != replacement.KeyValues.Count)
            throw new ArgumentException("Количество полей и значений в замене не совпадает.");
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
            var p = "@key" + i; where.Add($"{QuoteColumn(replacement.SourceTable, replacement.KeyColumns[i])} = {p}");
            command.Parameters.AddWithValue(p, replacement.KeyValues[i] ?? DBNull.Value);
        }
        if (set.Count == 0 || where.Count == 0) throw new ArgumentException("Пустая операция замены.");
        command.CommandText = $"UPDATE {QuoteTable(replacement.SourceTable)} SET {string.Join(", ", set)} WHERE {string.Join(" AND ", where)}";
        await command.ExecuteNonQueryAsync(ct);
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
    public IReadOnlyList<object> Values { get; init; } = Array.Empty<object>();
    public string Display { get; init; } = "";
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
