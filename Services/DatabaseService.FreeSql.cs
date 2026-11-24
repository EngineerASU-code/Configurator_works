using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Configurator
{
    public partial class DatabaseService
    {
        public Task<(DataTable Result, int AffectedRows)> ExecuteFreeSqlAsync(
            string sql, string expectedDatabase = null, CancellationToken ct = default) =>
            ExecuteFreeSqlAsync(sql, null, expectedDatabase, ct);

        /// <summary>Maximum rows a free-SQL SELECT materializes; the rest is cancelled on the server.</summary>
        public const int FreeSqlMaxResultRows = 10_000;
        public static readonly TimeSpan FreeSqlConfirmationTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Runs one validated statement. SELECT results are capped at <see cref="FreeSqlMaxResultRows"/>
        /// (see DataTable.ExtendedProperties["Truncated"]). INSERT/UPDATE/DELETE run inside a transaction:
        /// when <paramref name="confirmAffectedRows"/> is supplied it receives the affected-row count before
        /// COMMIT, and a negative answer rolls the change back and throws <see cref="FreeSqlRolledBackException"/>.
        /// </summary>
        public Task<(DataTable Result, int AffectedRows)> ExecuteFreeSqlAsync(
            string sql, IReadOnlyDictionary<string, object> parameters,
            string expectedDatabase = null, CancellationToken ct = default,
            Func<int, Task<bool>> confirmAffectedRows = null) =>
            ExecuteFreeSqlCoreAsync(sql, parameters, expectedDatabase, ct, confirmAffectedRows, FreeSqlConfirmationTimeout);

        internal async Task<(DataTable Result, int AffectedRows)> ExecuteFreeSqlCoreAsync(
            string sql, IReadOnlyDictionary<string, object> parameters, string expectedDatabase,
            CancellationToken ct, Func<int, Task<bool>> confirmAffectedRows, TimeSpan confirmationTimeout)
        {
            var target = FreeSqlPolicy.Analyze(sql);
            bool expectsResult = target.Table == null;

            using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
            await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(expectedDatabase) &&
                !string.Equals(connection.Database, expectedDatabase, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Не удалось установить контекст базы «{expectedDatabase}». " +
                    $"Текущий контекст SQL Server: «{connection.Database}».");
            }

            using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            if (parameters != null)
            {
                var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in parameters)
                {
                    string rawName = pair.Key ?? string.Empty;
                    string bareName = rawName.StartsWith("@", StringComparison.Ordinal) ? rawName[1..] : rawName;
                    if (!IsValidParameterName(bareName))
                        throw new ArgumentException($"Имя SQL-параметра «{rawName}» недопустимо.", nameof(parameters));
                    string name = "@" + bareName;
                    if (!parameterNames.Add(name))
                        throw new ArgumentException($"SQL-параметр «{name}» указан более одного раза.", nameof(parameters));
                    command.Parameters.AddWithValue(name, pair.Value ?? DBNull.Value);
                }
            }

            if (command.CommandType != CommandType.Text)
                throw new InvalidOperationException("Разрешены только текстовые SQL-команды.");

            string traceSql = sql.Length > 2000 ? sql[..2000] + "…" : sql;

            if (expectsResult)
            {
                SessionTestLog.Trace("FREE-SQL", $"SELECT [{connection.Database}]: {traceSql}");
                bool truncated = false;
                var table = new DataTable();
                using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
                {
                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string name = string.IsNullOrEmpty(reader.GetName(i)) ? "Column" + (i + 1) : reader.GetName(i);
                        string unique = name;
                        for (int n = 2; !usedNames.Add(unique); n++) unique = name + "_" + n;
                        table.Columns.Add(unique, reader.GetFieldType(i));
                    }

                    table.BeginLoadData();
                    try
                    {
                        while (await reader.ReadAsync(ct))
                        {
                            if (table.Rows.Count >= FreeSqlMaxResultRows) { truncated = true; break; }
                            var values = new object[reader.FieldCount];
                            reader.GetValues(values);
                            table.Rows.Add(values);
                        }
                    }
                    finally { table.EndLoadData(); }

                    // Do not make Dispose() drain a huge remainder of the result set.
                    if (truncated) { try { command.Cancel(); } catch { /* best effort */ } }
                }
                table.ExtendedProperties["Truncated"] = truncated;
                return (table, -1);
            }

            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            int affected;
            try
            {
                // Verify the resolved target on the connection that performs the write.
                using var check = new SqlCommand("""
                    SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
                    WHERE t.is_ms_shipped=0 AND s.name=@schema AND t.name=@table
                    """, connection, transaction);
                check.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = target.Schema;
                check.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = target.Table;
                if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string resolved)
                    throw new InvalidOperationException("Цель записи должна быть существующей пользовательской таблицей.");
                Configurator.Core.Projects.ProjectWriteGuard.EnsureDataTableWritable(resolved);
                affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                SessionTestLog.Trace("FREE-SQL", $"DML [{connection.Database}] affected={affected}: {traceSql}");
                if (confirmAffectedRows != null && !await ConfirmFreeSqlAsync(confirmAffectedRows, affected, ct, confirmationTimeout).ConfigureAwait(false))
                {
                    transaction.Rollback();
                    SessionTestLog.Trace("FREE-SQL", $"ROLLBACK по решению пользователя; affected={affected}");
                    throw new FreeSqlRolledBackException(affected);
                }
                ct.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            catch (FreeSqlRolledBackException) { throw; }
            catch
            {
                try { transaction.Rollback(); } catch { /* connection may already be broken */ }
                throw;
            }
            return (null, affected);
        }

        internal static async Task<bool> ConfirmFreeSqlAsync(Func<int, Task<bool>> confirm, int affected, CancellationToken ct, TimeSpan? timeout = null)
        {
            try { return await confirm(affected).WaitAsync(timeout ?? FreeSqlConfirmationTimeout, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return false; }
        }

        private static bool IsValidParameterName(string name)
        {
            if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_'))
                return false;
            for (int i = 1; i < name.Length; i++)
                if (!(char.IsLetterOrDigit(name[i]) || name[i] == '_')) return false;
            return true;
        }
    }
}

namespace Configurator
{
    /// <summary>Raised when the user declined an UPDATE/INSERT/DELETE preview; the transaction was rolled back.</summary>
    public sealed class FreeSqlRolledBackException : Exception
    {
        public int AffectedRows { get; }
        public FreeSqlRolledBackException(int affectedRows)
            : base($"Изменения отменены (откат транзакции). Было бы затронуто строк: {affectedRows}.") => AffectedRows = affectedRows;
    }
}
