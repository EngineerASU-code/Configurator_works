#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;

namespace Configurator.Infrastructure.Database;

public sealed class ClassArchitectureReader
{
    private readonly Configurator.DatabaseService? _database;
    private readonly IProjectDataSource? _projectDataSource;

    /// <summary>Online reader backed by SQL Server.</summary>
    public ClassArchitectureReader(string connectionString)
    {
        _database = new Configurator.DatabaseService(connectionString);
    }

    /// <summary>Offline reader backed exclusively by the local .cfgproj snapshot.</summary>
    public ClassArchitectureReader(IProjectDataSource projectDataSource)
    {
        _projectDataSource = projectDataSource ?? throw new ArgumentNullException(nameof(projectDataSource));
    }

    public async Task<List<FieldDefinition>> LoadFieldsAsync(string tableName, string className = "", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_projectDataSource != null)
        {
            var schema = _projectDataSource.GetSchema(tableName);
            return (schema?.Columns ?? new List<ProjectColumnSchema>()).Select(c => new FieldDefinition
            {
                Name = c.Name,
                DataType = c.DataType,
                Size = c.MaxLength < 0 ? 0 : c.MaxLength,
                Nullable = c.IsNullable,
                DefaultValue = c.HasDefault ? "DEFAULT" : "",
                Description = c.IsIdentity ? "IDENTITY" : c.IsComputed ? "COMPUTED" : "",
                Group = FieldGroupCatalog.Resolve(className, c.Name)
            }).ToList();
        }
        var columns = await _database!.GetTableColumnsAsync(tableName, ct);
        return columns.Select(c => new FieldDefinition
        {
            Name = c.Name,
            DataType = c.DataType,
            Size = c.MaxLength < 0 ? 0 : c.MaxLength,
            Nullable = c.IsNullable,
            DefaultValue = c.HasDefault ? "DEFAULT" : "",
            Description = c.IsIdentity ? "IDENTITY" : c.IsComputed ? "COMPUTED" : "",
            Group = FieldGroupCatalog.Resolve(className, c.Name)
        }).ToList();
    }

    public async Task<DataTable> ReadRowsAsync(string tableName, int take = 500, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_projectDataSource != null)
        {
            var schema = _projectDataSource.GetSchema(tableName);
            if (schema == null) return new DataTable();
            var table = new DataTable(schema.TableName);
            foreach (var column in schema.Columns)
                table.Columns.Add(column.Name, ResolveClrType(column.DataType));
            foreach (var record in _projectDataSource.GetRecords(schema.TableName).Take(Math.Clamp(take, 1, 2000)))
            {
                var row = table.NewRow();
                foreach (DataColumn column in table.Columns)
                {
                    if (!record.Values.TryGetPropertyValue(column.ColumnName, out var node) || node == null)
                    {
                        row[column] = DBNull.Value;
                        continue;
                    }
                    row[column] = ConvertJsonValue(node, column.DataType);
                }
                table.Rows.Add(row);
            }
            return table;
        }
        var columns = await _database!.GetTableColumnsAsync(tableName, ct);
        if (columns.Count == 0) return new DataTable();
        var order = columns.Take(3).Select(c => c.Name).ToList();
        return await _database.GetTableDataPageAsync(tableName, 1, Math.Clamp(take, 1, 2000), order, null, ct);
    }

    public async Task<bool> ExistsAsync(string tableName, CancellationToken ct = default)
    {
        try
        {
            var fields = await LoadFieldsAsync(tableName, "", ct);
            return fields.Count > 0;
        }
        catch { return false; }
    }

    private static Type ResolveClrType(string sqlType) => (sqlType ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "bit" => typeof(bool),
        "tinyint" => typeof(byte),
        "smallint" => typeof(short),
        "int" => typeof(int),
        "bigint" => typeof(long),
        "real" => typeof(float),
        "float" => typeof(double),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "uniqueidentifier" => typeof(Guid),
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => typeof(byte[]),
        _ => typeof(string)
    };

    private static object ConvertJsonValue(JsonNode node, Type targetType)
    {
        try
        {
            if (targetType == typeof(string)) return node.ToString();
            if (targetType == typeof(byte[])) return Convert.FromBase64String(node.GetValue<string>());
            return Convert.ChangeType(node.ToString(), targetType, CultureInfo.InvariantCulture);
        }
        catch { return node.ToString(); }
    }

    public static string PickTypeName(DataRow row)
    {
        foreach (var name in new[] { "TypeName", "Name", "Type", "Code", "Record", "ID", "Id" })
        {
            if (!row.Table.Columns.Contains(name)) continue;
            string value = row[name] == DBNull.Value ? string.Empty : Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "Тип";
    }
}
