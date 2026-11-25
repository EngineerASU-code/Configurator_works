using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator;

public partial class DatabaseService
{
    internal async Task<List<Dictionary<string, object>>> FindTablePasteRowsAsync(
        string table, IReadOnlyDictionary<string, object> key)
    {
        using var connection = SqlProviderGuard.CreateConnection(_connectionString);
        await SqlProviderGuard.OpenConnectionAsync(connection);
        return await ReadTablePasteRowsAsync(connection, null, table, key);
    }

    private async Task<List<Dictionary<string, object>>> ReadTablePasteRowsAsync(
        SqlConnection connection, SqlTransaction transaction, string table, IReadOnlyDictionary<string, object> key)
    {
        using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        var predicates = key.Select((field, index) =>
        {
            var parameter = $"@k{index}";
            command.Parameters.AddWithValue(parameter, field.Value ?? DBNull.Value);
            return $"{QuoteIdentifier(field.Key)} = {parameter}";
        }).ToArray();
        var hints = transaction == null ? "" : " WITH (UPDLOCK, HOLDLOCK)";
        command.CommandText = $"SELECT TOP (2) * FROM {QuoteTableName(table)}{hints} WHERE {string.Join(" AND ", predicates)}";
        using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row.Add(reader.GetName(i), reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    internal async Task<List<ChangeJournalRowChange>> ApplyTablePasteAsync(string table, IReadOnlyList<string> primaryKey,
        IReadOnlyList<(Dictionary<string, object> Key, Dictionary<string, object> Values,
            Dictionary<string, object> Expected)> rows)
    {
        ProjectWriteGuard.EnsureDataTableWritable(table);
        using var connection = SqlProviderGuard.CreateConnection(_connectionString);
        await SqlProviderGuard.OpenConnectionAsync(connection);
        using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var target = QuoteTableName(table);
        var changes = new List<ChangeJournalRowChange>();
        foreach (var row in rows)
        {
            var current = await ReadTablePasteRowsAsync(connection, transaction, table, row.Key);
            bool replace = row.Expected != null;
            if (replace ? current.Count != 1 || current[0].Count != row.Expected.Count ||
                row.Expected.Any(field => !current[0].TryGetValue(field.Key, out var value) ||
                    !StructuralComparisons.StructuralEqualityComparer.Equals(field.Value, value)) : current.Count != 0)
                throw new InvalidOperationException("Таблица изменилась или значения Record совпадают. Повторите вставку.");

            using var command = new SqlCommand { Connection = connection, Transaction = transaction };
            var fields = row.Values.Where(field => !replace ||
                (!row.Key.ContainsKey(field.Key) && !primaryKey.Contains(field.Key, StringComparer.OrdinalIgnoreCase))).ToArray();
            if (fields.Length == 0) continue;
            for (int i = 0; i < fields.Length; i++)
                command.Parameters.AddWithValue($"@p{i}", fields[i].Value ?? DBNull.Value);
            if (replace)
            {
                var predicates = row.Key.Select((field, index) =>
                {
                    command.Parameters.AddWithValue($"@k{index}", field.Value);
                    return $"{QuoteIdentifier(field.Key)} = @k{index}";
                }).ToArray();
                var assignments = fields.Select((field, index) => $"{QuoteIdentifier(field.Key)} = @p{index}");
                command.CommandText = $"UPDATE {target} SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", predicates)}";
            }
            else
            {
                var names = fields.Select(field => QuoteIdentifier(field.Key));
                var parameters = fields.Select((_, index) => $"@p{index}");
                command.CommandText = $"INSERT INTO {target} ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameters)})";
            }
            await command.ExecuteNonQueryAsync();
            var after = (await ReadTablePasteRowsAsync(connection, transaction, table, row.Key)).Single();
            var identity = primaryKey.Count == 0 ? row.Key :
                primaryKey.ToDictionary(name => name, name => after[name], StringComparer.OrdinalIgnoreCase);
            var change = new ChangeJournalRowChange
            {
                TableName = table,
                Kind = replace ? ChangeJournalOperationKind.Update : ChangeJournalOperationKind.Insert,
                Before = ToJournalSnapshot(row.Expected),
                After = ToJournalSnapshot(after),
                IdentityBefore = ToJournalSnapshot(replace ? identity : null),
                IdentityAfter = ToJournalSnapshot(identity)
            };
            if (!replace || change.ChangedAfter.Count > 0) changes.Add(change);
        }
        await transaction.CommitAsync();
        return changes;
    }

    private static Dictionary<string, ChangeJournalValue> ToJournalSnapshot(IReadOnlyDictionary<string, object> values)
        => values?.ToDictionary(x => x.Key, x => ChangeJournalValue.FromObject(x.Value), StringComparer.OrdinalIgnoreCase) ?? new();
}
