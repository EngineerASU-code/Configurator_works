using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Data.SqlClient;
using Configurator.Core.Architecture;

namespace Configurator;

public partial class MainWindow
{
    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode1Permission(Permission.Mode1Delete)) return;
        if (_isOfflineWorkspace) { DeleteOfflineRows(); return; }
        if (_activeVm == null || !_activeVm.HasValidKey) return;

        int selectedCount = _rightPanel.DataTableGrid.SelectedItems.Count;
        if (selectedCount == 0) return;

        int selectedIndex = Math.Max(0, _rightPanel.DataTableGrid.SelectedIndex);
        string deleteTable = _activeVm.CurrentTable;
        string deleteDbName = _activeDbName;

        var rows = _rightPanel.DataTableGrid.SelectedItems.OfType<DataRowView>().ToList();
        var snapshots = rows.Select(row => (
            Values: row.DataView.Table.Columns.Cast<DataColumn>().ToDictionary(c => c.ColumnName, c => row[c.ColumnName]),
            Identity: _activeVm.BuildIdentityValues(row))).ToList();

        if (snapshots.Any(x => x.Identity.Count == 0))
        {
            Configurator.Infrastructure.ModernDialog.Show("Не удалось определить идентификатор одной из выбранных строк.", "Удаление не выполнено", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string confirmMsg = selectedCount == 1 ? "Удалить выбранную строку?" : $"Удалить {selectedCount} строк?";
        if (Configurator.Infrastructure.ModernDialog.Show(confirmMsg, "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var connectionString = _connection.ToConnectionString(deleteDbName);
            var analyzer = new DatabaseArchitectureAnalyzer(connectionString);
            var schema = await analyzer.AnalyzeAsync();
            var protection = new DeletionProtectionService(connectionString, schema);

            var plans = new List<(DeletionPlan Plan, Dictionary<string, object> Snapshot, Dictionary<string, object> Identity)>();
            var allReplacements = new List<(DeletionPlan Plan, IReadOnlyList<ReferenceReplacement> Replacements)>();

            // Сначала строим все планы и просим пользователя выбрать замены.
            // Пока ни одна строка не удаляется: Cancel в любом диалоге отменяет
            // всю пакетную операцию, а не оставляет частично удалённые данные.
            for (int rowIndex = 0; rowIndex < snapshots.Count; rowIndex++)
            {
                var snapshot = snapshots[rowIndex];
                var identity = new Dictionary<string, object>(snapshot.Identity, StringComparer.OrdinalIgnoreCase);
                foreach (var relation in schema.Relations.Where(r => r.TargetTable.Equals(deleteTable, StringComparison.OrdinalIgnoreCase)))
                    foreach (var column in relation.TargetColumns)
                        if (rows[rowIndex].DataView.Table.Columns.Contains(column) && !identity.ContainsKey(column))
                            identity[column] = rows[rowIndex][column];

                var plan = await protection.BuildPlanAsync(new DeletionRequest
                {
                    TableName = deleteTable,
                    Key = identity
                });

                plans.Add((plan, snapshot.Values, identity));

                if (plan.ValidationErrors.Count > 0)
                {
                    Configurator.Infrastructure.ModernDialog.Show(
                        "Удаление заблокировано:\n\n" + string.Join("\n", plan.ValidationErrors),
                        "Зависимые данные", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                IReadOnlyList<ReferenceReplacement> replacements = Array.Empty<ReferenceReplacement>();
                if (plan.RequiresReplacement)
                {
                    var options = new List<DependencyReplacementWindow.DependencyOption>();
                    foreach (var hit in plan.Dependencies)
                    {
                        var candidates = await protection.GetReplacementCandidatesAsync(hit, identity);
                        if (candidates.Count == 0)
                            throw new InvalidOperationException($"Для зависимости {hit.SourceTable}.{hit.SourceColumnDescription} нет допустимого значения для замены.");

                        var currentColumn = hit.Relation.TargetColumns.FirstOrDefault(c => identity.ContainsKey(c));
                        options.Add(new DependencyReplacementWindow.DependencyOption
                        {
                            Hit = hit,
                            CurrentValue = currentColumn == null ? "" : identity[currentColumn],
                            Candidates = candidates
                        });
                    }

                    var dialog = new DependencyReplacementWindow(options) { Owner = this };
                    if (dialog.ShowDialog() != true) return;
                    replacements = dialog.Replacements;
                }

                allReplacements.Add((plan, replacements));
            }

            // Capture the exact dependent values before the transaction changes them.
            // These snapshots become part of the same journal operation, so a composite
            // delete can later be restored as one unit (dependent updates + deleted row).
            var journalReplacementChanges = new List<ChangeJournalRowChange>();
            foreach (var item in allReplacements)
            {
                foreach (var replacement in item.Replacements)
                {
                    var beforeRow = await LoadRowByIdentityAsync(connectionString, replacement.SourceTable, replacement.KeyColumns, replacement.KeyValues);
                    if (beforeRow.Count == 0)
                        throw new InvalidOperationException($"Не удалось сохранить состояние зависимой строки {replacement.SourceTable} перед удалением.");

                    var changedColumns = replacement.SourceColumns.Concat(replacement.AdditionalAssignments.Keys)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var before = changedColumns.ToDictionary(c => c, c => beforeRow.TryGetValue(c, out var v) ? v : null, StringComparer.OrdinalIgnoreCase);
                    var after = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < replacement.SourceColumns.Count; i++)
                        after[replacement.SourceColumns[i]] = replacement.NewValues[i];
                    foreach (var extra in replacement.AdditionalAssignments) after[extra.Key] = extra.Value;

                    var identityBefore = replacement.KeyColumns
                        .Select((c, i) => new { c, i })
                        .ToDictionary(x => x.c, x => replacement.KeyValues[x.i], StringComparer.OrdinalIgnoreCase);
                    var identityAfter = new Dictionary<string, object>(identityBefore, StringComparer.OrdinalIgnoreCase);
                    foreach (var changed in after)
                        if (identityAfter.ContainsKey(changed.Key)) identityAfter[changed.Key] = changed.Value;

                    journalReplacementChanges.Add(CreateJournalChange(
                        replacement.SourceTable, ChangeJournalOperationKind.Update, before, after, identityBefore, identityAfter));
                }
            }

            // Every selected row and every dependency replacement shares one SQL transaction.
            // If any update/delete fails, no part of the batch is committed.
            var executionItems = allReplacements
                .Select(item => new DeletionExecutionItem(item.Plan, item.Replacements))
                .ToList();
            await protection.ExecuteBatchAsync(executionItems, (_, _, _) => Task.FromResult(true));
            int deleted = executionItems.Count;

            _fieldsBuilder.Clear();
            _activeVm.ClearCurrentRow();
            SafeSetButtonEnabled(_rightPanel.BtnUpdate, false);
            SafeSetButtonEnabled(_rightPanel.BtnDuplicate, false);
            SafeSetButtonEnabled(_rightPanel.BtnDelete, false);

            string deleteDetails = string.Join(" | ", snapshots.Select(x => string.Join(", ", x.Identity.Select(p => $"{p.Key}={FormatLogValue(p.Value)}")))) +
                (allReplacements.Any(x => x.Replacements.Count > 0) ? $" · замен ссылок: {allReplacements.Sum(x => x.Replacements.Count)}" : "");
            var journalDeleteChanges = snapshots.Select(item =>
                CreateJournalChange(deleteTable, ChangeJournalOperationKind.Delete, item.Values, null, item.Identity, null)).ToList();
            journalDeleteChanges.InsertRange(0, journalReplacementChanges);
            var journalKind = allReplacements.Any(x => x.Replacements.Count > 0)
                ? ChangeJournalOperationKind.Composite
                : ChangeJournalOperationKind.Delete;
            LogAction($"Удалено строк: {deleted} · {deleteTable}", async () =>
            {
                var undoDb = new DatabaseService(_connection.ToConnectionString(deleteDbName));
                var meta = await undoDb.GetTableColumnsAsync(deleteTable);
                foreach (var item in snapshots)
                {
                    var values = item.Values.Where(p =>
                    {
                        var m = meta.FirstOrDefault(x => x.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase));
                        return m == null || !m.IsReadOnly;
                    }).ToDictionary(x => x.Key, x => x.Value);
                    if (!await undoDb.RowExistsByValuesAsync(deleteTable, item.Identity))
                        await undoDb.InsertRowAsync(deleteTable, values);
                }
            }, category: "Удаление", source: deleteTable, details: deleteDetails,
            journalChanges: journalDeleteChanges, operationKind: journalKind, databaseName: deleteDbName);

            if (_activeVm.CurrentPage > _activeVm.TotalPages)
                _activeVm.CurrentPage = _activeVm.TotalPages;

            int preferredIndex = Math.Min(selectedIndex, Math.Max(0, _activeVm.PageSize - 1));
            await ReloadCurrentPageFastAsync(preferredIndex, null);
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show("Ошибка удаления: " + ex.Message, "Удаление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task<Dictionary<string, object>> LoadRowByIdentityAsync(string connectionString, string tableName, IReadOnlyList<string> keyColumns, IReadOnlyList<object> keyValues)
    {
        if (keyColumns == null || keyValues == null || keyColumns.Count == 0 || keyColumns.Count != keyValues.Count)
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
        var where = new List<string>();
        await using var command = new SqlCommand { Connection = connection };
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var value = keyValues[i];
            if (value == null || value == DBNull.Value) where.Add($"[{keyColumns[i].Replace("]", "]]" )}] IS NULL");
            else
            {
                var p = "@k" + i;
                where.Add($"[{keyColumns[i].Replace("]", "]]" )}] = {p}");
                command.Parameters.AddWithValue(p, value);
            }
        }
        command.CommandText = $"SELECT TOP (1) * FROM {QuoteTableName(tableName)} WHERE {string.Join(" AND ", where)}";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) result[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return result;
    }

    private static string QuoteTableName(string tableName)
    {
        var parts = (tableName ?? "").Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];
        return $"[{schema.Replace("]", "]]" )}].[{table.Replace("]", "]]" )}]";
    }
}
