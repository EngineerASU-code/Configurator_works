using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Configurator;

public partial class MainWindow
{
    private void ProjectEditor_ChangeJournalRequested(ChangeJournalEntry entry)
    {
        if (entry == null) return;
        if (string.Equals(entry.ProjectType, "Offline", StringComparison.OrdinalIgnoreCase))
        {
            if (!_isOfflineWorkspace || _openedLocalProject == null ||
                !string.Equals(_openedLocalProject.Manifest?.ProjectId, entry.ProjectId, StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine($"[ChangeJournal] Mode 3 event ignored because its Offline project context is no longer active. ProjectId={entry.ProjectId}");
                return;
            }
            _changeJournal.BindOfflineProject(_openedLocalProject, () => _offlineProjectDirty = true, entry.Mode);
        }
        else
        {
            _changeJournal.BindOnlineProject(entry.Server, entry.Database, entry.ProjectName, entry.Mode);
        }
        if (!_changeJournal.Record(entry))
        {
            Configurator.Infrastructure.ModernDialog.Show(this,
                "Изменение выполнено, но запись в журнал не сохранена. Не закрывайте приложение до фиксации проблемы.",
                "Ошибка журнала изменений", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshActionLogUi();
    }

    private void ActionLog_Changed(object sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RefreshActionLogUi);
            return;
        }
        RefreshActionLogUi();
    }

    private void ConfigureJournalContext(string databaseOverride = null)
    {
        if (_isOfflineWorkspace && _openedLocalProject != null)
        {
            _changeJournal.BindOfflineProject(_openedLocalProject, () => _offlineProjectDirty = true, _currentMode);
            return;
        }
        var database = string.IsNullOrWhiteSpace(databaseOverride)
            ? (_activeDbName ?? _selectedDbName ?? "")
            : databaseOverride;
        if (string.IsNullOrWhiteSpace(database)) database = _selectedDbName ?? "";
        _changeJournal.BindOnlineProject(_connection?.Server ?? "", database, database, _currentMode);
    }

    private void RefreshActionLogUi()
    {
        ConfigureJournalContext();
        if (_actionLogWindow != null && _actionLogWindow.IsLoaded)
            _actionLogWindow.RefreshEntries();
        if (LastActionText != null)
            LastActionText.Text = _actionLog.LastDescription;

    }

    private ActionLogWindow _actionLogWindow;

    private void MenuOptionsActionLog_Click(object sender, RoutedEventArgs e)
    {
        ConfigureJournalContext();
        // The journal is modeless: opening it must never disable the main window.
        if (_actionLogWindow == null || !_actionLogWindow.IsLoaded)
        {
            _actionLogWindow = new ActionLogWindow(_changeJournal, RestoreJournalEntryAsync)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _actionLogWindow.Closed += (_, _) => _actionLogWindow = null;
            _actionLogWindow.Show();
        }
        else
        {
            if (_actionLogWindow.WindowState == WindowState.Minimized)
                _actionLogWindow.WindowState = WindowState.Normal;
            _actionLogWindow.Activate();
        }
    }

    private void LastActionBorder_Click(object sender, MouseButtonEventArgs e)
    {
        MenuOptionsActionLog_Click(sender, e);
        e.Handled = true;
    }


    private async Task RestoreJournalEntryAsync(ChangeJournalEntry entry)
    {
        if (!UserAuthorizationService.Require(Permission.RestoreJournal)) return;
        if (entry == null) return;
        if (string.Equals(entry.ProjectType, "Offline", StringComparison.OrdinalIgnoreCase))
        {
            var project = _openedLocalProject;
            if (!_isOfflineWorkspace || project == null)
                throw new InvalidOperationException("Для восстановления необходимо открыть исходный Offline-проект.");
            if (!string.Equals(entry.ProjectId, project.Manifest?.ProjectId, StringComparison.Ordinal))
                throw new InvalidOperationException("Запись журнала относится к другому Offline-проекту. Переключитесь на исходный проект и повторите попытку.");

            // Commit visible grid edits first so conflict checks compare against the real local state.
            CommitOfflineGridToProject();
            var oldRecords = project.LocalRecords.Select(r => new Configurator.Core.Projects.ProjectRecord
            {
                TableName = r.TableName, Key = r.Key, RowVersion = r.RowVersion,
                Values = (System.Text.Json.Nodes.JsonObject)(r.Values?.DeepClone() ?? new System.Text.Json.Nodes.JsonObject())
            }).ToList();
            var oldArchitecture = (System.Text.Json.Nodes.JsonObject)(project.Architecture?.DeepClone() ?? new System.Text.Json.Nodes.JsonObject());
            var wasDirty = _offlineProjectDirty;
            try
            {
                _changeJournal.BindOfflineProject(project, () => _offlineProjectDirty = true, _currentMode);
                _changeJournal.RestoreOffline(entry, project, () => _offlineProjectDirty = true);
                _changeJournal.MarkRestored(entry);
                await SaveOfflineProjectAsync();
                if (_currentMode == 1 && !string.IsNullOrWhiteSpace(_offlineCurrentTableName))
                    OpenOfflineTable(_offlineCurrentTableName);
                if (_currentMode == 3 && _projectEditor != null)
                    await _projectEditor.RefreshAfterOfflineJournalRestoreAsync();
                RefreshActionLogUi();
                return;
            }
            catch
            {
                project.LocalRecords = oldRecords;
                project.Architecture = oldArchitecture;
                _offlineProjectDirty = wasDirty;
                throw;
            }
        }
        if (!entry.CanRestore) throw new InvalidOperationException("Для этой операции восстановление недоступно.");
        if (string.IsNullOrWhiteSpace(entry.Database))
            throw new InvalidOperationException("В журнале не указана база данных операции.");

        var settings = new ConnectionSettings
        {
            Server = string.IsNullOrWhiteSpace(entry.Server) ? _connection.Server : entry.Server,
            User = _connection.User,
            Password = _connection.Password
        };
        await _changeJournal.RestoreAsync(entry, settings.ToConnectionString(entry.Database));
        _changeJournal.MarkRestored(entry);
        if (_activeVm != null) await ReloadCurrentPageFastAsync();
        RefreshActionLogUi();
    }

    private void GlobalButtonClicked(object sender, RoutedEventArgs e)
    {
        // Intentionally empty. The journal records only operations that change
        // project/database objects, never button presses or navigation noise.
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            await UndoLastActionAsync();
        }
    }

