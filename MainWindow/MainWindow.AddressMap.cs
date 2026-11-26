#nullable enable
using System.Windows;
using System.IO;
using Configurator.Core.Workspace;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;

namespace Configurator;

public partial class MainWindow
{
    private void MenuAddressMap_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Require(Permission.CheckTools)) return;
        var sources = new List<AddressMapSource>();
        if (_workspaceModeChosen && !_isOfflineWorkspace && !_offlineSelectionRequired)
        {
            foreach (var database in GetActiveDatabases())
            {
                var connection = _connection.ToConnectionString(database);
                sources.Add(new($"Online · {database}", ct =>
                {
                    var source = new OnlineProjectDataSource(connection, database);
                    return Task.FromResult(new ProjectWorkspace(source, new OnlineProjectCrudDataSource(connection),
                        architecture: new ProjectArchitectureDataSource(source.Server, source.Database)));
                }, CanEdit: UserAuthorizationService.Has(Permission.Mode1Edit), Server: _connection.Server, Database: database));
            }
        }
        AddressMapSource? activeSource = null;
        foreach (var (path, project) in _offlineProjects)
        {
            bool active = _isOfflineWorkspace && ReferenceEquals(project, _openedLocalProject) &&
                string.Equals(path, _openedLocalProjectPath, StringComparison.OrdinalIgnoreCase);
            bool canEdit = active && UserAuthorizationService.Has(Permission.Mode1Edit);
            var source = new AddressMapSource($"Offline · {project.Manifest.Name} · {path}", ct =>
            {
                if (canEdit && _isOfflineWorkspace && ReferenceEquals(project, _openedLocalProject)) CommitOfflineGridToProject();
                return Task.FromResult(OfflineMapWorkspace(project));
            }, CanEdit: canEdit, Path: path, ReassignOffline: canEdit
                ? (owner, address, ct) => ReassignOfflinePlcAddressAsync(project, path, owner, address, ct) : null);
            sources.Add(source);
            if (active) activeSource = source;
        }

        new PlcAddressWindow(sources, async (path, ct) =>
            OfflineMapWorkspace((await _projectPackageService.OpenForCompareAsync(path, ct)).Project),
            ProjectEditor_ChangeJournalRequested, activeSource) { Owner = this }.Show();
    }

    private async Task<ChangeJournalRowChange> ReassignOfflinePlcAddressAsync(ConfiguratorProject project, string path,
        AddressOwner owner, string address, CancellationToken ct)
    {
        await _offlineSaveGate.WaitAsync(ct);
        ChangeJournalEntry entry;
        try
        {
            if (!UserAuthorizationService.Has(Permission.CheckTools) || !UserAuthorizationService.Has(Permission.Mode1Edit))
                throw new UnauthorizedAccessException("Нет права изменять адреса проекта.");
            if (!_isOfflineWorkspace || !ReferenceEquals(_openedLocalProject, project) ||
                !string.Equals(_openedLocalProjectPath, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Изменить адрес можно только в активном Offline-проекте.");
            var reservedFile = GetReservedProjectFile(path) ?? throw new IOException("Файл Offline-проекта больше не открыт.");
            CommitOfflineGridToProject();
            entry = await PlcAddressReassignment.ExecuteOfflineAsync(project, path, reservedFile, _projectPackageService, owner, address, ct);
            _offlineProjectDirty = false;
        }
        finally { _offlineSaveGate.Release(); }

        try
        {
            ShowLastAction(entry.DisplayDescription);
            RefreshActionLogUi();
            if (_currentMode == 1 && !string.IsNullOrWhiteSpace(_offlineCurrentTableName))
                OpenOfflineTable(_offlineCurrentTableName);
            if (_currentMode == 3 && _projectEditor != null)
                await _projectEditor.RefreshAfterOfflineJournalRestoreAsync();
        }
        catch (Exception ex)
        {
            SessionTestLog.Trace("КАРТА PLC", "Проект сохранён. Ошибка обновления интерфейса: " + Configurator.Infrastructure.UserErrorMessage.Format(ex));
        }
        return entry.Changes.Single();
    }

    private static ProjectWorkspace OfflineMapWorkspace(ConfiguratorProject project) =>
        new(new OfflineProjectDataSource(project), architecture: new ProjectArchitectureDataSource(project), allowWrite: false);
}
