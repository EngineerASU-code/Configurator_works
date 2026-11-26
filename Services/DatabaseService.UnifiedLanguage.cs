using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;

namespace Configurator;

public partial class DatabaseService
{
    private static readonly ConcurrentDictionary<(string, string), bool> LanguageProjects = new();
    private (string, string) LanguageProject
    {
        get
        {
            var builder = new SqlConnectionStringBuilder(_connectionString);
            return (builder.DataSource.ToUpperInvariant(), builder.InitialCatalog.ToUpperInvariant());
        }
    }

    internal bool UsesUnifiedLanguage => LanguageProjects.ContainsKey(LanguageProject);

    internal async Task SetUnifiedLanguageAsync(bool enabled)
    {
        var project = LanguageProject;
        if (!enabled) { LanguageProjects.TryRemove(project, out _); return; }
        var source = new OnlineProjectDataSource(_connectionString);
        var tables = await source.GetTablesAsync();
        using var connection = SqlProviderGuard.CreateConnection(_connectionString);
        await SqlProviderGuard.OpenConnectionAsync(connection, default);
        using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        foreach (var table in tables)
        {
            var pairs = UnifiedLanguage.Pairs(table.Columns.Select(c => c.Name)).ToArray();
            if (pairs.Length == 0) continue;
            if (!ProjectWriteGuard.IsDataTableWritable(table.TableName)) continue;
            foreach (var (_, target) in pairs)
                if (table.KeyColumns.Contains(target, StringComparer.OrdinalIgnoreCase) ||
                    table.Columns.Any(c => c.Name == target && (c.IsReadOnly || c.IsComputed || c.IsIdentity)))
                    throw new InvalidOperationException($"Поле {table.TableName}.{target} защищено от записи.");
            string assignments = string.Join(", ", pairs.Select(p => $"{QuoteIdentifier(p.Target)} = {QuoteIdentifier(p.Source)}"));
            // Binary comparison preserves case, accents and trailing spaces under any SQL collation.
            string differences = string.Join(" OR ", pairs.Select(p =>
            {
                string a = QuoteIdentifier(p.Source), b = QuoteIdentifier(p.Target);
                return $"({a} IS NULL AND {b} IS NOT NULL OR {a} IS NOT NULL AND {b} IS NULL OR CONVERT(varbinary(max), {a}) <> CONVERT(varbinary(max), {b}) OR DATALENGTH({a}) <> DATALENGTH({b}))";
            }));
            using var command = new SqlCommand($"UPDATE {QuoteTableName(table.TableName)} SET {assignments} WHERE {differences}", connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        LanguageProjects[project] = true;
    }
}
