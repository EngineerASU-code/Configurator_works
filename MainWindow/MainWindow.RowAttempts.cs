using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Configurator;

public partial class MainWindow
{
    private string _attemptFolder;
    private static string AttemptUser => UserAuthorizationService.CurrentUser?.UserName ?? Environment.UserName;

    private void RefreshRowAttempts(string database)
    {
        _attemptFolder = string.IsNullOrEmpty(database) ? null : LocalRowAttempt.Folder(_connection.Server, database, AttemptUser);
        try
        {
            RowAttemptsButton.Visibility = _attemptFolder != null && LocalRowAttempt.List(_attemptFolder).Length > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (IOException) { RowAttemptsButton.Visibility = Visibility.Visible; }
        catch (UnauthorizedAccessException) { RowAttemptsButton.Visibility = Visibility.Visible; }
    }

    private async void RowAttempts_Click(object sender, RoutedEventArgs args)
    {
        if (_attemptFolder == null || !RequireMode1Permission(Permission.Mode1Edit)) return;
        var folder = _attemptFolder;
        try
        {
            foreach (var path in LocalRowAttempt.List(_attemptFolder))
            {
                using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                if (!File.Exists(path)) continue;
                var entry = LocalRowAttempt.Read(path);
                var change = entry.Changes.Single();
                var db = new DatabaseService(_connection.ToConnectionString(entry.Database));
                Dictionary<string, object> current = null;
                var requested = change.After.Where(p => change.Before.TryGetValue(p.Key, out var old) &&
                    (p.Value.Json != old.Json || p.Value.IsDbNull != old.IsDbNull)).ToDictionary(p => p.Key, p => p.Value.ToObject());
                string preview;
                try
                {
                    current = await db.ReadAttemptRowAsync(change);
                    preview = string.Join("\n", requested.Select(p => $"{p.Key}: {FormatLogValue(current[p.Key])} → {FormatLogValue(p.Value)}"));
                }
                catch (Exception ex)
                {
                    current = null;
                    preview = "Не удалось сверить строку. " + Infrastructure.UserErrorMessage.Format(ex);
                }
                if (folder != _attemptFolder || _isOfflineWorkspace) break;
                bool hasChanges = current != null && requested.Any(p =>
                    !System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(current[p.Key], p.Value));
                var dialog = CreateAttemptWindow(entry, preview, hasChanges);
                dialog.Owner = this;
                if (dialog.ShowDialog() != true) break;
                if (dialog.Tag is true)
                {
                    if (!hasChanges || folder != _attemptFolder || !RequireMode1Permission(Permission.Mode1Edit)) break;
                    await ApplyRowAttemptAsync(db, folder, path, entry, current, requested);
                    _changeJournal.BindOnlineProject(entry.Server, entry.Database, entry.Database, entry.Mode);
                    entry.Result = "Успешно";
                    if (!SaveJournalEntry(entry)) break;
                    ShowLastAction(entry.DisplayDescription);
                }
                File.Delete(path);
            }
        }
        catch (Exception ex) { Infrastructure.ModernDialog.Show(this, Infrastructure.UserErrorMessage.Format(ex), "Попытки сохранения", MessageBoxButton.OK, MessageBoxImage.Warning); }
        UpdateCurrentProjectText();
    }

    internal static async Task ApplyRowAttemptAsync(DatabaseService db, string folder, string path, ChangeJournalEntry entry,
        Dictionary<string, object> current, Dictionary<string, object> requested)
    {
        var change = entry.Changes.Single();
        var after = new Dictionary<string, object>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in requested) after[pair.Key] = pair.Value;
        var keys = change.IdentityBefore.Keys.ToList();
        var identity = keys.ToDictionary(k => k, k => current[k]);
        entry.Changes[0] = CreateJournalChange(change.TableName, ChangeJournalOperationKind.Update,
            current, after, identity, keys.ToDictionary(k => k, k => after[k]));
        entry.Result = LocalRowAttempt.Unknown;
        LocalRowAttempt.Save(folder, entry, replace: true);
        try { await db.UpdateRowAsync(change.TableName, keys, identity, requested, expectedValues: current); }
        catch (Exception ex)
        {
            if (ex.Data.Contains("RowUpdateRolledBack")) LocalRowAttempt.NoteRollback(path, entry);
            throw;
        }
    }

    internal static Window CreateAttemptWindow(ChangeJournalEntry entry, string preview, bool canApply)
    {
        var before = entry.Changes.FirstOrDefault()?.Before;
        var labels = new List<string>();
        foreach (var field in new[] { "PLC", "Record", "Name" })
        {
            var item = before?.FirstOrDefault(p => p.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (item?.Value != null) labels.Add($"{field}: {item.Value.Value.ToObject()}");
        }
        var identity = labels.Count == 0 ? entry.EntityIdentity : string.Join("    ·    ", labels);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        var keep = new Button { Content = "Оставить на проверку", IsCancel = true, Padding = new Thickness(12,5,12,5) };
        var resolve = new Button { Content = "Оставить на сервере", Margin = new Thickness(8,0,0,0), Padding = keep.Padding };
        var journal = new Button { Content = "Применить изменение", IsEnabled = canApply, Margin = resolve.Margin, Padding = keep.Padding };
        buttons.Children.Add(keep);
        buttons.Children.Add(journal);
        buttons.Children.Add(resolve);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = $"{identity}\n{entry.TimestampUtc.ToLocalTime():g}\n\nЗапрошенное изменение\n{entry.Details}\n\n{entry.Result}\n\nНа сервере → после применения\n{preview}" });
        var window = new Window { Title = "Попытка сохранения строки", Width = 620, Height = 340, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        journal.Click += (_, _) =>
        {
            window.Tag = true;
            window.DialogResult = true;
        };
        resolve.Click += (_, _) =>
        {
            if (Infrastructure.ModernDialog.Show(window, "Удалить локальную копию этой попытки? Данные на сервере останутся прежними.",
                "Подтверждение разбора", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) window.DialogResult = true;
        };
        return window;
    }
}
