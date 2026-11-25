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
        MenuCheckRecord.Visibility = UserAuthorizationService.Has(Permission.CheckTools) ? Visibility.Visible : Visibility.Collapsed;
        MenuAddressMap.Visibility = MenuCheckRecord.Visibility;
        MenuCheckRecord.IsEnabled = _workspaceModeChosen && !_isOfflineWorkspace && !_offlineSelectionRequired;
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
        bool canRequestWrite = (canShowMode3 && (UserAuthorizationService.Has(Permission.ManageTextLists) ||
            UserAuthorizationService.Has(Permission.EditData) || UserAuthorizationService.Has(Permission.AddData) || UserAuthorizationService.Has(Permission.DeleteData))) ||
            (canShowMode2 && (UserAuthorizationService.Has(Permission.Mode2Manage) ||
                UserAuthorizationService.Has(Permission.ManageClasses) || UserAuthorizationService.Has(Permission.ManageRelations) ||
                UserAuthorizationService.Has(Permission.ManageTextLists)));

        static bool IsTransferAuthorized(CompareDifference difference, bool aToB)
        {
            if (difference.MetadataKind == CompareMetadataKind.ClassDefinition)
                return PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 2) &&
                    UserAuthorizationService.Has(Permission.Mode2FieldsShow) &&
                    (UserAuthorizationService.Has(Permission.Mode2Manage) || UserAuthorizationService.Has(Permission.ManageClasses));
            if (difference.MetadataKind == CompareMetadataKind.ManualRelation)
                return PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 2) &&
                    UserAuthorizationService.Has(Permission.Mode2RelationsShow) &&
                    (UserAuthorizationService.Has(Permission.Mode2Manage) || UserAuthorizationService.Has(Permission.ManageRelations));
            if (difference.MetadataKind is CompareMetadataKind.ProjectTextList or CompareMetadataKind.ThemePreset)
                return (PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 2) ||
                    PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 3)) &&
                    (UserAuthorizationService.Has(Permission.Mode2Manage) || UserAuthorizationService.Has(Permission.ManageTextLists));
            var required = Permission.EditData;
            if (difference.Column is null && difference.Status is (CompareStatus.OnlyA or CompareStatus.OnlyB))
            {
                bool sourceExists = aToB == (difference.Status == CompareStatus.OnlyA);
                required = sourceExists ? Permission.AddData : Permission.DeleteData;
            }
            return PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 3) &&
                UserAuthorizationService.Has(required);
        }

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
            canRequestWrite,
            canShowMode2 || canShowMode3)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ApplyTransferAsync = CompareTransfer.ApplyAsync,
            ApplyMetadataTransferAsync = CompareTransfer.ApplyMetadataAsync
        };
        window.JournalChanged += (entry, path) =>
        {
            _compareActions[entry.OperationId] = (entry, path);
            ShowLastAction(entry.DisplayDescription);
            RefreshActionLogUi();
        };
        window.Closed += (_, _) => _compareWindow = null;
        _compareWindow = window;
        window.Show();
    }
}