    private async Task UndoLastActionAsync()
    {
        if (!UserAuthorizationService.Require(Permission.RestoreJournal)) return;
        try
        {
            var latest = _changeJournal.LoadRecent(500).FirstOrDefault(x => x.CanRestore);
            if (latest != null)
            {
                await RestoreJournalEntryAsync(latest);
                if (_activeVm != null) await ReloadCurrentPageFastAsync();
                RefreshActionLogUi();
                return;
            }

            await _actionLog.UndoAsync();
            if (_activeVm != null) await ReloadCurrentPageFastAsync();
            RefreshActionLogUi();
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show($"Не удалось отменить действие: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", "Отмена действия", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LogAction(
        string description,
        Func<Task> undo = null,
        string category = "Действие",
        string source = "",
        string details = "",
        IReadOnlyList<ChangeJournalRowChange> journalChanges = null,
        ChangeJournalOperationKind? operationKind = null,
        string databaseName = "")
    {
        ConfigureJournalContext(databaseName);
        _actionLog.Record(description, undo, category, source, details);

        if (journalChanges == null || journalChanges.Count == 0)
            return;

        var journalSaved = _changeJournal.Record(new ChangeJournalEntry
        {
            Database = databaseName ?? _activeDbName ?? "",
            Server = _connection?.Server ?? "",
            Category = category ?? "Действие",
            User = UserAuthorizationService.CurrentUser?.DisplayName ?? Environment.UserName,
            UserRole = UserAuthorizationService.CurrentUser?.RoleName ?? "",
            Mode = _currentMode,
            ProjectType = _isOfflineWorkspace ? "Offline" : "Online",
            ProjectId = _isOfflineWorkspace ? (_openedLocalProject?.Manifest.ProjectId ?? "") : "online:" + (_connection?.Server ?? "") + "/" + (databaseName ?? _selectedDbName ?? ""),
            ProjectName = _isOfflineWorkspace ? (_openedLocalProject?.Manifest.Name ?? "") : (databaseName ?? _selectedDbName ?? ""),
            EntityType = "Строка таблицы",
            EntityIdentity = string.Join(", ", journalChanges.SelectMany(change => (change.IdentityAfter?.Count > 0 ? change.IdentityAfter : change.IdentityBefore ?? new Dictionary<string, ChangeJournalValue>()).Select(pair => $"{pair.Key}={pair.Value?.ToObject() ?? "NULL"}"))),
            Result = "Успешно",
            Source = source ?? "",
            Description = description ?? "",
            Details = details ?? "",
            OperationKind = operationKind ?? InferJournalOperation(journalChanges),
            Changes = journalChanges.ToList()
        });
        if (!journalSaved)
        {
            Configurator.Infrastructure.ModernDialog.Show(
                "Данные уже изменены, но запись в журнал изменений не сохранена. Не повторяйте операцию; проверьте доступность папки ChangeJournal и сохраните сведения об операции вручную.",
                "Предупреждение журнала изменений", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static ChangeJournalOperationKind InferJournalOperation(IReadOnlyList<ChangeJournalRowChange> changes)
    {
        if (changes == null || changes.Count == 0) return ChangeJournalOperationKind.Composite;
        var kinds = changes.Select(x => x.Kind).Distinct().ToList();
        return kinds.Count == 1 ? kinds[0] : ChangeJournalOperationKind.Composite;
    }

    private static Dictionary<string, ChangeJournalValue> ToJournalValues(IReadOnlyDictionary<string, object> values)
        => values?.ToDictionary(x => x.Key, x => ChangeJournalValue.FromObject(x.Value), StringComparer.OrdinalIgnoreCase)
           ?? new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase);

    private static ChangeJournalRowChange CreateJournalChange(
        string tableName,
        ChangeJournalOperationKind kind,
        IReadOnlyDictionary<string, object> before,
        IReadOnlyDictionary<string, object> after,
        IReadOnlyDictionary<string, object> identityBefore,
        IReadOnlyDictionary<string, object> identityAfter)
        => new ChangeJournalRowChange
        {
            TableName = tableName ?? "",
            Kind = kind,
            Before = ToJournalValues(before),
            After = ToJournalValues(after),
            IdentityBefore = ToJournalValues(identityBefore),
            IdentityAfter = ToJournalValues(identityAfter)
        };
}
