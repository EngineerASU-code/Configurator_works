#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Configurator.Core.Projects;

/// <summary>
/// Live SQL Server read adapter. Every connection is created through SqlProviderGuard.
/// Paging, filtering, search, ordering and counts execute in SQL Server.
/// </summary>
public sealed class OnlineProjectDataSource : IProjectDataSource, IProjectAsyncDataSource, IProjectPagedDataSource, IProjectSchemaCache
{
    private readonly string _connectionString;
    private readonly string _displayName;
    public string Server { get; }
    public string Database { get; }
    private readonly Dictionary<string, ProjectTableSchema> _schemaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _schemaSync = new();

    public OnlineProjectDataSource(string connectionString, string displayName = "Online")
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Для Online-источника требуется строка подключения.", nameof(connectionString));
        _connectionString = connectionString;
        var builder = new SqlConnectionStringBuilder(connectionString);
        Server = builder.DataSource;
        Database = builder.InitialCatalog;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? Database : displayName;
    }

    public void InvalidateSchemaCache()
    {
        lock (_schemaSync) _schemaCache.Clear();
    }

    public string DisplayName => _displayName;
    public bool IsReadOnly => false;

    public IReadOnlyList<ProjectTableSchema> GetTables() =>
        GetTablesAsync().GetAwaiter().GetResult();

    public IReadOnlyList<ProjectRecord> GetRecords(string tableName)
    {
        const int compatibilityLimit = 5000;
        var first = ReadPageAsync(new ProjectReadQuery { TableName = tableName, PageNumber = 1, PageSize = 1000 }).GetAwaiter().GetResult();
        if (first.TotalCount > compatibilityLimit)
            throw new InvalidOperationException($"Таблица «{tableName}» содержит {first.TotalCount} записей. Используйте постраничное чтение через IProjectPagedDataSource.");
        if (first.TotalCount <= first.Items.Count) return first.Items;
        var all = first.Items.ToList();
        for (int page = 2; all.Count < first.TotalCount; page++)
        {
            var next = ReadPageAsync(new ProjectReadQuery { TableName = tableName, PageNumber = page, PageSize = 1000 }).GetAwaiter().GetResult();
            all.AddRange(next.Items);
            if (next.Items.Count == 0) break;
        }
        return all;
    }

    public ProjectTableSchema? GetSchema(string tableName) =>
        GetSchemaAsync(tableName).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<ProjectTableSchema>> GetTablesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = @"SELECT TABLE_SCHEMA, TABLE_NAME
                             FROM INFORMATION_SCHEMA.TABLES
                             WHERE TABLE_TYPE = 'BASE TABLE'
                             ORDER BY TABLE_SCHEMA, TABLE_NAME";
        var names = new List<string>();
        await using (var connection = CreateConnection())
        {
            await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                names.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        }

        var result = new List<ProjectTableSchema>(names.Count);
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = await GetSchemaAsync(name, cancellationToken).ConfigureAwait(false);
            if (schema != null) result.Add(schema);
        }
        return result;
    }

    public async Task<ProjectTableSchema?> GetSchemaAsync(string tableName, CancellationToken cancellationToken = default)
    {
        var (schemaName, bareTable) = ParseTableName(tableName);
        var cacheKey = $"{schemaName}.{bareTable}";
        lock (_schemaSync)
            if (_schemaCache.TryGetValue(cacheKey, out var cached)) return cached;

        const string sql = @"
;WITH CandidateIndexes AS (
    SELECT i.object_id, i.index_id, i.is_primary_key,
           ROW_NUMBER() OVER (PARTITION BY i.object_id
               ORDER BY CASE WHEN i.is_primary_key=1 THEN 0 ELSE 1 END, i.index_id) AS candidate_rank
    FROM sys.indexes i
    WHERE i.is_primary_key=1
       OR (i.is_unique=1 AND i.type IN (1,2) AND i.is_disabled=0 AND i.is_hypothetical=0 AND i.has_filter=0
           AND NOT EXISTS (
               SELECT 1 FROM sys.index_columns ix
               JOIN sys.columns cx ON cx.object_id=ix.object_id AND cx.column_id=ix.column_id
               WHERE ix.object_id=i.object_id AND ix.index_id=i.index_id
                 AND ix.is_included_column=0 AND cx.is_nullable=1))
), SelectedKeyColumns AS (
    SELECT ic.object_id, ic.column_id, ic.key_ordinal
    FROM CandidateIndexes ci
    JOIN sys.index_columns ic ON ic.object_id=ci.object_id AND ic.index_id=ci.index_id
    WHERE ci.candidate_rank=1 AND ic.is_included_column=0
)
SELECT c.name, ty.name AS data_type, c.is_nullable, c.is_identity, c.is_computed,
       c.max_length, c.precision, c.scale,
       CASE WHEN dc.object_id IS NULL THEN 0 ELSE 1 END AS has_default,
       CASE WHEN sk.column_id IS NULL THEN 0 ELSE 1 END AS is_key_column,
       ISNULL(sk.key_ordinal, 0) AS key_ordinal
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id
JOIN sys.types ty ON ty.user_type_id=c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
LEFT JOIN SelectedKeyColumns sk ON sk.object_id=c.object_id AND sk.column_id=c.column_id
WHERE s.name=@schema AND t.name=@table
ORDER BY c.column_id";
        var columns = new List<(ProjectColumnSchema Column, bool IsKey, int Ordinal)>();
        await using (var connection = CreateConnection())
        {
            await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schemaName;
            command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = bareTable;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add((new ProjectColumnSchema
                {
                    Name = reader.GetString(0),
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetBoolean(2),
                    IsIdentity = reader.GetBoolean(3),
                    IsComputed = reader.GetBoolean(4),
                    MaxLength = reader.GetInt16(5),
                    Precision = reader.GetByte(6),
                    Scale = reader.GetByte(7),
                    HasDefault = reader.GetInt32(8) != 0,
                    IsReadOnly = reader.GetBoolean(3) || reader.GetBoolean(4)
                }, reader.GetInt32(9) == 1, Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture)));
            }
        }
        if (columns.Count == 0) return null;
        var result = new ProjectTableSchema
        {
            TableName = cacheKey,
            Columns = columns.Select(x => x.Column).ToList(),
            KeyColumns = columns.Where(x => x.IsKey).OrderBy(x => x.Ordinal).Select(x => x.Column.Name).ToList(),
            HasStableKey = columns.Any(x => x.IsKey)
        };
        lock (_schemaSync) _schemaCache[cacheKey] = result;
        return result;
    }

    public async Task<ProjectRecordPage> ReadPageAsync(ProjectReadQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.TableName)) throw new ArgumentException("Не задана таблица.", nameof(query));
        int page = Math.Max(1, query.PageNumber);
        int size = Math.Clamp(query.PageSize, 1, 1000);
        var schema = await GetSchemaAsync(query.TableName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Таблица «{query.TableName}» не найдена или недоступна.");
        var columns = schema.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in query.Filters ?? new Dictionary<string, object?>())
            if (!columns.Contains(filter.Key)) throw new InvalidOperationException($"В таблице «{schema.TableName}» отсутствует поле фильтра «{filter.Key}».");
        if (!string.IsNullOrWhiteSpace(query.SortColumn) && !columns.Contains(query.SortColumn))
            throw new InvalidOperationException($"В таблице «{schema.TableName}» отсутствует поле сортировки «{query.SortColumn}».");

        var where = BuildWhere(schema, query, out var parameters);
        var orderColumn = !string.IsNullOrWhiteSpace(query.SortColumn)
            ? schema.Columns.First(c => c.Name.Equals(query.SortColumn, StringComparison.OrdinalIgnoreCase)).Name
            : schema.KeyColumns.FirstOrDefault() ?? schema.Columns.First().Name;
        var direction = query.SortDescending ? "DESC" : "ASC";
        var orderTerms = new List<string> { $"{QuoteIdentifier(orderColumn)} {direction}" };
        var stableOrderColumns = schema.KeyColumns.Count > 0
            ? schema.KeyColumns
            : schema.Columns.Select(c => c.Name).ToList();
        foreach (var keyColumn in stableOrderColumns)
            if (!keyColumn.Equals(orderColumn, StringComparison.OrdinalIgnoreCase))
                orderTerms.Add($"{QuoteIdentifier(keyColumn)} ASC");
        var sql = $"SELECT * FROM {QuoteTable(schema.TableName)} {where} ORDER BY {string.Join(", ", orderTerms)} OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        var items = new List<ProjectRecord>();
        int total;
        await using (var connection = CreateConnection())
        {
            await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using (var countCommand = new SqlCommand($"SELECT COUNT_BIG(*) FROM {QuoteTable(schema.TableName)} {where};", connection))
            {
                AddParameters(countCommand, parameters);
                var countValue = await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                long count = Convert.ToInt64(countValue, CultureInfo.InvariantCulture);
                total = count > int.MaxValue ? int.MaxValue : (int)count;
            }

            await using var command = new SqlCommand(sql, connection);
            AddParameters(command, parameters);
            command.Parameters.Add("@offset", SqlDbType.BigInt).Value = (long)(page - 1) * size;
            command.Parameters.Add("@pageSize", SqlDbType.Int).Value = size;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new JsonObject();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    values[reader.GetName(i)] = value == null ? null : JsonSerializer.SerializeToNode(value);
                }
                var key = schema.KeyColumns.Count == 0 ? string.Empty : BuildKey(schema, values);
                items.Add(new ProjectRecord { TableName = schema.TableName, Key = key, Values = values });
            }
        }
        return new ProjectRecordPage { Items = items, PageNumber = page, PageSize = size, TotalCount = total };
    }

    public async Task<int> CountAsync(ProjectReadQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var schema = await GetSchemaAsync(query.TableName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Таблица «{query.TableName}» не найдена или недоступна.");
        var where = BuildWhere(schema, query, out var parameters);
        await using var connection = CreateConnection();
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {QuoteTable(schema.TableName)} {where};", connection);
        AddParameters(command, parameters);
        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        long count = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private SqlConnection CreateConnection()
    {
        SqlProviderGuard.EnsureSqlAllowed("OnlineProjectDataSource connection");
        return SqlProviderGuard.CreateConnection(_connectionString);
    }

    private static string BuildWhere(ProjectTableSchema schema, ProjectReadQuery query, out List<(string Name, object? Value)> parameters)
    {
        parameters = new();
        var known = schema.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clauses = new List<string>();
        int index = 0;
        foreach (var pair in query.Filters ?? new Dictionary<string, object?>())
        {
            if (!known.Contains(pair.Key)) throw new InvalidOperationException($"Неизвестное поле фильтра «{pair.Key}».");
            var actualColumnName = schema.Columns.First(c => c.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).Name;
            var column = QuoteIdentifier(actualColumnName);
            if (pair.Value == null || pair.Value == DBNull.Value) clauses.Add($"{column} IS NULL");
            else
            {
                var name = "@f" + index++;
                clauses.Add($"{column} = {name}");
                parameters.Add((name, pair.Value));
            }
        }
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var searchColumns = schema.Columns.Where(c => !IsBinaryType(c.DataType)).ToList();
            if (searchColumns.Count > 0)
            {
                var searchName = "@search";
                clauses.Add("(" + string.Join(" OR ", searchColumns.Select(c => $"CHARINDEX(LOWER({searchName}), LOWER(CONVERT(nvarchar(max), {QuoteIdentifier(c.Name)}))) > 0")) + ")");
                parameters.Add((searchName, query.SearchText.Trim()));
            }
        }
        return clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
    }

    private static bool IsBinaryType(string type) =>
        type.Equals("image", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("binary", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("varbinary", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("rowversion", StringComparison.OrdinalIgnoreCase);

    private static void AddParameters(SqlCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var item in parameters)
            command.Parameters.AddWithValue(item.Name, item.Value ?? DBNull.Value);
    }

    private static string BuildKey(ProjectTableSchema schema, JsonObject values) =>
        ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => values[name]);

    private static (string Schema, string Table) ParseTableName(string tableName)
    {
        var parts = tableName.Replace("[", string.Empty).Replace("]", string.Empty).Split('.', 2);
        if (parts.Length == 1) return ("dbo", ValidateIdentifier(parts[0]));
        return (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1]));
    }

    private static string ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('.') || value.Contains(';') || value.Contains('\0'))
            throw new ArgumentException("Некорректный SQL-идентификатор.", nameof(value));
        return value;
    }

    private static string QuoteIdentifier(string value) => "[" + ValidateIdentifier(value).Replace("]", "]]") + "]";
    private static string QuoteTable(string tableName)
    {
        var (schema, table) = ParseTableName(tableName);
        return $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
    }
}
