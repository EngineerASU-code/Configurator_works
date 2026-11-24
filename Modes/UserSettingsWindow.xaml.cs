using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
namespace Configurator;
public partial class UserSettingsWindow : Window
{
    private string _originalRole; private readonly Dictionary<Permission,CheckBox> _checks=new();
    public UserSettingsWindow()
    {
        InitializeComponent();
        if (!UserAuthorizationService.IsAdministrator) { Loaded += (_, _) => { Configurator.Infrastructure.ModernDialog.Show("Настройки пользователей доступны только Администратору.", "Доступ запрещён", MessageBoxButton.OK, MessageBoxImage.Warning); Close(); }; return; }
        RolesTab.Visibility = Visibility.Collapsed;
        RoleBox.IsEnabled = false;
        EnabledBox.IsEnabled = false;
        DisplayNameBox.IsEnabled = false;
        Loaded += (_, _) => { RefreshRoles(); RefreshUsers(); };
    }
    private void RefreshUsers()
    {
        UsersList.ItemsSource = UserAuthorizationService.Users;
        UsersList.SelectedIndex = UsersList.Items.Count == 0 ? -1 : 0;
    }
    private void RefreshRoles(){RolesList.ItemsSource=null;RolesList.ItemsSource=UserAuthorizationService.Roles.Select(x=>x.Name).ToList();RoleBox.ItemsSource=UserAuthorizationService.Roles.Select(x=>x.Name).ToList();if(RolesList.Items.Count>0&&RolesList.SelectedIndex<0)RolesList.SelectedIndex=0;}
    private void UsersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UsersList.SelectedItem is not UserAccount user) return;
        UserNameBox.Text = user.UserName;
        DisplayNameBox.Text = user.DisplayName;
        RoleBox.SelectedItem = user.RoleName;
        EnabledBox.IsChecked = user.Enabled;
        PasswordBox.Clear();
    }
    private void RolesList_SelectionChanged(object s,SelectionChangedEventArgs e){var name=RolesList.SelectedItem?.ToString();var r=UserAuthorizationService.Roles.FirstOrDefault(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase));if(r==null)return;_originalRole=r.Name;RoleNameBox.Text=r.Name;BuildPermissionChecks(r.Permissions.Select(x => Enum.TryParse<Permission>(x, out var parsed) ? parsed : (Permission?)null).Where(x => x.HasValue).Select(x => x.Value).ToHashSet());}

    private void BuildPermissionChecks(HashSet<Permission> selected)
    {
        PermissionsHost.Children.Clear();
        _checks.Clear();
        bool hasMode1Rights = selected.Overlaps(new[] { Permission.Mode1View, Permission.Mode1Add, Permission.Mode1Edit, Permission.Mode1Delete });
        foreach (var group in PermissionCatalog.Groups)
        {
            var groupPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            groupPanel.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 5, 4, 2),
                Foreground = (System.Windows.Media.Brush)FindResource("BrushText")
            });
            groupPanel.Children.Add(new TextBlock
            {
                Text = group.Description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 7),
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary")
            });
            foreach (var entry in group.Entries)
            {
                bool isAdminRole = string.Equals(_originalRole, "Администратор", StringComparison.OrdinalIgnoreCase);
                bool isChecked = isAdminRole || selected.Contains(entry.Permission);
                // Display legacy aliases as effective selections without rewriting the stored role.
                if (!hasMode1Rights && entry.Permission == Permission.Mode1View) isChecked |= selected.Contains(Permission.ViewData);
                if (!hasMode1Rights && entry.Permission == Permission.Mode1Add) isChecked |= selected.Contains(Permission.AddData);
                if (!hasMode1Rights && entry.Permission == Permission.Mode1Edit) isChecked |= selected.Contains(Permission.EditData);
                if (!hasMode1Rights && entry.Permission == Permission.Mode1Delete) isChecked |= selected.Contains(Permission.DeleteData);
                if (entry.Permission == Permission.Mode2Manage) isChecked |= selected.Contains(Permission.ManageClasses) || selected.Contains(Permission.ManageRelations);
                if (entry.Permission == Permission.Mode3Manage) isChecked |= selected.Contains(Permission.ManageObjects);

                var row = new Grid { Margin = new Thickness(4, 3, 4, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var cb = new CheckBox
                {
                    Content = entry.Title,
                    Tag = entry.Permission,
                    Margin = new Thickness(0, 2, 12, 0),
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushText"),
                    IsChecked = isChecked,
                    IsEnabled = !isAdminRole,
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = entry.Details ?? entry.Description
                };
                cb.SetValue(AutomationProperties.NameProperty, entry.Title);
                Grid.SetColumn(cb, 0);
                row.Children.Add(cb);
                var description = new TextBlock
                {
                    Text = entry.Description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextSecondary"),
                    Margin = new Thickness(0, 0, 4, 0),
                    ToolTip = entry.Details ?? entry.Description
                };
                Grid.SetColumn(description, 1);
                row.Children.Add(description);
                groupPanel.Children.Add(row);
                _checks[entry.Permission] = cb;
            }
            var separator = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Background = (System.Windows.Media.Brush)FindResource("BrushBorder") };
            groupPanel.Children.Add(separator);
            PermissionsHost.Children.Add(groupPanel);
        }
    }
    private void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        if (UsersList.SelectedItem is not UserAccount user) return;
        try
        {
            UserAuthorizationService.SaveUser(user.UserName, user.UserName, DisplayNameBox.Text,
                RoleBox.SelectedItem?.ToString() ?? user.RoleName, EnabledBox.IsChecked == true, PasswordBox.Password);
            RefreshUsers();
            UsersList.SelectedValue = user.UserName;
            Configurator.Infrastructure.ModernDialog.Show("Пользователь сохранён.", "Пользователи", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex),
                "Пользователи", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void AddRole_Click(object s,RoutedEventArgs e){_originalRole=null;RoleNameBox.Text="";BuildPermissionChecks(new HashSet<Permission>());RolesList.SelectedIndex=-1;RoleNameBox.Focus();}
    private void DeleteRole_Click(object s,RoutedEventArgs e){var name=RolesList.SelectedItem?.ToString();if(string.IsNullOrWhiteSpace(name))return;try{UserAuthorizationService.DeleteRole(name);RefreshRoles();}catch(Exception ex){Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex),"Роли",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void SaveRole_Click(object s,RoutedEventArgs e){try{var perms=_checks.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key);UserAuthorizationService.SaveRole(_originalRole,RoleNameBox.Text,"",perms);RefreshRoles();RolesList.SelectedItem=RoleNameBox.Text;}catch(Exception ex){Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex),"Роли",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void Close_Click(object s,RoutedEventArgs e)=>Close();
}
