using System.Data;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator;

public sealed partial class ProgramManagementRepository
{
    internal async Task<IReadOnlyList<ChangeJournalRowChange>> ChangeParameterTypeAsync(
        IReadOnlyDictionary<string, object> original, IReadOnlyDictionary<string, object> updated,
        CancellationToken ct = default)
    {
        var keys = UniqueKeyColumns[ParameterTable];
        if (!original.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(updated.Keys) ||
            keys.Except(new[] { "Type" }).Any(key => !ValuesEqual(original[key], updated[key])) ||
            ValuesEqual(original["Type"], updated["Type"]))
            throw new InvalidOperationException("При смене типа нужно сохранить исходный контекст параметра.");

        var oldKey = ContextReplacementKey(original, keys);
        var replacement = new Dictionary<string, object>(updated, StringComparer.OrdinalIgnoreCase);
        if (_offline?.Project.Settings.UnifiedLanguage ?? _database.UsesUnifiedLanguage)
            UnifiedLanguage.Mirror(replacement, original.Keys);
        var newKey = ContextReplacementKey(replacement, keys);
        var journal = new List<ChangeJournalRowChange>();
        if (_offline != null)
            await _offline.ExecuteMutationTransactionAsync(() => ApplyAsync(null, null), ct);
        else
        {
            using var connection = SqlProviderGuard.CreateConnection(_connectionString);
            await SqlProviderGuard.OpenConnectionAsync(connection, ct);
            using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await ApplyAsync(connection, transaction);
            await transaction.CommitAsync(ct);
        }
        return journal;

        async Task ApplyAsync(SqlConnection connection, SqlTransaction transaction)
        {
            Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> Read(string table, IReadOnlyDictionary<string, object> key)
                => _offline != null ? Task.FromResult(SnapshotOfflineRows(table, key))
                                    : ReadLockedRowsAsync(connection, transaction, table, key, ct);

            async Task<IReadOnlyList<string>> VerifiedKeys(string table, IReadOnlyList<string> expected)
            {
                var schema = _offline != null ? GetOfflineSchema(table)
                    : await OnlineProjectDataSource.ReadSchemaAsync(connection, transaction, table, ct);
                if (schema == null || !schema.HasStableKey || !schema.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected))
                    throw new InvalidOperationException($"Не подтверждён составной ключ {table}.");
                return schema.KeyColumns;
            }

            int step = Convert.ToInt32(oldKey["Step_Number"]);
            async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> Family(string table, Dictionary<string, object> key)
                => InParameterScope(await Read(table, RecipeScopeForParameter(key)), step);

            var parameterKeys = await VerifiedKeys(ParameterTable, keys);
            EnsureSnapshotMatches(new[] { original }, await Family(ParameterTable, oldKey));
            if ((await Family(ParameterTable, newKey)).Count > 0 || (await Family(RecipeValuesTable, newKey)).Count > 0)
                throw new InvalidOperationException("В этой последовательности уже занят номер параметра для выбранного типа.");

            var recipes = await Family(RecipeValuesTable, oldKey);
            var recipeKeys = recipes.Count > 0 ? await VerifiedKeys(RecipeValuesTable, RecipeValueKey) : RecipeValueKey;
            var changes = replacement.Where(pair => !ExactValueEquals(original[pair.Key], pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            async Task Update(string table, IReadOnlyList<string> identity, Dictionary<string, object> key, Dictionary<string, object> fields)
            {
                if (_offline != null)
                {
                    await _offline.UpdateRecordAsync(table, identity, key, fields, ct);
                    return;
                }
                var where = BuildPredicates(key, "key", out var parameters);
                var assignments = fields.Keys.Select((column, i) => $"{QuoteIdentifier(column)} = @p{i}");
                using var command = new SqlCommand($"UPDATE {QuoteTable(table)} SET {string.Join(", ", assignments)} WHERE {where}", connection, transaction);
                AddParameters(command, parameters);
                int i = 0;
                foreach (var value in fields.Values) command.Parameters.AddWithValue("@p" + i++, value ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(ct);
            }

            await Update(ParameterTable, parameterKeys, oldKey, changes);
            journal.Add(ContextReplacementJournal(ParameterTable, keys, original, replacement));

            var expectedRecipes = new List<IReadOnlyDictionary<string, object>>();
            foreach (var before in recipes)
            {
                var key = ContextReplacementKey(before, recipeKeys);
                var type = Convert.ChangeType(replacement["Type"], before["Type"].GetType(), System.Globalization.CultureInfo.InvariantCulture);
                var after = new Dictionary<string, object>(before) { ["Type"] = type };
                await Update(RecipeValuesTable, recipeKeys, key, new Dictionary<string, object> { ["Type"] = type });
                expectedRecipes.Add(after);
                journal.Add(ContextReplacementJournal(RecipeValuesTable, recipeKeys, before, after));
            }
            EnsureSnapshotMatches(new[] { replacement }, await Family(ParameterTable, newKey));
            EnsureSnapshotMatches(expectedRecipes, await Family(RecipeValuesTable, newKey));
            if ((await Family(RecipeValuesTable, oldKey)).Count + (await Family(ParameterTable, oldKey)).Count != 0)
                throw new InvalidOperationException("В проекте остались ссылки на прежний тип. Изменения отменены.");
        }
    }
}
