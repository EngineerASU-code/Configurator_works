using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
namespace Configurator;
public partial class UserSettingsWindow : Window
{
    private string _originalUser; private string _originalRole; private readonly Dictionary<Permission,CheckBox> _checks=new();
    public UserSettingsWindow()
    {
        InitializeComponent();
        if (!UserAuthorizationService.IsAdministrator) { Loaded += (_, _) => { Configurator.Infrastructure.ModernDialog.Show("Настройки пользователей доступны только Администратору.", "Доступ запрещён", MessageBoxButton.OK, MessageBoxImage.Warning); Close(); }; return; }
        Loaded += (_, _) => { AddUserButton.IsEnabled = false; DeleteUserButton.IsEnabled = false; RolesTab.Visibility = Visibility.Visible; RefreshUsers(); RefreshRoles(); };
    }
    private void RefreshUsers(){UsersList.ItemsSource=null;UsersList.ItemsSource=UserAuthorizationService.Users.Select(x=>x.UserName.Equals("admin",StringComparison.OrdinalIgnoreCase) ? "Администратор" : x.DisplayName).ToList();if(UsersList.Items.Count>0&&UsersList.SelectedIndex<0)UsersList.SelectedIndex=0;}
    private void RefreshRoles(){RolesList.ItemsSource=null;RolesList.ItemsSource=UserAuthorizationService.Roles.Select(x=>x.Name).ToList();RoleBox.ItemsSource=UserAuthorizationService.Roles.Select(x=>x.Name).ToList();if(RolesList.Items.Count>0&&RolesList.SelectedIndex<0)RolesList.SelectedIndex=0;}
    private void UsersList_SelectionChanged(object s,SelectionChangedEventArgs e){var name=UsersList.SelectedItem?.ToString();var userName=name?.Equals("Администратор",StringComparison.OrdinalIgnoreCase)==true ? "admin" : name;var u=UserAuthorizationService.Users.FirstOrDefault(x=>x.UserName.Equals(userName,StringComparison.OrdinalIgnoreCase) || x.DisplayName.Equals(name,StringComparison.OrdinalIgnoreCase));if(u==null)return;AddUserButton.IsEnabled=true;DeleteUserButton.IsEnabled=!UserAuthorizationService.IsBuiltInUser(u.UserName);_originalUser=u.UserName;UserNameBox.Text=u.UserName;UserNameBox.IsReadOnly=u.UserName.Equals("admin",StringComparison.OrdinalIgnoreCase);DisplayNameBox.Text=u.DisplayName;RoleBox.SelectedItem=u.RoleName;EnabledBox.IsChecked=u.Enabled;PasswordBox.Clear();PasswordBox.IsEnabled=true; }
    private void RolesList_SelectionChanged(object s,SelectionChangedEventArgs e){var name=RolesList.SelectedItem?.ToString();var r=UserAuthorizationService.Roles.FirstOrDefault(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase));if(r==null)return;_originalRole=r.Name;RoleNameBox.Text=r.Name;BuildPermissionChecks(r.Permissions.Select(x => Enum.TryParse<Permission>(x, out var parsed) ? parsed : (Permission?)null).Where(x => x.HasValue).Select(x => x.Value).ToHashSet());}

    private void BuildPermissionChecks(HashSet<Permission> selected)
    {
        PermissionsHost.Children.Clear();
        _checks.Clear();
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
                bool isChecked = selected.Contains(entry.Permission);
                // Display legacy aliases as effective selections without rewriting the stored role.
                if (entry.Permission == Permission.Mode1View) isChecked |= selected.Contains(Permission.ViewData);
                if (entry.Permission == Permission.Mode1Add) isChecked |= selected.Contains(Permission.AddData);
                if (entry.Permission == Permission.Mode1Edit) isChecked |= selected.Contains(Permission.EditData);
                if (entry.Permission == Permission.Mode1Delete) isChecked |= selected.Contains(Permission.DeleteData);
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
    private void AddUser_Click(object s,RoutedEventArgs e)
    {
        _originalUser = null;
        UsersList.SelectedIndex = -1;
        UserNameBox.IsReadOnly = false;
        UserNameBox.Clear(); DisplayNameBox.Clear(); RoleBox.SelectedItem = "Проектировщик";
        EnabledBox.IsChecked = true; PasswordBox.Clear(); PasswordBox.IsEnabled = true;
        AddUserButton.IsEnabled = false; DeleteUserButton.IsEnabled = false;
        UserNameBox.Focus();
    }
    private void DeleteUser_Click(object s,RoutedEventArgs e){var name=_originalUser;if(string.IsNullOrWhiteSpace(name))return;try{UserAuthorizationService.DeleteUser(name);RefreshUsers();}catch(Exception ex){Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex),"Пользователи",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void SaveUser_Click(object s,RoutedEventArgs e){try{var savedName=UserNameBox.Text; UserAuthorizationService.SaveUser(_originalUser,UserNameBox.Text,DisplayNameBox.Text,RoleBox.SelectedItem?.ToString()??"Проектировщик",EnabledBox.IsChecked==true,PasswordBox.Password);_originalUser=savedName;RefreshUsers();UsersList.SelectedItem=UserAuthorizationService.Users.FirstOrDefault(u=>u.UserName.Equals(savedName,StringComparison.OrdinalIgnoreCase))?.DisplayName;Configurator.Infrastructure.ModernDialog.Show("Пользователь сохранён.","Пользователи",MessageBoxButton.OK,MessageBoxImage.Information);}catch(Exception ex){Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex),"Пользователи",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void AddRole_Click(object s,RoutedEventArgs e){_originalRole=null;RoleNameBox.Text="";BuildPermissionChecks(new HashSet<Permission>());RolesList.SelectedIndex=-1;RoleNameBox.Focus();}
    private void DeleteRole_Click(object s,RoutedEventArgs e){var name=RolesList.SelectedItem?.ToString();if(string.IsNullOrWhiteSpace(name))return;try{UserAuthorizationService.DeleteRole(name);RefreshRoles();}catch(Exception ex){Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex),"Роли",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void SaveRole_Click(object s,RoutedEventArgs e){try{var perms=_checks.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key);UserAuthorizationService.SaveRole(_originalRole,RoleNameBox.Text,"",perms);RefreshRoles();RolesList.SelectedItem=RoleNameBox.Text;}catch(Exception ex){Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex),"Роли",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void Close_Click(object s,RoutedEventArgs e)=>Close();
}
