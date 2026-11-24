using Configurator.Core.Projects;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Configurator.Core.Architecture;

namespace Configurator.Infrastructure.Database;

public sealed class ClassRegistryService
{
    private readonly string _connectionString;

    public ClassRegistryService(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task EnsureRegistryAsync(CancellationToken ct = default)
    {
        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);

        const string existsSql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM sys.tables t
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = N'dbo' AND t.name = N'Classes'
            ) THEN 1 ELSE 0 END
            """;

        using var exists = new SqlCommand(existsSql, connection);
        bool existsResult = Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 1;

        if (!existsResult)
            throw new InvalidOperationException("Реестр dbo.Classes отсутствует в выбранной базе.");

        var columns = await GetColumnsAsync(connection, ct);
        if (!columns.Contains("Record") && !columns.Contains("ClassNumber"))
            throw new InvalidOperationException(
                "dbo.Classes существует, но в ней нет колонки Record или ClassNumber для номера класса.");

        if (!columns.Contains("Name"))
            throw new InvalidOperationException("dbo.Classes существует, но в ней нет колонки Name.");
    }

    public async Task<int> GetNextClassNumberAsync(CancellationToken ct = default)
    {
        await EnsureRegistryAsync(ct);

        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);

        string numberColumn = await ResolveNumberColumnAsync(connection, ct);
        using var command = new SqlCommand(
            $"SELECT ISNULL(MAX([{numberColumn}]), 0) + 1 FROM [dbo].[Classes]",
            connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<string> GetClassNameByNumberAsync(int classNumber, CancellationToken ct = default)
    {
        await EnsureRegistryAsync(ct);

        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        string numberColumn = await ResolveNumberColumnAsync(connection, ct);
        using var command = new SqlCommand(
            $"SELECT TOP (1) [Name] FROM [dbo].[Classes] WHERE [{numberColumn}] = @Number",
            connection);
        command.Parameters.Add("@Number", SqlDbType.Int).Value = classNumber;
        var value = await command.ExecuteScalarAsync(ct);
        return value == null || value == DBNull.Value ? null : Convert.ToString(value);
    }

    public async Task<Dictionary<int, bool>> GetAvailabilityAsync(CancellationToken ct = default)
    {
        await EnsureRegistryAsync(ct);
        using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        string numberColumn = await ResolveNumberColumnAsync(connection, ct);
        var columns = await GetColumnsAsync(connection, ct);
        if (!columns.Contains("Available")) return new Dictionary<int, bool>();
        var result = new Dictionary<int, bool>();
        using var command = new SqlCommand($"SELECT [{numberColumn}], [Available] FROM [dbo].[Classes]", connection);
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            int number = Convert.ToInt32(reader.GetValue(0));
            bool available = reader.IsDBNull(1) || Convert.ToBoolean(reader.GetValue(1));
            result[number] = available;
        }
        return result;
    }

    public Task UpsertAsync(ClassDefinition definition, CancellationToken ct = default) =>
        throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

    public Task RemoveAsync(ClassDefinition definition, CancellationToken ct = default) =>
        throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

    private static async Task<HashSet<string>> GetColumnsAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name
            FROM sys.columns c
            INNER JOIN sys.tables t ON t.object_id = c.object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = N'dbo' AND t.name = N'Classes'
            """;

        using var command = new SqlCommand(sql, connection);
        using var reader = await command.ExecuteReaderAsync(ct);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
            result.Add(Convert.ToString(reader[0]) ?? "");
        return result;
    }

    private static async Task<string> ResolveNumberColumnAsync(SqlConnection connection, CancellationToken ct)
    {
        var columns = await GetColumnsAsync(connection, ct);
        if (columns.Contains("Record")) return "Record";
        if (columns.Contains("ClassNumber")) return "ClassNumber";
        throw new InvalidOperationException("dbo.Classes не содержит Record/ClassNumber.");
    }
}
