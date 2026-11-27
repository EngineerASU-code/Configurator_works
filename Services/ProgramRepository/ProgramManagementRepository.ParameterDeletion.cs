using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator;

public sealed partial class ProgramManagementRepository
{
    private const string RecipeValuesTable = "dbo.Prog_Recipes";
    private static readonly IReadOnlyList<string> RecipeValueKey = ProgramSupportCatalog.ProgramTables
        .Single(table => table.TableName == RecipeValuesTable).KeyColumns;

    internal sealed record ParameterDeletionPreview(
        IReadOnlyDictionary<string, object> Scope,
        object Record,
        IReadOnlyList<DependencyReplacementWindow.DependencyOption> Options);

    internal async Task<ParameterDeletionPreview> PrepareParameterDeletionAsync(ProgramContext context,
        int stepNumber, object type, object record, CancellationToken ct = default)
    {
        var scope = ProgramScope(context, includeSequence: true);
        scope["Step_Number"] = stepNumber;
        scope["Type"] = type;

        var recipeScope = RecipeScopeForParameter(scope);
        var recipeValues = InParameterScope(_offline != null
            ? SnapshotOfflineRows(RecipeValuesTable, recipeScope)
            : SnapshotRows(await GetScopedTableAsync(RecipeValuesTable, recipeScope, ct, readAll: true)), stepNumber);
        var dependencies = recipeValues.Where(row => ValuesEqual(row["Record"], record)).ToArray();
        if (dependencies.Length == 0)
            return new ParameterDeletionPreview(scope, record, Array.Empty<DependencyReplacementWindow.DependencyOption>());

        var parameters = InParameterScope(SnapshotRows(await GetScopedTableAsync(ParameterTable, recipeScope, ct, readAll: true)), stepNumber);
        if (parameters.Count(row => ValuesEqual(row["Record"], record)) != 1)
            throw new InvalidOperationException("Номер определения параметра неоднозначен в последовательности.");
        var available = parameters.Where(row => row["Record"] != DBNull.Value && !ValuesEqual(row["Record"], record))
            .GroupBy(row => row["Record"]).Where(group => group.Count() == 1).Select(group => group.Single())
            .Where(row => ValuesEqual(row["Step_Number"], stepNumber))
            .Select(row => new ReplacementCandidate
            {
                Values = new[] { row["Record"] },
                TargetValues = row,
                Display = DependencyRecordLabel.Name(row)
            }).ToArray();
        var removedParameter = parameters.FirstOrDefault(row => ValuesEqual(row["Record"], record));
        var relation = new DatabaseRelation { SourceTable = RecipeValuesTable, TargetTable = ParameterTable }
            .WithColumns(new[] { "Record" }, new[] { "Record" });
        var options = dependencies.Select(row =>
        {
            var recipe = row["Recipe_Record"];
            var candidates = available.Where(candidate => !recipeValues.Any(value =>
                ValuesEqual(value["Recipe_Record"], recipe) && ValuesEqual(value["Step_Number"], row["Step_Number"])
                && ValuesEqual(value["Record"], candidate.Values[0]))).ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException($"Рецепт № {recipe} ссылается на параметр № {record}. В последовательности нет свободного параметра такого типа для замены.");

            return new DependencyReplacementWindow.DependencyOption
            {
                Hit = new DependencyHit
                {
                    SourceTable = RecipeValuesTable,
                    TargetTable = ParameterTable,
                    SourceKey = SelectRecipeParameterKey(row, RecipeValueKey),
                    SourceValues = row,
                    TargetName = removedParameter == null ? "" : DependencyRecordLabel.Name(removedParameter),
                    TargetKey = new Dictionary<string, object> { ["Record"] = record },
                    Relation = relation
                },
                Candidates = candidates
            };
        }).ToArray();
        return new ParameterDeletionPreview(scope, record, options);
    }

