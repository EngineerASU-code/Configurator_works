using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
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
            CancellationToken ct, Func<int, Task<bool>> confirmAffectedRows, TimeSpan confirmationTimeout, Action<IReadOnlyList<ChangeJournalRowChange>> committed = null)
        {
            var target = FreeSqlPolicy.Analyze(sql, out var modification);
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
            List<ChangeJournalRowChange> changes = null;
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
                if (committed == null)
                    affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                else
                {
                    changes = await CaptureFreeSqlAsync(command, modification, target.Schema, target.Table, ct);
                    affected = changes.Count;
                }
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
            if (changes?.Count > 0) committed(changes);
            return (null, affected);
        }

        private async Task<List<ChangeJournalRowChange>> CaptureFreeSqlAsync(SqlCommand command,
            DataModificationSpecification modification, string schema, string table, CancellationToken ct)
        {
            // OUTPUT captures old and new keys on the same row, even for a multi-row key update.
            using var metadata = new SqlCommand("""
                SELECT c.name, CASE WHEN EXISTS (
                    SELECT 1 FROM sys.indexes i JOIN sys.index_columns k ON k.object_id=i.object_id AND k.index_id=i.index_id
                    WHERE i.object_id=c.object_id AND i.is_primary_key=1 AND k.column_id=c.column_id AND k.key_ordinal>0
                ) THEN 1 ELSE 0 END, c.system_type_id,
                CASE WHEN EXISTS (SELECT 1 FROM sys.triggers tr JOIN sys.trigger_events e ON e.object_id=tr.object_id
                    WHERE tr.parent_id=t.object_id AND tr.is_disabled=0 AND e.type_desc=@operation)
                    OR EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.referenced_object_id=t.object_id AND fk.is_disabled=0
                        AND ((@operation='DELETE' AND fk.delete_referential_action<>0) OR (@operation='UPDATE' AND fk.update_referential_action<>0)))
                THEN 1 ELSE 0 END
                FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id
                JOIN sys.schemas s ON s.schema_id=t.schema_id
                WHERE s.name=@schema AND t.name=@table AND c.is_hidden=0
                ORDER BY c.column_id
                """, command.Connection, command.Transaction);
            metadata.Parameters.AddWithValue("@schema", schema);
            metadata.Parameters.AddWithValue("@table", table);
            metadata.Parameters.AddWithValue("@operation", modification is InsertSpecification ? "INSERT" :
                modification is DeleteSpecification ? "DELETE" : "UPDATE");
            bool mayHaveRelatedChanges = false;
            var columns = new List<string>();
            var keys = new List<string>();
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await metadata.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(0);
                    columns.Add(name);
                    mayHaveRelatedChanges = reader.GetInt32(3) != 0;
                    if (reader.GetByte(2) == 189) versions.Add(name);
                    if (reader.GetInt32(1) == 1) keys.Add(name);
                }
            }
            var kind = modification switch
            {
                InsertSpecification => ChangeJournalOperationKind.Insert,
                DeleteSpecification => ChangeJournalOperationKind.Delete,
                _ => ChangeJournalOperationKind.Update
            };
            var sides = kind == ChangeJournalOperationKind.Update ? new[] { "DELETED", "INSERTED" }
                : new[] { kind == ChangeJournalOperationKind.Delete ? "DELETED" : "INSERTED" };
            var source = QuoteIdentifier(schema) + "." + QuoteIdentifier(table);
            var temp = "#journal" + Guid.NewGuid().ToString("N");
            var fields = sides.SelectMany((side, index) => columns.Select((column, i) =>
                (versions.Contains(column) ? $"CONVERT(binary(8),src.{QuoteIdentifier(column)})" : $"src.{QuoteIdentifier(column)}") + $" AS [v{index}_{i}]"));
            var output = sides.SelectMany(side => columns.Select(column => side + "." + QuoteIdentifier(column)));
            var capture = " OUTPUT " + string.Join(", ", output) + " INTO " + temp + " ";
            var sql = command.CommandText;
            var offset = modification switch
            {
                InsertSpecification insert => insert.InsertSource.StartOffset,
                UpdateSpecification update => update.SetClauses.Last().StartOffset + update.SetClauses.Last().FragmentLength,
                DeleteSpecification delete => delete.Target.StartOffset + delete.Target.FragmentLength,
                _ => throw new InvalidOperationException()
            };
            // A user OUTPUT clause can coexist with our preceding OUTPUT INTO.
            sql = sql.Insert(modification.OutputClause?.StartOffset ?? offset, capture);
            command.CommandText = $"SELECT TOP (0) {string.Join(", ", fields)} INTO {temp} FROM {source} src UNION ALL SELECT TOP (0) {string.Join(", ", fields)} FROM {source} src; " +
                sql + $"\n; SELECT * FROM {temp};";
            var result = new List<ChangeJournalRowChange>();
            await using var rows = await command.ExecuteReaderAsync(ct);
            if (modification.OutputClause != null && !await rows.NextResultAsync(ct))
                throw new InvalidOperationException("SQL не вернул снимки для журнала.");
            if (rows.FieldCount != columns.Count * sides.Length || rows.GetName(0) != "v0_0")
                throw new InvalidOperationException("SQL вернул неожиданный формат снимка для журнала.");
            while (await rows.ReadAsync(ct))
            {
                Dictionary<string, ChangeJournalValue> Snapshot(int offset) => columns.Select((name, i) =>
                    (name, value: ChangeJournalValue.FromObject(rows.GetValue(offset + i))))
                    .ToDictionary(x => x.name, x => x.value, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, ChangeJournalValue> Identity(Dictionary<string, ChangeJournalValue> values) =>
                    values.Where(x => keys.Count == 0 || keys.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                var before = kind == ChangeJournalOperationKind.Insert ? new() : Snapshot(0);
                var after = kind == ChangeJournalOperationKind.Delete ? new() : Snapshot(kind == ChangeJournalOperationKind.Update ? columns.Count : 0);
                result.Add(new ChangeJournalRowChange { Kind = kind, TableName = schema + "." + table, Before = before,
                    CaptureWarning = mayHaveRelatedChanges ? "Для таблицы включён триггер или каскадное изменение. Журнал содержит только строки целевой таблицы." : "",
                    After = after, IdentityBefore = Identity(before), IdentityAfter = Identity(after) });
            }
            return result;
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
