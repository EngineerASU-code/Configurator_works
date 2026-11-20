#nullable enable
using System.ComponentModel;
using System.Windows;
using Configurator.Core.Compare;
using Configurator.Infrastructure.Projects;

namespace Configurator;

public partial class MainWindow
{
    private CompareWindow? _compareWindow;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_compareWindow?.IsApplyingTransfer == true)
        {
            e.Cancel = true;
            StatusText.Text = "Дождитесь завершения переноса в окне сравнения.";
        }
        base.OnClosing(e);
    }

    private void CloseCompareWindow() => _compareWindow?.CloseForContextChange();

    private void MenuOptions_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        var user = UserAuthorizationService.CurrentUser;
        var role = UserAuthorizationService.CurrentRole;
        bool canOpen = PermissionPolicy.CanEnterMode(user, role, 2) ||
            PermissionPolicy.CanEnterMode(user, role, 3);
        MenuCompare.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MenuCompare_Click(object sender, RoutedEventArgs e)
    {
        var user = UserAuthorizationService.CurrentUser;
        var role = UserAuthorizationService.CurrentRole;
        bool canShowMode2 = PermissionPolicy.CanEnterMode(user, role, 2);
        bool canShowMode3 = PermissionPolicy.CanEnterMode(user, role, 3);
        if (!canShowMode2 && !canShowMode3) return;

        if (_compareWindow is { } current)
        {
            if (!current.IsVisible) return;
            if (current.WindowState == WindowState.Minimized)
                current.WindowState = WindowState.Normal;
            current.Activate();
            return;
        }

        bool canUseOnline = _workspaceModeChosen && !_isOfflineWorkspace && !_offlineSelectionRequired;
        IEnumerable<string> databases = canUseOnline ? GetActiveDatabases() : Array.Empty<string>();
        bool canRequestWrite = canShowMode3 && UserAuthorizationService.Has(Permission.Mode3Manage) &&
            (UserAuthorizationService.Has(Permission.EditData) || UserAuthorizationService.Has(Permission.AddData));

        static bool IsTransferAuthorized(CompareDifference difference) =>
            PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 3) &&
            UserAuthorizationService.Has(Permission.Mode3Manage) &&
            UserAuthorizationService.Has(difference.Column is null ? Permission.AddData : Permission.EditData);

        var window = new CompareWindow(
            databases,
            _selectedDbName,
            databaseName => _connection.ToConnectionString(databaseName),
            IsTransferAuthorized,
            canUseOnline,
            canShowMode2,
            canShowMode3,
            canShowMode2 && UserAuthorizationService.Has(Permission.Mode2GraphShow),
            canShowMode2 && UserAuthorizationService.Has(Permission.Mode2RelationsShow),
            canShowMode2 && UserAuthorizationService.Has(Permission.Mode2FieldsShow),
            canRequestWrite)
        {
            ApplyTransferAsync = CompareTransfer.ApplyAsync
        };
        window.Closed += (_, _) => _compareWindow = null;
        _compareWindow = window;
        window.Show();
    }
}
