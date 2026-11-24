#nullable enable
using System.Windows;
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
                sources.Add(new($"Online · {database}", ct =>
                {
                    var connection = _connection.ToConnectionString(database);
                    var source = new OnlineProjectDataSource(connection, database);
                    return Task.FromResult(new ProjectWorkspace(source, new OnlineProjectCrudDataSource(connection),
                        architecture: new ProjectArchitectureDataSource(source.Server, source.Database)));
                }, CanEdit: UserAuthorizationService.Has(Permission.Mode1Edit)));
            }
        }
        foreach (var (path, project) in _offlineProjects)
            sources.Add(new($"Offline · {project.Manifest.Name} · {path}", ct => Task.FromResult(OfflineMapWorkspace(project)), Path: path));

        new PlcAddressWindow(sources, async (path, ct) =>
            OfflineMapWorkspace((await _projectPackageService.OpenForCompareAsync(path, ct)).Project)) { Owner = this }.ShowDialog();
    }

    private static ProjectWorkspace OfflineMapWorkspace(ConfiguratorProject project) =>
        new(new OfflineProjectDataSource(project), architecture: new ProjectArchitectureDataSource(project), allowWrite: false);
}
