using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator.Infrastructure.Database;

internal sealed record SavedSqlQuery(int Id, string Name);

internal sealed class SavedSqlQueryStore(string connectionString, string database)
{
    public async Task<List<SavedSqlQuery>> ListAsync()
    {
        using var connection = await OpenAsync();
        var queries = new List<SavedSqlQuery>();
        if (!await ExistsAsync(connection)) throw new InvalidOperationException("Библиотека запросов недоступна: служебная таблица отсутствует. Изменение схемы запрещено.");

        using var command = new SqlCommand(
            "SELECT Id, Name FROM dbo.__ConfiguratorSavedQueries ORDER BY Name, Id;", connection);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            queries.Add(new SavedSqlQuery(reader.GetInt32(0), reader.GetString(1)));
        return queries;
    }

    public async Task<(SavedSqlQuery Query, string Sql)> ReadAsync(int id)
    {
        using var connection = await OpenAsync();
        using var command = new SqlCommand(
            "SELECT Name, SqlText FROM dbo.__ConfiguratorSavedQueries WHERE Id = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Запрос больше не найден в базе. Обновите список.");
        return (new SavedSqlQuery(id, reader.GetString(0)), reader.GetString(1));
    }

    public async Task<SavedSqlQuery> SaveAsync(int? id, string name, string sql, string expectedName = null, string expectedSql = null)
    {
        name = name.Trim();
        if (name.Length is 0 or > 128 || string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("Укажите имя запроса (до 128 символов) и введите SQL.");

        using var connection = await OpenAsync();
        if (!await ExistsAsync(connection)) throw new InvalidOperationException("Библиотека запросов недоступна: служебная таблица отсутствует. Изменение схемы запрещено.");
        if (id.HasValue && (expectedName == null || expectedSql == null))
            throw new InvalidOperationException("Неизвестна исходная версия запроса. Откройте его заново.");

        using var command = new SqlCommand(id.HasValue
            ? "UPDATE dbo.__ConfiguratorSavedQueries SET Name = @name, SqlText = @sql WHERE Id = @id AND CONVERT(varbinary(max), Name) = CONVERT(varbinary(max), @expectedName) AND CONVERT(varbinary(max), SqlText) = CONVERT(varbinary(max), @expectedSql); SELECT @@ROWCOUNT;"
            : "INSERT INTO dbo.__ConfiguratorSavedQueries (Name, SqlText) OUTPUT INSERTED.Id VALUES (@name, @sql);",
            connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
        command.Parameters.Add("@sql", SqlDbType.NVarChar, -1).Value = sql;
        if (id.HasValue)
        {
            command.Parameters.Add("@id", SqlDbType.Int).Value = id.Value;
            command.Parameters.Add("@expectedName", SqlDbType.NVarChar, 128).Value = expectedName;
            command.Parameters.Add("@expectedSql", SqlDbType.NVarChar, -1).Value = expectedSql;
        }

        try
        {
            int result = (int)await command.ExecuteScalarAsync();
            if (id.HasValue && result == 0)
                throw new InvalidOperationException("Запрос изменён или удалён другим пользователем. Откройте его заново.");
            return new SavedSqlQuery(id ?? result, name);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new InvalidOperationException("Запрос с таким именем уже есть в этой базе.", ex);
        }
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = SqlProviderGuard.CreateConnection(connectionString);
        try
        {
            await SqlProviderGuard.OpenConnectionAsync(connection);
            if (!string.Equals(connection.Database, database, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Не удалось открыть базу проекта «{database}».");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection)
    {
        using var command = new SqlCommand(
            "SELECT OBJECT_ID(N'dbo.__ConfiguratorSavedQueries', N'U');", connection);
        return await command.ExecuteScalarAsync() is int;
    }

}
