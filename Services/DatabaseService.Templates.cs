using Configurator.Core.Projects;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Configurator
{
    public partial class DatabaseService
    {
            public async Task<List<TemplateColumn>> GetTemplateColumnsAsync(string tableName, CancellationToken ct = default)
            {
                var result = new List<TemplateColumn>();
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                var (schema, table) = ParseTableName(tableName);
    
                const string sql = @"
    SELECT c.name AS ColumnName,
           t.name AS DataType,
           c.max_length AS MaxLength,
           c.precision AS [Precision],
           c.scale AS Scale,
           c.is_nullable AS IsNullable,
           c.is_identity AS IsIdentity,
           c.is_computed AS IsComputed,
           dc.definition AS DefaultDefinition,
           CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey
    FROM sys.columns c
    JOIN sys.tables tb ON tb.object_id = c.object_id
    JOIN sys.schemas s ON s.schema_id = tb.schema_id
    JOIN sys.types t ON t.user_type_id = c.user_type_id
    LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
    LEFT JOIN (
        SELECT ic.object_id, ic.column_id
        FROM sys.index_columns ic
        JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.is_primary_key = 1
    ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
    WHERE s.name = @schema AND tb.name = @table
    ORDER BY c.column_id";
    
                using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@schema", schema);
                command.Parameters.AddWithValue("@table", table);
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    bool computed = Convert.ToBoolean(reader["IsComputed"]);
                    string type = reader["DataType"].ToString() ?? "int";
                    result.Add(new TemplateColumn
                    {
                        Name = reader["ColumnName"].ToString() ?? "",
                        OriginalName = reader["ColumnName"].ToString() ?? "",
                        DataType = type,
                        MaxLength = Convert.ToInt32(reader["MaxLength"]),
                        Precision = Convert.ToByte(reader["Precision"]),
                        Scale = Convert.ToByte(reader["Scale"]),
                        IsNullable = Convert.ToBoolean(reader["IsNullable"]),
                        IsIdentity = Convert.ToBoolean(reader["IsIdentity"]),
                        IsPrimaryKey = Convert.ToBoolean(reader["IsPrimaryKey"]),
                        IsComputed = computed,
                        HasDefault = reader["DefaultDefinition"] != DBNull.Value,
                        DefaultDefinition = reader["DefaultDefinition"] == DBNull.Value ? "" : reader["DefaultDefinition"].ToString() ?? "",
                        IsExisting = true
                    });
                }
                return result;
            }
    
            public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
            {
                using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
                var (schema, table) = ParseTableName(tableName);
                const string sql = "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@schema AND t.name=@table) THEN 1 ELSE 0 END";
                using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@schema", schema);
                command.Parameters.AddWithValue("@table", table);
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 1;
            }
    
            public Task DropTableAsync(string tableName, CancellationToken ct = default) =>
                throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

            public Task CreateTemplateTableAsync(string tableName, List<TemplateColumn> columns, CancellationToken ct = default) =>
                throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

            public Task SyncTemplateColumnsAsync(string tableName, List<TemplateColumn> desired, CancellationToken ct = default) =>
                throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

            public Task RenameTableAsync(string oldTableName, string newTableName, CancellationToken ct = default) =>
                throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);
    }
}