    internal async Task<IReadOnlyList<ChangeJournalRowChange>> DeleteParameterWithReplacementsAsync(
        ParameterDeletionPreview preview, IReadOnlyDictionary<string, object> expected,
        IReadOnlyList<ReferenceReplacement> replacements, CancellationToken ct = default)
    {
        var parameterKey = new Dictionary<string, object>(preview.Scope, StringComparer.OrdinalIgnoreCase)
        {
            ["Record"] = preview.Record
        };
        var selections = preview.Options.Select(option =>
        {
            var replacement = replacements.SingleOrDefault(replacement =>
                replacement.SourceTable == RecipeValuesTable &&
                replacement.KeyColumns.SequenceEqual(option.Hit.SourceKey.Keys, StringComparer.OrdinalIgnoreCase) &&
                replacement.KeyValues.SequenceEqual(option.Hit.SourceKey.Values));
            if (replacement?.NewValues.Count != 1)
                throw new InvalidOperationException("Выберите замену для каждого связанного рецепта.");
            var candidate = option.Candidates.SingleOrDefault(item => ValuesEqual(item.Values[0], replacement.NewValues[0]))
                ?? throw new InvalidOperationException("Выбран недоступный параметр для замены.");
            return (Before: option.Hit.SourceValues, Candidate: candidate.TargetValues);
        }).ToArray();
        if (selections.Length != replacements.Count)
            throw new InvalidOperationException("Нужен подтверждённый список замен в связанных рецептах.");

        var changes = new List<ChangeJournalRowChange>();
        if (_offline != null)
        {
            var schema = _offline.GetSchema(RecipeValuesTable);
            if (selections.Length > 0 && (schema == null || !schema.HasStableKey || !schema.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(RecipeValueKey)))
                throw new InvalidOperationException("В локальном проекте нет подтверждённого ключа значений рецептов.");
            await _offline.ExecuteMutationTransactionAsync(async () =>
            {
                await ApplyReplacementsAsync(null, null, schema?.KeyColumns ?? RecipeValueKey);
                await DeleteOfflineIfUnreferencedAsync(ParameterTable, parameterKey,
                    new[] { (RecipeValuesTable, (IReadOnlyDictionary<string, object>)parameterKey) }, ct, expected);
            }, ct);
        }
        else
        {
            using var connection = SqlProviderGuard.CreateConnection(_connectionString);
            await SqlProviderGuard.OpenConnectionAsync(connection, ct);
            using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await EnsureUniqueIndexAsync(connection, transaction, ParameterTable, UniqueKeyColumns[ParameterTable], ct);
            if (selections.Length > 0)
            {
                await EnsureUniqueIndexAsync(connection, transaction, RecipeValuesTable, RecipeValueKey, ct);
                await EnsureSafeReplacementSqlAsync(connection, transaction, ParameterTable, ct);
                await EnsureSafeReplacementSqlAsync(connection, transaction, RecipeValuesTable, ct);
            }
            await ApplyReplacementsAsync(connection, transaction, RecipeValueKey);
            await ExecuteSqlChangeAsync(connection, transaction, ParameterTable, parameterKey, null, ct);
            if ((await ReadLockedRowsAsync(connection, transaction, ParameterTable, parameterKey, ct)).Count != 0)
                throw new InvalidOperationException("Удаление параметра не подтвердилось. Изменения отменены.");
            await transaction.CommitAsync(ct);
        }

        changes.Add(new ChangeJournalRowChange
        {
            TableName = ParameterTable,
            Kind = ChangeJournalOperationKind.Delete,
            Before = JournalParameterValues(expected),
            IdentityBefore = JournalParameterValues(parameterKey)
        });
        return changes;

        async Task ApplyReplacementsAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> recipeKey)
        {
            Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> ReadRows(string table, IReadOnlyDictionary<string, object> key)
                => connection == null ? Task.FromResult(SnapshotOfflineRows(table, key))
                    : ReadLockedRowsAsync(connection, transaction, table, key, ct);

            var recipeScope = RecipeScopeForParameter(parameterKey);
            int step = Convert.ToInt32(parameterKey["Step_Number"], CultureInfo.InvariantCulture);
            EnsureSnapshotMatches(new[] { expected }, InParameterScope(await ReadRows(ParameterTable, recipeScope), step));
            EnsureSnapshotMatches(selections.Select(item => item.Before).ToArray(),
                InParameterScope(await ReadRows(RecipeValuesTable, recipeScope), step));
            foreach (var (before, candidate) in selections)
            {
                var replacementKey = SelectRecipeParameterKey(candidate, UniqueKeyColumns[ParameterTable]);
                EnsureSnapshotMatches(new[] { candidate }, InParameterScope(await ReadRows(ParameterTable, RecipeScopeForParameter(replacementKey)), step));
                var oldKey = SelectRecipeParameterKey(before, recipeKey);
                var newRecord = candidate["Record"];
                var newKey = new Dictionary<string, object>(oldKey, StringComparer.OrdinalIgnoreCase) { ["Record"] = newRecord };
                if ((await ReadRows(RecipeValuesTable, newKey)).Count != 0)
                    throw new InvalidOperationException($"В рецепте № {before["Recipe_Record"]} уже есть значение параметра № {newRecord}.");

                if (connection == null)
                    await _offline.UpdateRecordAsync(RecipeValuesTable, recipeKey, oldKey,
                        new Dictionary<string, object> { ["Record"] = newRecord }, ct);
                else
                    await ExecuteSqlChangeAsync(connection, transaction, RecipeValuesTable, oldKey, newRecord, ct);

                var after = new Dictionary<string, object>(before, StringComparer.OrdinalIgnoreCase) { ["Record"] = newRecord };
                EnsureSnapshotMatches(new[] { after }, await ReadRows(RecipeValuesTable, newKey));
                changes.Add(new ChangeJournalRowChange
                {
                    TableName = RecipeValuesTable,
                    Kind = ChangeJournalOperationKind.Update,
                    Before = JournalParameterValues(before),
                    After = JournalParameterValues(after),
                    IdentityBefore = JournalParameterValues(oldKey),
                    IdentityAfter = JournalParameterValues(newKey)
                });
            }
            EnsureSnapshotMatches(Array.Empty<IReadOnlyDictionary<string, object>>(), InParameterScope(await ReadRows(RecipeValuesTable, recipeScope), step));
        }
    }

    private static Dictionary<string, object> RecipeScopeForParameter(IReadOnlyDictionary<string, object> scope)
    {
        var result = new Dictionary<string, object>(scope, StringComparer.OrdinalIgnoreCase);
        if (Convert.ToInt32(scope["Step_Number"], CultureInfo.InvariantCulture) > 0) result.Remove("Step_Number");
        return result;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object>> InParameterScope(
        IReadOnlyList<IReadOnlyDictionary<string, object>> rows, int step)
        => step == 0 ? rows : rows.Where(row => Convert.ToInt32(row["Step_Number"], CultureInfo.InvariantCulture) > 0).ToArray();

    private static async Task ExecuteSqlChangeAsync(SqlConnection connection, SqlTransaction transaction,
        string table, IReadOnlyDictionary<string, object> key, object replacement, CancellationToken ct)
    {
        var clause = BuildPredicates(key, "key", out var parameters);
        var sql = replacement == null
            ? $"DELETE FROM {QuoteTable(table)} WITH (UPDLOCK, HOLDLOCK) WHERE {clause}"
            : $"UPDATE {QuoteTable(table)} WITH (UPDLOCK, HOLDLOCK) SET [Record] = @replacement WHERE {clause}";
        using var command = new SqlCommand(sql, connection, transaction);
        AddParameters(command, parameters);
        if (replacement != null) command.Parameters.AddWithValue("@replacement", replacement);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Изменение связанной строки не подтвердилось. Операция отменена.");
    }

    private static Dictionary<string, object> SelectRecipeParameterKey(
        IReadOnlyDictionary<string, object> values, IReadOnlyList<string> columns)
        => columns.ToDictionary(column => column, column => values[column], StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, ChangeJournalValue> JournalParameterValues(IReadOnlyDictionary<string, object> values)
        => values.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase);
}
