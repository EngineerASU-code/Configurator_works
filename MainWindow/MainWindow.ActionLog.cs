using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using Configurator.Infrastructure.Projects;
using Configurator.Core.Projects;
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
        ShowLastAction(entry.DisplayDescription);
        if (!_changeJournal.Record(entry))
        {
            Configurator.Infrastructure.ModernDialog.Show(this,
                "Изменение выполнено, но запись в журнал не сохранена. Не закрывайте приложение до фиксации проблемы.",
                "Ошибка журнала изменений", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        if (_actionLogWindow?.IsLoaded != true) return;
        ConfigureJournalContext();
        _actionLogWindow.RefreshEntries();
    }

    private void ShowLastAction(string description)
    {
        LastActionText.Text = description;
        LastActionText.ToolTip = description;
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
                ReadEntries = JournalTimeline,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _actionLogWindow.Closed += (_, _) => _actionLogWindow = null;
            _actionLogWindow.RefreshEntries();
            _actionLogWindow.Show();
        }
        else
        {
            if (_actionLogWindow.WindowState == WindowState.Minimized)
                _actionLogWindow.WindowState = WindowState.Normal;
            _actionLogWindow.Activate();
        }
    }

    private readonly Dictionary<string, (ChangeJournalEntry Entry, string Path)> _compareActions = new();

    private IEnumerable<ChangeJournalEntry> JournalActions(int limit)
        => _changeJournal.LoadRecent(limit).Concat(_compareActions.Values.Select(action => action.Entry))
            .DistinctBy(entry => entry.OperationId).OrderByDescending(entry => entry.TimestampUtc);

    private IEnumerable<ChangeJournalEntry> JournalTimeline()
    {
        var actions = JournalActions(int.MaxValue).ToList();
        var rollbacks = actions.Where(entry => entry.RestoredUtc.HasValue).Select(entry => new ChangeJournalEntry
        {
            OperationId = entry.OperationId + "-undo", TimestampUtc = entry.RestoredUtc.Value,
            User = entry.RestoredBy, ProjectName = entry.ProjectName, ProjectType = entry.ProjectType,
            Database = entry.Database, Mode = entry.Mode, Category = "Откат", Description = "Откат · " + entry.DisplayDescription,
            RestoreIncludesAllChanges = false, EntityIdentity = entry.EntityIdentity, OperationKind = entry.OperationKind
        });
        return actions.Concat(rollbacks).OrderByDescending(entry => entry.TimestampUtc).Take(500);
    }

    private async Task RestoreExternalCompareAsync(ChangeJournalEntry entry, string path)
    {
        var journal = new ChangeJournalService();
        if (path != null)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 81920, true);
            var (project, _) = await ProjectPackageService.ReadForCompareAsync(file, CancellationToken.None);
            journal.BindOfflineProject(project, () => { }, entry.Mode);
            var stored = journal.LoadRecent(int.MaxValue).FirstOrDefault(item => item.OperationId == entry.OperationId);
            if (stored?.CanRestore != true || stored.ProjectId != project.Manifest.ProjectId)
                throw new InvalidOperationException("Исходная операция недоступна для отмены в этом файле.");
            if (stored.RestoreKind == CompareProjectSettingsRestore.RestoreKind)
                CompareProjectSettingsRestore.RestoreOffline(stored, project);
            else
                journal.RestoreOffline(stored, project, () => { });
            journal.MarkRestored(stored);
            await new ProjectPackageService().SaveAsync(path, project, CancellationToken.None, requireExisting: true, reservedFile: file);
            entry.RestoredUtc = stored.RestoredUtc;
            entry.RestoredBy = stored.RestoredBy;
        }
        else
        {
            if (_connection == null || !string.Equals(ConnectionSettings.ProjectServerName(entry.Server),
                ConnectionSettings.ProjectServerName(_connection.Server), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Для отмены подключитесь к серверу целевого проекта.");
            journal.BindOnlineProject(entry.Server, entry.Database, entry.ProjectName, entry.Mode);
            if (entry.RestoreKind == CompareProjectSettingsRestore.RestoreKind)
                await CompareProjectSettingsRestore.RestoreOnlineAsync(entry);
            else
                await journal.RestoreAsync(entry, _connection.ToConnectionString(entry.Database));
            journal.MarkRestored(entry);
        }
        ShowLastAction("Отменено · " + entry.DisplayDescription);
        RefreshActionLogUi();
    }

    private bool _restoringJournalEntry;

    private async Task RestoreJournalEntryAsync(ChangeJournalEntry entry)
    {
        if (_restoringJournalEntry) return;
        _restoringJournalEntry = true;
        try
        {
            if (!UserAuthorizationService.Require(Permission.RestoreJournal) || !entry.CanRestore) return;
            if (_compareActions.TryGetValue(entry.OperationId, out var action) &&
                (action.Path != null ? !_isOfflineWorkspace || !string.Equals(action.Path, _openedLocalProjectPath, StringComparison.OrdinalIgnoreCase)
                : _isOfflineWorkspace || !string.Equals(entry.Database, _activeDbName ?? _selectedDbName, StringComparison.OrdinalIgnoreCase)))
                await RestoreExternalCompareAsync(entry, action.Path);
            else
                await RestoreJournalEntryCoreAsync(entry);
            if (entry.RestoredUtc.HasValue)
            {
                if (_compareActions.ContainsKey(entry.OperationId)) _compareActions[entry.OperationId] = (entry, action.Path);
                _compareWindow?.RefreshAfterUndo();
            }
        }
        finally { _restoringJournalEntry = false; }
    }

    private async Task RestoreJournalEntryCoreAsync(ChangeJournalEntry entry)
    {
        if (!UserAuthorizationService.Require(Permission.RestoreJournal)) return;
        if (entry == null) return;
        if (entry.RestoreKind == Configurator.Infrastructure.Projects.CompareProjectSettingsRestore.RestoreKind)
        {
            await RestoreCompareProjectSettingsAsync(entry);
            return;
        }
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
            }
            catch
            {
                project.LocalRecords = oldRecords;
                project.Architecture = oldArchitecture;
                _offlineProjectDirty = wasDirty;
                throw;
            }
            ShowLastAction("Отменено · " + entry.DisplayDescription);
            if (_currentMode == 1 && !string.IsNullOrWhiteSpace(_offlineCurrentTableName))
                OpenOfflineTable(_offlineCurrentTableName);
            if (_currentMode == 3 && _projectEditor != null)
                await _projectEditor.RefreshAfterOfflineJournalRestoreAsync();
            RefreshActionLogUi();
            return;
        }
        if (!entry.CanRestore) throw new InvalidOperationException("Для этой операции восстановление недоступно.");
        if (string.IsNullOrWhiteSpace(entry.Database))
            throw new InvalidOperationException("В журнале не указана база данных операции.");

        var currentDatabase = string.IsNullOrWhiteSpace(_activeDbName) ? _selectedDbName : _activeDbName;
        if (_isOfflineWorkspace || !string.Equals(entry.Database, currentDatabase, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ConnectionSettings.ProjectServerName(entry.Server), ConnectionSettings.ProjectServerName(_connection.Server),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для отмены откройте исходную базу проекта.");

        await _changeJournal.RestoreAsync(entry, _connection.ToConnectionString(entry.Database));
        _changeJournal.BindOnlineProject(_connection.Server, entry.Database, entry.ProjectName, entry.Mode);
        _changeJournal.MarkRestored(entry);
        ShowLastAction("Отменено · " + entry.DisplayDescription);
        if (_activeVm != null) await ReloadCurrentPageFastAsync();
        RefreshActionLogUi();
    }

    private async Task RestoreCompareProjectSettingsAsync(ChangeJournalEntry entry)
    {
        if (!entry.CanRestore) throw new InvalidOperationException("Для этой операции восстановление недоступно.");
        if (string.Equals(entry.ProjectType, "Offline", StringComparison.OrdinalIgnoreCase))
        {
            var project = _openedLocalProject;
            if (!_isOfflineWorkspace || project == null)
                throw new InvalidOperationException("Для восстановления необходимо открыть исходный Offline-проект.");
            if (!string.Equals(entry.ProjectId, project.Manifest?.ProjectId, StringComparison.Ordinal))
                throw new InvalidOperationException("Запись Compare относится к другому Offline-проекту. Откройте исходный проект и повторите попытку.");

            var oldSettings = Configurator.Infrastructure.Projects.CompareProjectSettingsRestore.CloneSettings(
                new Configurator.Core.Compare.ProjectCompareSettings(project.TextLists, project.ThemePresets));
            var oldArchitecture = (System.Text.Json.Nodes.JsonObject)(project.Architecture?.DeepClone() ?? new System.Text.Json.Nodes.JsonObject());
            var wasDirty = _offlineProjectDirty;
            try
            {
                Configurator.Infrastructure.Projects.CompareProjectSettingsRestore.RestoreOffline(entry, project);
                _changeJournal.BindOfflineProject(project, () => _offlineProjectDirty = true, entry.Mode);
                _changeJournal.MarkRestored(entry);
                await SaveOfflineProjectAsync();
            }
            catch
            {
                project.TextLists = oldSettings.TextLists.ToList();
                project.ThemePresets = oldSettings.ThemePresets.Select(item => item.Clone()).ToList();
                project.Architecture = oldArchitecture;
                _offlineProjectDirty = wasDirty;
                throw;
            }
        }
        else
        {
            await Configurator.Infrastructure.Projects.CompareProjectSettingsRestore.RestoreOnlineAsync(entry);
            _changeJournal.MarkRestored(entry);
        }

        ShowLastAction("Отменено · " + entry.DisplayDescription);
        RefreshActionLogUi();
    }

    private void GlobalButtonClicked(object sender, RoutedEventArgs e)
    {
        // Intentionally empty. The journal records only operations that change
        // project/database objects, never button presses or navigation noise.
    }

    private void ProjectUndoInput(object sender, PreProcessInputEventArgs input)
    {
        if (input.StagingItem.Input is not KeyEventArgs key || key.RoutedEvent != Keyboard.PreviewKeyDownEvent ||
            key.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control || !_workspaceModeChosen ||
            UserAuthorizationService.CurrentUser == null || !IsVisible) return;
        key.Handled = true;
        if (key.IsRepeat || _undoShortcutPending || _restoringJournalEntry || _compareWindow?.IsApplyingTransfer == true) return;
        _undoShortcutPending = true;
        Dispatcher.InvokeAsync(async () =>
        {
            try { await UndoLastActionAsync(); }
            finally { _undoShortcutPending = false; }
        });
    }

    private bool _undoShortcutPending;

    private async Task UndoLastActionAsync()
    {
        if (!UserAuthorizationService.Require(Permission.RestoreJournal)) return;
        try
        {
            ConfigureJournalContext();
            var latest = JournalActions(int.MaxValue).FirstOrDefault(entry => !entry.IsRestored);
            if (latest != null)
            {
                if (!latest.CanRestore)
                    throw new InvalidOperationException("Последнее изменение проекта нельзя безопасно отменить. Более ранние изменения не отменены.");
                await RestoreJournalEntryAsync(latest);
                return;
            }
            Configurator.Infrastructure.ModernDialog.Show(this,
                "В журнале текущего проекта нет сохранённых изменений для отмены.",
                "Отмена действия", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show($"Не удалось отменить действие: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", "Отмена действия", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LogAction(
        string description,
        string category = "Действие",
        string source = "",
        string details = "",
        IReadOnlyList<ChangeJournalRowChange> journalChanges = null,
        ChangeJournalOperationKind? operationKind = null,
        string databaseName = "", bool offline = false)
    {
        if (offline) ConfigureJournalContext(databaseName);
        else _changeJournal.BindOnlineProject(_connection.Server, databaseName, databaseName, _currentMode);

        if (journalChanges == null || journalChanges.Count == 0)
            return;

        var entry = new ChangeJournalEntry
        {
            Database = databaseName ?? _activeDbName ?? "",
            Server = _connection?.Server ?? "",
            Category = category ?? "Действие",
            User = UserAuthorizationService.CurrentUser?.DisplayName ?? Environment.UserName,
            UserRole = UserAuthorizationService.CurrentUser?.RoleName ?? "",
            Mode = _currentMode,
            ProjectType = offline ? "Offline" : "Online",
            ProjectId = offline ? (_openedLocalProject?.Manifest.ProjectId ?? "") : "online:" + (_connection?.Server ?? "") + "/" + (databaseName ?? _selectedDbName ?? ""),
            ProjectName = offline ? (_openedLocalProject?.Manifest.Name ?? "") : (databaseName ?? _selectedDbName ?? ""),
            EntityType = "Строка таблицы",
            EntityIdentity = string.Join(", ", journalChanges.SelectMany(change => (change.IdentityAfter?.Count > 0 ? change.IdentityAfter : change.IdentityBefore ?? new Dictionary<string, ChangeJournalValue>()).Select(pair => $"{pair.Key}={pair.Value?.ToObject() ?? "NULL"}"))),
            Result = "Успешно",
            Source = source ?? "",
            Description = description ?? "",
            Details = string.Join(" ", new[] { details }.Concat(journalChanges.Select(change => change.CaptureWarning).Distinct())
                .Where(text => !string.IsNullOrWhiteSpace(text))),
            OperationKind = operationKind ?? InferJournalOperation(journalChanges),
            Changes = journalChanges.ToList()
        };
        ShowLastAction(entry.DisplayDescription);
        if (!_changeJournal.Record(entry))
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
        IReadOnlyDictionary<string, object> identityAfter, bool completeOfflineRow = false)
        => new ChangeJournalRowChange
        {
            TableName = tableName ?? "",
            Kind = kind,
            Before = ToJournalValues(before),
            After = ToJournalValues(after),
            AfterIsCompleteOfflineRecord = completeOfflineRow,
            IdentityBefore = ToJournalValues(identityBefore),
            IdentityAfter = ToJournalValues(identityAfter)
        };
}
