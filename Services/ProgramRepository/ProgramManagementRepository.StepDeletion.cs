using System.Data;
using System.Globalization;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator;

internal sealed record StepDeletionPreview(IReadOnlyList<IReadOnlyDictionary<string, object>> Rows,
    IReadOnlyList<ProjectColumnSchema> Destinations, int Number, int IncomingCount, IReadOnlyList<int> Candidates,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>> Children)
{
    public bool NeedsReplacement => IncomingCount > 0 || Children.Values.Any(rows => rows.Count > 0);
}

public sealed partial class ProgramManagementRepository
{
    internal async Task<StepDeletionPreview> PrepareStepDeletionAsync(ProgramContext context, int number)
    {
        var scope = ProgramScope(context, includeSequence: true);
        ProjectTableSchema schema;
        if (_offline != null) schema = GetOfflineSchema(TransitionTable);
        else
        {
            using var connection = SqlProviderGuard.CreateConnection(_connectionString);
            await SqlProviderGuard.OpenConnectionAsync(connection, CancellationToken.None);
            schema = await OnlineProjectDataSource.ReadSchemaAsync(connection, null, TransitionTable, CancellationToken.None);
        }
        var destinations = StepDestinationColumns(schema);
        var rows = SnapshotRows(await GetScopedTableAsync(TransitionTable, scope, CancellationToken.None, readAll: true));
        var other = rows.Where(row => ReadStepNumber(row["SeqStep_Number"]) != number).ToArray();
        int incomingCount = other.Sum(row => destinations.Count(column => ReadStepNumber(row[column.Name]) == number));
        var candidates = other.GroupBy(row => ReadStepNumber(row["SeqStep_Number"]))
            .Where(group => group.Key > 0 && group.Count() == 1 &&
                !destinations.Any(column => ReadStepNumber(group.Single()[column.Name]) == number))
            .Select(group => group.Key).Order().ToArray();
        var childScope = new Dictionary<string, object>(scope, StringComparer.OrdinalIgnoreCase) { ["Step_Number"] = number };
        var children = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        children.Add(RecipeValuesTable, await ReadContextReplacementRowsAsync(RecipeValuesTable, childScope, CancellationToken.None));
        return new StepDeletionPreview(rows, destinations, number, incomingCount, candidates, children);
    }

