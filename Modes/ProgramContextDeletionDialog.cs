#nullable enable
using System.Windows;
using System.Windows.Controls;
using Configurator.Infrastructure;

namespace Configurator;

internal static class ProgramContextDeletionDialog
{
    public static bool Confirm(Window owner, ProgramManagementRepository.ContextReplacementPreview preview, out object? replacement)
    {
        replacement = null;
        bool sequence = preview.Table == ProgramManagementRepository.SequenceTable;
        var title = sequence ? "Удаление последовательности" : "Удаление программы";
        var prompt = $"Удалить {(sequence ? "последовательность" : "программу")} № {preview.Source["Record"]}?";
        var dependencies = preview.Dependencies.Where(pair => pair.Value.Count > 0)
            .Select(pair => $"{pair.Key} · {pair.Value.Count}").ToArray();
        if (dependencies.Length == 0)
            return ModernDialog.Show(owner, prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!UserAuthorizationService.Require(Permission.EditData)) return false;
        if (preview.Candidates.Count == 0)
            throw new InvalidOperationException("В текущем контексте нет другой записи для переноса зависимостей.");

        var selection = new ListBox { Margin = new Thickness(0, 8, 0, 12), MinHeight = 72, MaxHeight = 260, SelectedValuePath = "Key", DisplayMemberPath = "Value" };
        System.Windows.Automation.AutomationProperties.SetName(selection, "Запись для переноса зависимостей");
        selection.ItemsSource = preview.Candidates.Select(row =>
        {
            var name = new[] { "Name_L1", "NameL1", "Name_L2", "NameL2" }
                .Select(column => row.TryGetValue(column, out var value) ? Convert.ToString(value) : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return new KeyValuePair<object, string>(row["Record"], name == null ? $"№ {row["Record"]}" : $"№ {row["Record"]} · {name}");
        }).ToArray();

        var confirm = new Button { Content = "Перенести и удалить", IsEnabled = false, IsDefault = true, MinWidth = 145, Margin = new Thickness(6, 0, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = prompt + "\nВыберите, куда перенести связанные записи.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = string.Join("\n", dependencies), Margin = new Thickness(0, 8, 0, 0) });
        content.Children.Add(selection);
        content.Children.Add(buttons);
        var dialog = new Window { Owner = owner, Title = title, Content = content, Width = 440,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        selection.SelectionChanged += (_, _) => confirm.IsEnabled = selection.SelectedItem != null;
        confirm.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return false;
        replacement = selection.SelectedValue;
        return replacement != null && UserAuthorizationService.Require(Permission.EditData);
    }
}

public partial class ProjectModeView
{
    private async Task DeleteProgramFromListAsync(Dictionary<string, object> source, int generation, Core.Projects.ProjectRecord? assembly = null)
    {
        try
        {
            if (IsProgramCardActive && !await ProgramManagementEditor.PrepareToLeaveProgramAsync()) return;
            if (generation != _workspaceGeneration) return;
            var context = new ProgramContext { PlcValue = source["PLC"], ClassNumberValue = source["PLC_Class_Number"], RecordValue = source["Record"] };
            var repository = _offlineSource != null
                ? new ProgramManagementRepository(_offlineSource)
                : new ProgramManagementRepository(_connection.ToConnectionString(_selectedDatabase));
            var preview = await repository.PrepareContextReplacementAsync(context, null, source);
            bool Current() => generation == _workspaceGeneration && IsVisible && (assembly == null ||
                (_workspace is { IsReadOnly: false } && (ObjectList.SelectedItem as ObjectItem)?.AssemblyProgram == assembly));
            if (!Current()) return;
            object? replacement = null;
            bool confirmed = assembly == null ? ProgramContextDeletionDialog.Confirm(Window.GetWindow(this), preview, out replacement)
                : ModernDialog.Show(Window.GetWindow(this), $"Удалить Object «{AssemblyLabel(assembly)}» и записи программы?\n" +
                    string.Join("\n", preview.Dependencies.Where(p => p.Value.Count > 0).Select(p => $"{p.Key} · {p.Value.Count}")) +
                    "\nСтатусы, датчики и справочник шагов сохранятся.", "Удаление Object", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed || !Current() ||
                generation != _workspaceGeneration || !IsVisible || !UserAuthorizationService.Require(Permission.DeleteData)) return;
            var changes = await repository.ReplaceAndDeleteContextAsync(preview, replacement, deleteContents: assembly != null);
            var table = ProgramManagementRepository.ProgramTable;
            var identity = changes[^1].IdentityBefore.ToDictionary(pair => pair.Key, pair => pair.Value.ToObject(), StringComparer.OrdinalIgnoreCase);
            var description = assembly != null ? $"Удалён Object «{AssemblyLabel(assembly)}», записей: {changes.Count}" : replacement == null ? "Удалена программа" : $"Удалена программа, перенесено связанных записей: {changes.Count - 1}";
            RecordMode3Mutation(table, ChangeJournalOperationKind.Delete, description, source, null!, identity, null!, generation,
                relatedChanges: changes.SkipLast(1).ToList(),
                journalOperationKind: changes.Count == 1 ? ChangeJournalOperationKind.Delete : ChangeJournalOperationKind.Composite);
            if (generation != _workspaceGeneration) return;
            if (assembly != null && _configurationKind == ConfigurationKind.Objects) await LoadAssemblyListAsync();
            else await LoadObjectsAsync();
            TxtStatus.Text = description;
            ShowActionFeedback(description);
        }
        catch (Exception ex)
        {
            var message = UserErrorMessage.Format(ex);
            ShowActionFeedback($"Ошибка: {message}", isError: true);
            ModernDialog.Show(Window.GetWindow(this), message, "Удаление программы", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
