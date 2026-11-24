using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using System.Globalization;

namespace Configurator
{
    public partial class DatabaseService
    {
            // === P1-11: Async ===
            public async Task<DataTable> GetTableListAsync(CancellationToken ct = default)
            {
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
    
                const string query = @"
                    SELECT TABLE_SCHEMA, TABLE_NAME
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_TYPE = 'BASE TABLE'
                    ORDER BY TABLE_NAME";
    
                using var command = new SqlCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync(ct);
    
                var table = new DataTable();
                table.Load(reader);
                return table;
            }
    

            public async Task<List<string>> GetTypeLookupTablesAsync(CancellationToken ct = default)
            {
                var result = new List<string>();
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);

                const string query = @"
                    SELECT TABLE_SCHEMA, TABLE_NAME
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_TYPE = 'BASE TABLE'
                      AND TABLE_SCHEMA = 'dbo'
                      AND (TABLE_NAME LIKE '%Type' OR TABLE_NAME LIKE '%Types')
                    ORDER BY TABLE_NAME";

                using var command = new SqlCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    result.Add($"{reader.GetString(0)}.{reader.GetString(1)}");

                return result;
            }

            // === P1-11: Async ===
            public Task<DataTable> GetTableDataPageAsync(string tableName, int pageNumber, int pageSize, IReadOnlyList<string> orderColumns, TableFilter filter = null, CancellationToken ct = default)
                => GetColumnViewPageAsync(tableName, pageNumber, pageSize,
                    orderColumns?.Select(column => new TableColumnSort(column, System.ComponentModel.ListSortDirection.Ascending)).ToArray(),
                    filter == null ? null : new[] { filter }, ct);

