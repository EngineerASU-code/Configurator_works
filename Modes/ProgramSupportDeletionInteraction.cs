#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using System.Windows;
using Configurator.Infrastructure;

namespace Configurator;

internal static class ProgramSupportDeletionInteraction
{
    internal static async Task<bool> StageAsync(Window? owner, ProgramSupportRepository repository,
        ProgramSupportTableData tableData, DataRow row, IReadOnlyDictionary<string, object> scope, Func<bool> isCurrent)
    {
        var added = row.RowState == DataRowState.Added;
        var permission = added ? Permission.AddData : Permission.DeleteData;
        if (!UserAuthorizationService.Require(permission)) return false;

        var plan = added ? null : await repository.PrepareDeletionReplacementAsync(tableData, row, scope);
        if (!isCurrent()) return false;
        IReadOnlyList<ReferenceReplacement> replacements = Array.Empty<ReferenceReplacement>();
        if (plan?.Options.Count > 0)
        {
            if (!UserAuthorizationService.Require(Permission.EditData)) return false;
            var dialog = new DependencyReplacementWindow(plan.Options, requireExistingCandidates: true, stageDeletion: true)
            {
                Owner = owner
            };
            if (dialog.ShowDialog() != true) return false;
            replacements = dialog.Replacements;
        }
        else if (ModernDialog.Show(owner,
            added ? "Удалить новую строку?" : "Пометить выбранную строку для удаления? Изменение вступит в силу после сохранения.",
            "Удаление строки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return false;
        }

        if (!isCurrent() || !UserAuthorizationService.Require(permission) ||
            (replacements.Count > 0 && !UserAuthorizationService.Require(Permission.EditData))) return false;

        if (added) row.Table.Rows.Remove(row);
        else
        {
            if (plan != null) repository.StageDeletionReplacement(tableData, row, plan, replacements);
            row.Delete();
        }
        return true;
    }
}
