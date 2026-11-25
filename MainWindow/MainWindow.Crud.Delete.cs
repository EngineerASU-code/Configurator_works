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
    private bool _tableDeleteInProgress;

    private async void BtnDelete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedTableRowsAsync();

    private async Task DeleteSelectedTableRowsAsync()
    {
        if (_tableDeleteInProgress || _tablePasteInProgress ||
            _currentMode != 1 || !_rightPanel.BtnDelete.IsEnabled ||
            !RequireMode1Permission(Permission.Mode1Delete)) return;

        _tableDeleteInProgress = true;
        try
        {
            if (_isOfflineWorkspace) await DeleteOfflineRowsAsync();
            else await DeleteOnlineRowsAsync();
        }
        finally
        {
            _tableDeleteInProgress = false;
        }
    }

    private async Task DeleteOnlineRowsAsync()
    {
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

            var plans = new List<DeletionPlan>();
            var options = new List<DependencyReplacementWindow.DependencyOption>();
            var deletedRows = snapshots.Select(snapshot => (IReadOnlyDictionary<string, object>)snapshot.Values).ToList();
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
                    Key = identity,
                    ExpectedValues = snapshot.Values
                });

                plans.Add(plan);

                if (plan.ValidationErrors.Count > 0)
                {
                    Configurator.Infrastructure.ModernDialog.Show(
                        "Удаление заблокировано:\n\n" + string.Join("\n", plan.ValidationErrors),
                        "Зависимые данные", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (plan.RequiresReplacement)
                {
                    foreach (var hit in plan.Dependencies)
                    {
                        var candidates = await protection.GetReplacementCandidatesAsync(hit, identity, excludedTargets: deletedRows);
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

                }
            }

            IReadOnlyList<ReferenceReplacement> selected = Array.Empty<ReferenceReplacement>();
            if (options.Count > 0)
            {
                var dialog = new DependencyReplacementWindow(options, requireExistingCandidates: true) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                selected = dialog.Replacements;
            }

            // The dialog returns one replacement per option, in the same order.
            var allReplacements = new List<(DeletionPlan Plan, IReadOnlyList<ReferenceReplacement> Replacements)>();
            var offset = 0;
            foreach (var plan in plans)
            {
                var count = plan.Dependencies.Count;
                allReplacements.Add((plan, selected.Skip(offset).Take(count).ToList()));
                offset += count;
            }

            // Capture the exact dependent values before the transaction changes them.
            // These snapshots become part of the same journal operation, so a composite
            // delete can later be restored as one unit (dependent updates + deleted row).
            var journalReplacementChanges = await CaptureReplacementChangesAsync(connectionString,
                allReplacements.SelectMany(item => item.Replacements).ToList());

            // Every selected row and every dependency replacement shares one SQL transaction.
            // If any update/delete fails, no part of the batch is committed.
            var executionItems = allReplacements
                .Select(item => new DeletionExecutionItem(item.Plan, item.Replacements))
                .ToList();
            await protection.ExecuteBatchAsync(executionItems, (_, _, _) => Task.FromResult(true));
            int deleted = executionItems.Count;

            string deleteDetails = string.Join(" | ", snapshots.Select(x => string.Join(", ", x.Identity.Select(p => $"{p.Key}={FormatLogValue(p.Value)}")))) +
                (allReplacements.Any(x => x.Replacements.Count > 0) ? $" · замен ссылок: {allReplacements.Sum(x => x.Replacements.Count)}" : "");
            var journalDeleteChanges = snapshots.Select(item =>
                CreateJournalChange(deleteTable, ChangeJournalOperationKind.Delete, item.Values, null, item.Identity, null)).ToList();
            journalDeleteChanges.InsertRange(0, journalReplacementChanges);
            var journalKind = allReplacements.Any(x => x.Replacements.Count > 0)
                ? ChangeJournalOperationKind.Composite
                : ChangeJournalOperationKind.Delete;
            LogAction($"Удалено строк: {deleted} · {deleteTable}", category: "Удаление", source: deleteTable, details: deleteDetails,
            journalChanges: journalDeleteChanges, operationKind: journalKind, databaseName: deleteDbName);

            if (_activeVm == null || _activeDbName != deleteDbName || _activeVm.CurrentTable != deleteTable) return;
            _fieldsBuilder.Clear();
            _activeVm.ClearCurrentRow();
            SafeSetButtonEnabled(_rightPanel.BtnUpdate, false);
            SafeSetButtonEnabled(_rightPanel.BtnDuplicate, false);
            SafeSetButtonEnabled(_rightPanel.BtnDelete, false);

            if (_activeVm.CurrentPage > _activeVm.TotalPages)
                _activeVm.CurrentPage = _activeVm.TotalPages;

            int preferredIndex = Math.Min(selectedIndex, Math.Max(0, _activeVm.PageSize - 1));
            await ReloadCurrentPageFastAsync(preferredIndex, null);
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show("Ошибка удаления: " + Configurator.Infrastructure.UserErrorMessage.Format(ex), "Удаление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static async Task<List<ChangeJournalRowChange>> CaptureReplacementChangesAsync(
        string connectionString, IReadOnlyList<ReferenceReplacement> replacements)
    {
        var changes = new List<ChangeJournalRowChange>();
        foreach (var replacement in replacements ?? Array.Empty<ReferenceReplacement>())
        {
            var beforeRow = await LoadRowByIdentityAsync(connectionString, replacement.SourceTable, replacement.KeyColumns, replacement.KeyValues);
            if (beforeRow.Count == 0)
                throw new InvalidOperationException($"Не удалось сохранить состояние зависимой строки {replacement.SourceTable} перед изменением.");

            var changedColumns = replacement.SourceColumns.Concat(replacement.AdditionalAssignments.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var before = changedColumns.ToDictionary(column => column,
                column => beforeRow.TryGetValue(column, out var value) ? value : null, StringComparer.OrdinalIgnoreCase);
            var after = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < replacement.SourceColumns.Count; i++)
                after[replacement.SourceColumns[i]] = replacement.NewValues[i];
            foreach (var extra in replacement.AdditionalAssignments) after[extra.Key] = extra.Value;

            var identityBefore = replacement.KeyColumns
                .Select((column, index) => new { column, index })
                .ToDictionary(item => item.column, item => replacement.KeyValues[item.index], StringComparer.OrdinalIgnoreCase);
            var identityAfter = new Dictionary<string, object>(identityBefore, StringComparer.OrdinalIgnoreCase);
            foreach (var changed in after)
                if (identityAfter.ContainsKey(changed.Key)) identityAfter[changed.Key] = changed.Value;

            changes.Add(CreateJournalChange(replacement.SourceTable, ChangeJournalOperationKind.Update,
                before, after, identityBefore, identityAfter));
        }
        return changes;
    }

    internal static async Task<Dictionary<string, object>> LoadRowByIdentityAsync(string connectionString, string tableName, IReadOnlyList<string> keyColumns, IReadOnlyList<object> keyValues)
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
        command.CommandText = $"SELECT TOP (2) * FROM {QuoteTableName(tableName)} WHERE {string.Join(" AND ", where)}";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) result[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        if (await reader.ReadAsync())
            throw new InvalidOperationException($"Ключ не уникален для созданной строки {tableName}; снимок не сохранён.");
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
