using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Configurator;

public partial class ProjectModeView
{
    private string _resolvedProjectDatabase = "";
    private bool _resolvingProjectArchitecture;
    private static readonly bool _dynamicHandlersRegistered = RegisterDynamicHandlers();

    private static bool RegisterDynamicHandlers()
    {
        EventManager.RegisterClassHandler(typeof(ProjectModeView), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ProjectModeView_LoadedDynamic));
        EventManager.RegisterClassHandler(typeof(ProjectModeView), ButtonBase.ClickEvent,
            new RoutedEventHandler(ProjectModeView_ButtonClickedDynamic), true);
        return true;
    }

    private static void ProjectModeView_LoadedDynamic(object sender, RoutedEventArgs e)
        => ScheduleDynamicArchitecture((ProjectModeView)sender);

    private static void ProjectModeView_ButtonClickedDynamic(object sender, RoutedEventArgs e)
    {
        var view = (ProjectModeView)sender;
        if (e.OriginalSource is not Button button) return;
        var content = button.Content?.ToString() ?? "";
        if (content == "Выбрать проект" || content == "↻")
            ScheduleDynamicArchitecture(view);
    }

    private static void ScheduleDynamicArchitecture(ProjectModeView view)
    {
        view.Dispatcher.BeginInvoke(new Action(async () => await view.ResolveProjectArchitectureAsync()), DispatcherPriority.ContextIdle);
    }

    private async Task ResolveProjectArchitectureAsync()
    {
        if (_resolvingProjectArchitecture || _connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return;
        if (string.Equals(_resolvedProjectDatabase, CurrentProjectIdentity, StringComparison.OrdinalIgnoreCase)) return;
        _resolvingProjectArchitecture = true;
        var projectIdentity = CurrentProjectIdentity;
        try
        {
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var resolver = new ProjectArchitectureResolver();
            await resolver.ResolveAsync(_architecture, db, isCurrentProject: () =>
                string.Equals(projectIdentity, CurrentProjectIdentity, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(projectIdentity, CurrentProjectIdentity, StringComparison.OrdinalIgnoreCase)) return;
            _resolvedProjectDatabase = projectIdentity;
            RefreshClasses();
            if (_selectedClass != null)
            {
                await LoadActualClassFieldsAsync();
                await LoadObjectsAsync();
                RebuildObjectFilters();
            }
            TxtStatus.Text = $"Архитектура проекта определена по БД «{_selectedDatabase}»";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Архитектура БД не определена: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
        }
        finally
        {
            _resolvingProjectArchitecture = false;
            if (!string.Equals(projectIdentity, CurrentProjectIdentity, StringComparison.OrdinalIgnoreCase))
                ScheduleDynamicArchitecture(this);
        }
    }

}