    internal async Task<IReadOnlyList<ChangeJournalRowChange>> ReplaceStepReferencesAndDeleteAsync(
        ProgramContext context, StepDeletionPreview preview, int replacement,
        IReadOnlyDictionary<string, object> expected, CancellationToken ct = default)
    {
        if (!preview.NeedsReplacement || !preview.Candidates.Contains(replacement))
            throw new InvalidOperationException("Выберите другое вхождение, которое не станет ссылаться на себя.");
        var scope = ProgramScope(context, includeSequence: true);
        var target = new Dictionary<string, object>(scope, StringComparer.OrdinalIgnoreCase) { ["SeqStep_Number"] = preview.Number };
        var childScope = new Dictionary<string, object>(scope, StringComparer.OrdinalIgnoreCase) { ["Step_Number"] = preview.Number };
        var keys = UniqueKeyColumns[TransitionTable];
        var journal = new List<ChangeJournalRowChange>();

        async Task ApplyAsync(ProjectTableSchema schema,
            Func<Task<IReadOnlyList<IReadOnlyDictionary<string, object>>>> readRows,
            Func<string, IReadOnlyDictionary<string, object>, Task<IReadOnlyList<IReadOnlyDictionary<string, object>>>> readChildren,
            Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, object>, object, Task> moveChild,
            Func<IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>, Task> update,
            Func<Task> delete)
        {
            var destinations = StepDestinationColumns(schema);
            if (!destinations.Select(column => (column.Name, column.DataType))
                    .SequenceEqual(preview.Destinations.Select(column => (column.Name, column.DataType))))
                throw new InvalidOperationException("Поля назначения изменились. Повторите подтверждение удаления.");
            var rows = await readRows();
            EnsureSnapshotMatches(preview.Rows, rows);
            var byNumber = rows.ToDictionary(row => ReadStepNumber(row["SeqStep_Number"]));
            EnsureSnapshotMatches(new[] { expected }, new[] { byNumber[preview.Number] });
            var recipeRows = preview.Children[RecipeValuesTable];
            EnsureSnapshotMatches(recipeRows, await readChildren(RecipeValuesTable, childScope));

            if (recipeRows.Count > 0)
            {
                var definitions = InParameterScope(await readChildren(ParameterTable, scope), 1);
                foreach (var before in recipeRows)
                {
                    if (definitions.Count(parameter => ValuesEqual(parameter["Record"], before["Record"])
                            && ValuesEqual(parameter["Type"], before["Type"])) != 1)
                        throw new InvalidOperationException("Для значения рецепта не найдено однозначное определение параметра шага.");
                    var key = ContextReplacementKey(before, RecipeValueKey);
                    var newValue = Convert.ChangeType(replacement, before["Step_Number"].GetType(), CultureInfo.InvariantCulture);
                    var after = new Dictionary<string, object>(before, StringComparer.OrdinalIgnoreCase) { ["Step_Number"] = newValue };
                    var newKey = ContextReplacementKey(after, RecipeValueKey);
                    if ((await readChildren(RecipeValuesTable, newKey)).Count != 0)
                        throw new InvalidOperationException($"Перенос на шаг {replacement} невозможен: в {RecipeValuesTable} уже есть строка с таким составным ключом.");
                    await moveChild(RecipeValuesTable, RecipeValueKey, key, newValue);
                    var actualChild = await readChildren(RecipeValuesTable, newKey);
                    EnsureSnapshotMatches(new[] { after }, actualChild);
                    journal.Add(ContextReplacementJournal(RecipeValuesTable, RecipeValueKey, before, actualChild[0]));
                }
            }
            EnsureSnapshotMatches(Array.Empty<IReadOnlyDictionary<string, object>>(), await readChildren(RecipeValuesTable, childScope));

            var updated = new List<int>();
            var expectedAfter = new List<IReadOnlyDictionary<string, object>>();
            foreach (var (number, row) in byNumber)
            {
                if (number == preview.Number) continue;
                var assignments = destinations.Where(column => ReadStepNumber(row[column.Name]) == preview.Number)
                    .ToDictionary(column => column.Name, column => FormatStepDestination(column, replacement), StringComparer.OrdinalIgnoreCase);
                var after = new Dictionary<string, object>(row, StringComparer.OrdinalIgnoreCase);
                if (assignments.Count > 0)
                {
                    var key = keys.ToDictionary(key => key, key => row[key], StringComparer.OrdinalIgnoreCase);
                    await update(key, assignments);
                    updated.Add(number);
                    foreach (var (column, value) in assignments) after[column] = value;
                }
                expectedAfter.Add(after);
            }
            await delete();
            var actualAfter = await readRows();
            EnsureSnapshotMatches(expectedAfter, actualAfter);
            var afterByNumber = actualAfter.ToDictionary(row => ReadStepNumber(row["SeqStep_Number"]));
            foreach (int number in updated)
                journal.Add(JournalStepDeletionRow(ChangeJournalOperationKind.Update, byNumber[number], afterByNumber[number]));
            journal.Add(JournalStepDeletionRow(ChangeJournalOperationKind.Delete, byNumber[preview.Number], null));
        }

        if (_offline != null)
        {
            await _offline.ExecuteMutationTransactionAsync(async () =>
            {
                var schema = GetOfflineSchema(TransitionTable);
                foreach (var other in _offline.Project.TableSchemas.Where(table =>
                    table.TableName != TransitionTable && table.TableName != ParameterTable && table.TableName != RecipeValuesTable &&
                    HasStepContextColumns(table.Columns.Select(column => column.Name), scope.Keys)))
                    if (SnapshotOfflineRows(other.TableName, scope).Count > 0)
                        throw new InvalidOperationException($"Не подтверждена замена ссылок в {other.TableName}.");
                var source = (IProjectCrudDataSource)_offline;
                await ApplyAsync(schema,
                    () => Task.FromResult(SnapshotOfflineRows(TransitionTable, scope)),
                    (table, key) => Task.FromResult(SnapshotOfflineRows(table, key)),
                    async (table, childKeys, key, value) =>
                    {
                        var childSchema = GetOfflineSchema(table);
                        if (!childSchema.HasStableKey || !childSchema.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(childKeys))
                            throw new InvalidOperationException($"Не подтверждён составной ключ {table}.");
                        await source.UpdateRecordAsync(table, childSchema.KeyColumns, key,
                            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Step_Number"] = value }, ct);
                    },
                    (key, assignments) => source.UpdateRecordAsync(TransitionTable, schema.KeyColumns, key, assignments, ct),
                    () => DeleteOfflineIfUnreferencedAsync(TransitionTable, target,
                        Array.Empty<(string, IReadOnlyDictionary<string, object>)>(), ct, expected));
            }, ct);
            return journal;
        }

        using var connection = SqlProviderGuard.CreateConnection(_connectionString);
        await SqlProviderGuard.OpenConnectionAsync(connection, ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await EnsureSafeReplacementSqlAsync(connection, transaction, TransitionTable, ct);
        var onlineSchema = await OnlineProjectDataSource.ReadSchemaAsync(connection, transaction, TransitionTable, ct);
        using (var metadata = new SqlCommand("""
            SELECT TOP (1) QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)
            FROM sys.tables t
            WHERE (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id=t.object_id
                    AND c.name IN ('PLC','PLC_Class_Prog_Number','Prog_Record','Seq_Record'))=4
              AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=t.object_id
                    AND c.name IN ('SeqStep_Number','Step_Number'))
              AND t.object_id NOT IN (OBJECT_ID('dbo.Prog_StepTransition'),OBJECT_ID('dbo.Prog_StepParam'),OBJECT_ID('dbo.Prog_Recipes'));
            """, connection, transaction))
        {
            if (await metadata.ExecuteScalarAsync(ct) is string table)
                throw new InvalidOperationException($"Не подтверждена замена ссылок в {table}.");
        }

        async Task WriteAsync(string sql, IReadOnlyDictionary<string, object> key, IReadOnlyDictionary<string, object> values = null)
        {
            var where = BuildPredicates(key, "stepKey", out var parameters);
            var assignments = values?.Select((pair, index) => (pair.Key, pair.Value, Parameter: "@destination" + index)).ToArray();
            if (assignments != null)
                sql += " SET " + string.Join(", ", assignments.Select(item => $"{QuoteIdentifier(item.Key)}={item.Parameter}"));
            using var command = new SqlCommand($"{sql} WHERE {where}", connection, transaction);
            AddParameters(command, parameters);
            if (assignments != null) AddParameters(command, assignments.Select(item => (item.Parameter, item.Value)));
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Изменение вхождения не подтвердилось. Удаление отменено.");
        }

        if (preview.Children[RecipeValuesTable].Count > 0)
        {
            await EnsureUniqueIndexAsync(connection, transaction, RecipeValuesTable, RecipeValueKey, ct);
            await EnsureSafeReplacementSqlAsync(connection, transaction, RecipeValuesTable, ct);
        }

        await ApplyAsync(onlineSchema,
            () => ReadLockedRowsAsync(connection, transaction, TransitionTable, scope, ct),
            async (table, key) => await HasScopedRowsAsync(connection, transaction, table, key, ct)
                ? await ReadLockedRowsAsync(connection, transaction, table, key, ct)
                : Array.Empty<IReadOnlyDictionary<string, object>>(),
            (table, _, key, value) => WriteContextReplacementSqlAsync(connection, transaction, table, key, "Step_Number", value, ct),
            (key, values) => WriteAsync($"UPDATE {QuoteTable(TransitionTable)}", key, values),
            () => WriteAsync($"DELETE FROM {QuoteTable(TransitionTable)}", target));
        await transaction.CommitAsync(ct);
        return journal;
    }

    private static bool HasStepContextColumns(IEnumerable<string> columnNames, IEnumerable<string> context)
    {
        var names = columnNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return context.All(names.Contains) && (names.Contains("SeqStep_Number") || names.Contains("Step_Number"));
    }

    private static IReadOnlyList<ProjectColumnSchema> StepDestinationColumns(ProjectTableSchema schema)
    {
        const string prefix = "TransDest_";
        if (schema == null || !schema.HasStableKey || !schema.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(UniqueKeyColumns[TransitionTable]))
            throw new InvalidOperationException("Не подтверждён стабильный ключ переходов.");
        var columns = schema.Columns.Where(column => column.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(column.Name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .OrderBy(column => column.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (columns.Length == 0 || columns.Any(column => !IsNumericOrText(column.DataType)))
            throw new InvalidOperationException("Не подтверждены поля назначения переходов.");
        return columns;
    }

    private static int ReadStepNumber(object value) => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : 0;

    private static object FormatStepDestination(ProjectColumnSchema column, int number)
    {
        if (column.IsIdentity || column.IsComputed || column.IsReadOnly)
            throw new InvalidOperationException($"Поле назначения {column.Name} доступно только для чтения.");
        var type = OfflineType(column.DataType);
        return Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
    }

    private static ChangeJournalRowChange JournalStepDeletionRow(ChangeJournalOperationKind kind,
        IReadOnlyDictionary<string, object> before, IReadOnlyDictionary<string, object> after)
    {
        var identity = UniqueKeyColumns[TransitionTable].ToDictionary(column => column,
            column => ChangeJournalValue.FromObject(before[column]), StringComparer.OrdinalIgnoreCase);
        return new ChangeJournalRowChange
        {
            TableName = TransitionTable,
            Kind = kind,
            IdentityBefore = identity,
            IdentityAfter = after == null ? new(StringComparer.OrdinalIgnoreCase) : identity,
            Before = before.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase),
            After = after?.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase)
                ?? new(StringComparer.OrdinalIgnoreCase)
        };
    }

    internal static async Task EnsureSafeReplacementSqlAsync(SqlConnection connection, SqlTransaction transaction,
        string tableName, CancellationToken ct)
    {
        using var command = new SqlCommand("""
            SELECT TOP (1) name FROM sys.triggers
            WHERE parent_id = OBJECT_ID(@table) AND is_disabled = 0
            UNION ALL
            SELECT TOP (1) name FROM sys.foreign_keys
            WHERE referenced_object_id = OBJECT_ID(@table) AND is_disabled = 0
              AND (delete_referential_action <> 0 OR update_referential_action <> 0);
            """, connection, transaction);
        command.Parameters.AddWithValue("@table", tableName);
        if (await command.ExecuteScalarAsync(ct) is string name)
            throw new InvalidOperationException($"Замена заблокирована: {tableName} использует триггер или каскадное ограничение {name}.");
    }
}
