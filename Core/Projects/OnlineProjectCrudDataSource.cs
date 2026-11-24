#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Data;
using Microsoft.Data.SqlClient;

namespace Configurator.Core.Projects;

/// <summary>
/// Online implementation of the shared mutation contract. It is constructed only
/// for an explicitly selected Online database; it never selects a database itself.
/// </summary>
public sealed class OnlineProjectCrudDataSource : IProjectCrudDataSource, IProjectBatchCrudDataSource, IProjectCreateWithKeyDataSource
{
    private readonly Configurator.DatabaseService _database;
    private readonly string _connectionString;
    internal string ConnectionString => _connectionString;

    public OnlineProjectCrudDataSource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Для Online-источника требуется строка подключения к выбранной БД.", nameof(connectionString));
        _connectionString = connectionString;
        _database = new Configurator.DatabaseService(connectionString);
    }

    public async Task CreateRecordAsync(string tableName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (values == null || values.Count == 0) throw new InvalidOperationException("Нет данных для создания записи.");
        await _database.InsertRowAsync(tableName, ToMutableDictionary(values), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, object?>> CreateRecordAndGetKeyAsync(string tableName,
        IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (values == null || values.Count == 0) throw new InvalidOperationException("Нет данных для вставки.");
        var keyColumns = await _database.GetPrimaryKeyColumnsAsync(tableName, cancellationToken).ConfigureAwait(false);
        if (keyColumns.Count == 0) throw new InvalidOperationException("Не удалось определить первичный ключ созданной записи.");
        return await _database.InsertRowAndGetKeyAsync(tableName, ToMutableDictionary(values), keyColumns, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CreateRecordsAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, CancellationToken cancellationToken = default)
    {
        ProjectWriteGuard.EnsureDataTableWritable(tableName);
        cancellationToken.ThrowIfCancellationRequested();
        if (records == null || records.Count == 0) return;
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("Не задана таблица.", nameof(tableName));
        if (records.Any(r => r == null || r.Count == 0)) throw new InvalidOperationException("Пакет содержит пустую запись.");
        var columns = records[0].Keys.ToList();
        var columnSet = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (columns.Count == 0 || records.Any(r => r.Keys.Count() != columns.Count || !columnSet.SetEquals(r.Keys)))
            throw new InvalidOperationException("Все записи пакета должны содержать одинаковый набор полей.");
        var quotedTable = QuoteTable(tableName);
        var quotedColumns = columns.Select(QuoteIdentifier).ToList();
        var parameterNames = columns.Select((_, i) => $"@p{i}").ToList();
        var sql = $"INSERT INTO {quotedTable} ({string.Join(", ", quotedColumns)}) VALUES ({string.Join(", ", parameterNames)});";

        using var connection = SqlProviderGuard.CreateConnection(_connectionString);
        SqlProviderGuard.EnsureSqlAllowed("OnlineProjectCrudDataSource.CreateRecordsAsync");
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = new SqlCommand(sql, connection, transaction);
                for (int i = 0; i < columns.Count; i++)
                {
                    var pair = record.First(x => x.Key.Equals(columns[i], StringComparison.OrdinalIgnoreCase));
                    command.Parameters.AddWithValue(parameterNames[i], pair.Value ?? DBNull.Value);
                }
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static (string Schema, string Table) ParseTable(string value)
    {
        var parts = value.Replace("[", string.Empty).Replace("]", string.Empty).Split('.', 2);
        return parts.Length == 1 ? ("dbo", ValidateIdentifier(parts[0]))
            : (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1]));
    }
    private static string ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('.') || value.Contains(';') || value.Contains('\0'))
            throw new ArgumentException("Некорректный SQL-идентификатор.");
        return value;
    }
    private static string QuoteIdentifier(string value) => "[" + ValidateIdentifier(value).Replace("]", "]]") + "]";
    private static string QuoteTable(string value)
    {
        var (schema, table) = ParseTable(value);
        return $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
    }

    public async Task UpdateRecordAsync(string tableName, IReadOnlyList<string> keyColumns,
        IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (keyColumns == null || keyColumns.Count == 0) throw new InvalidOperationException("Нельзя изменить запись без ключевых столбцов.");
        if (keyValues == null || keyColumns.Any(k => !keyValues.ContainsKey(k))) throw new InvalidOperationException("Не заданы все значения ключа.");
        if (values == null || values.Count == 0) throw new InvalidOperationException("Нет данных для изменения.");
        await _database.UpdateRowAsync(tableName, keyColumns.ToList(), ToMutableDictionary(keyValues), ToMutableDictionary(values), cancellationToken).ConfigureAwait(false);
    }

    internal Task UpdateCompareRecordAsync(ProjectTableSchema schema, ProjectRecord? expectedRecord,
        IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (keyValues == null || schema.KeyColumns.Any(key => !keyValues.ContainsKey(key)))
            throw new InvalidOperationException("Не заданы все значения стабильного ключа.");
        if (values == null || values.Count == 0)
            throw new InvalidOperationException("Нет выбранных полей для переноса.");

        return _database.UpdateCompareRowAsync(schema, expectedRecord, ToMutableDictionary(keyValues),
            ToMutableDictionary(values), cancellationToken);
    }

    public Task<int> DeleteRecordsAsync(string tableName, IReadOnlyList<string> keyColumns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> keyValues, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (keyValues == null || keyValues.Count == 0) return Task.FromResult(0);
        if (keyColumns == null || keyColumns.Count == 0) throw new InvalidOperationException("Нельзя удалить запись без ключевых столбцов.");
        var keys = keyValues.Select(ToMutableDictionary).ToList();
        return _database.DeleteRowsAsync(tableName, keyColumns.ToList(), keys, cancellationToken);
    }

    private static Dictionary<string, object> ToMutableDictionary(IReadOnlyDictionary<string, object?> source) =>
        source.ToDictionary(x => x.Key, x => x.Value ?? DBNull.Value, StringComparer.OrdinalIgnoreCase);
}