            internal async Task<DataTable> GetColumnViewPageAsync(string tableName, int pageNumber, int pageSize,
                IReadOnlyList<TableColumnSort> orderColumns, IReadOnlyList<TableFilter> filters, CancellationToken ct = default)
            {
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
    
                if (pageNumber < 1) pageNumber = 1;
                if (pageSize < 1) pageSize = 100;
                long offset = ((long)pageNumber - 1L) * pageSize;
                var order = orderColumns != null && orderColumns.Count > 0
                    ? string.Join(", ", orderColumns.Select(BuildColumnOrder))
                    : "(SELECT 0)";
    
                var where = BuildFilterClause(filters, out var filterParameters);
                var query = $@"SELECT *
    FROM {QuoteTableName(tableName)}
    {where}
    ORDER BY {order}
    OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";
    
                using var command = new SqlCommand(query, connection);
                foreach (var parameter in filterParameters) command.Parameters.Add(parameter);
                command.CommandTimeout = 60;
                SessionTestLog.Trace("SQL-ЗАПРОС", $"DatabaseService.GetTableDataPageAsync: {command.CommandText}; parameters=[{string.Join(", ", command.Parameters.Cast<SqlParameter>().Select(p => p.ParameterName + "=" + (p.ParameterName.Contains("password", StringComparison.OrdinalIgnoreCase) ? "<redacted>" : Convert.ToString(p.Value, CultureInfo.InvariantCulture))))}]");
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                var pageData = new DataTable();
                for (int i = 0; i < reader.FieldCount; i++)
                    pageData.Columns.Add(reader.GetName(i), reader.GetFieldType(i));

                // DataTable.Load(reader) performs synchronous reads. Since this method is
                // awaited from WPF, that synchronous loop can freeze the UI on slow filtered
                // pages. Read each row asynchronously so paging remains responsive.
                pageData.BeginLoadData();
                try
                {
                    while (await reader.ReadAsync(ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        var values = new object[reader.FieldCount];
                        reader.GetValues(values);
                        pageData.Rows.Add(values);
                    }
                }
                finally
                {
                    pageData.EndLoadData();
                }
                SessionTestLog.Trace("SQL-ОТВЕТ", $"GetTableDataPageAsync: table='{tableName}', rows={pageData.Rows.Count}, columns={pageData.Columns.Count}");
                return pageData;
            }
    
            public Task<int> GetTableRowCountAsync(string tableName, TableFilter filter = null, CancellationToken ct = default)
                => CountColumnViewRowsAsync(tableName, filter == null ? null : new[] { filter }, ct);

            internal async Task<int> CountColumnViewRowsAsync(string tableName, IReadOnlyList<TableFilter> filters, CancellationToken ct = default)
            {
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                var where = BuildFilterClause(filters, out var filterParameters);
                using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {QuoteTableName(tableName)} {where}", connection);
                foreach (var parameter in filterParameters) command.Parameters.Add(parameter);
                command.CommandTimeout = 60;
                long count = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
                return count > int.MaxValue ? int.MaxValue : (int)count;
            }
    
            private string BuildColumnOrder(TableColumnSort sort)
            {
                var column = QuoteIdentifier(sort.Column);
                if (sort.CompareAsText) column = $"CONVERT(nvarchar(max), {column})";
                return column + (sort.Direction == System.ComponentModel.ListSortDirection.Descending ? " DESC" : " ASC");
            }

            private string BuildFilterClause(IReadOnlyList<TableFilter> filters, out List<SqlParameter> parameters)
            {
                parameters = new List<SqlParameter>();
                if (filters == null || filters.Count == 0) return "";
                var predicates = new List<string>();
                foreach (var filter in filters.Where(f => f.IsActive))
                {
                    string column = QuoteIdentifier(filter.Column);
                    string op = filter.Operator;
                    if (op is "Is Null" or "Пусто (NULL)")
                    {
                        predicates.Add($"{column} IS NULL");
                        continue;
                    }
                    if (op is "Is Not Null" or "Не пусто (NOT NULL)")
                    {
                        predicates.Add($"{column} IS NOT NULL");
                        continue;
                    }

                    string parameter = $"@filter{parameters.Count}";
                    string comparison = op switch
                    {
                        "Equals" or "Равно" => "=",
                        "Not Equals" or "Не равно" => "<>",
                        "Greater" or "Больше" => ">",
                        "Greater Or Equal" or "Больше или равно" => ">=",
                        "Less" or "Меньше" => "<",
                        "Less Or Equal" or "Меньше или равно" => "<=",
                        _ => null
                    };
                    if (comparison != null)
                    {
                        if (filter.CompareAsText) column = $"CONVERT(nvarchar(max), {column})";
                        predicates.Add($"{column} {comparison} {parameter}");
                        parameters.Add(new SqlParameter(parameter, filter.TypedValue ?? filter.Value));
                        continue;
                    }

                    string literal = filter.Value.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_").Replace("[", "~[");
                    string pattern = op switch
                    {
                        "Starts With" or "Начинается с" => literal + "%",
                        "Ends With" or "Заканчивается на" => "%" + literal,
                        _ => "%" + literal + "%"
                    };
                    string like = op is "Not Contains" or "Не содержит" ? "NOT LIKE" : "LIKE";
                    predicates.Add($"CONVERT(nvarchar(max), {column}) {like} {parameter} ESCAPE '~'");
                    parameters.Add(new SqlParameter(parameter, pattern));
                }
                return predicates.Count == 0 ? "" : "WHERE " + string.Join(" AND ", predicates);
            }
        public async Task<PagedResult> GetTableDataPagedAsync(string tableName, int pageNumber, int pageSize, CancellationToken ct = default)
            {
                var orderColumns = await GetRowIdentityColumnsAsync(tableName, ct);
                if (orderColumns.Count == 0)
                {
                    var columns = await GetTableColumnsAsync(tableName, ct);
                    if (columns.Count > 0) orderColumns.Add(columns[0].Name);
                }
    
                var dataTask = GetTableDataPageAsync(tableName, pageNumber, pageSize, orderColumns, null, ct);
                var countTask = GetTableRowCountAsync(tableName, null, ct);
                await Task.WhenAll(dataTask, countTask);
                return new PagedResult { Data = dataTask.Result, TotalCount = countTask.Result };
            }
    
            private async Task<List<string>> GetRowIdentityColumnsAsync(string tableName, CancellationToken ct)
            {
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                var (schema, table) = ParseTableName(tableName);
                return await GetRowIdentityColumnsInternalAsync(connection, schema, table, ct);
            }
            public async Task<DataTable> GetAllTableRowsAsync(string tableName, CancellationToken ct = default)
            {
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                using var command = new SqlCommand($"SELECT * FROM {QuoteTableName(tableName)}", connection);
                command.CommandTimeout = 0;
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                var table = new DataTable();
                table.Load(reader);
                return table;
            }

            public async Task<bool> RowExistsByValuesAsync(string tableName, Dictionary<string, object> values, CancellationToken ct = default)
            {
                if (values == null || values.Count == 0) return false;
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                var clauses = new List<string>();
                int index = 0;
                foreach (var pair in values)
                {
                    if (pair.Value == null || pair.Value == DBNull.Value) clauses.Add($"{QuoteIdentifier(pair.Key)} IS NULL");
                    else clauses.Add($"{QuoteIdentifier(pair.Key)} = @p{index++}");
                }
                using var command = new SqlCommand($"SELECT TOP (1) 1 FROM {QuoteTableName(tableName)} WHERE {string.Join(" AND ", clauses)}", connection);
                index = 0;
                foreach (var pair in values)
                    if (pair.Value != null && pair.Value != DBNull.Value) command.Parameters.AddWithValue($"@p{index++}", pair.Value);
                var result = await command.ExecuteScalarAsync(ct);
                return result != null && result != DBNull.Value;
            }

    }
}
