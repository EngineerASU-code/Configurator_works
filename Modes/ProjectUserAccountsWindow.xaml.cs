using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Configurator.Core.Projects;

namespace Configurator;

public partial class ProjectUserAccountsWindow : Window
{
    private readonly ConfiguratorProject _project;
    private readonly Func<ConfiguratorProject, Task> _saveProject;
    private readonly Action _accountsChanged;
    private List<ProjectUserAccount> _users;
    private string _selectedUserName;
    private bool _loading;

    public ProjectUserAccountsWindow(ConfiguratorProject project, Func<ConfiguratorProject, Task> saveProject, Action accountsChanged)
    {
        InitializeComponent();
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _saveProject = saveProject ?? throw new ArgumentNullException(nameof(saveProject));
        _accountsChanged = accountsChanged ?? throw new ArgumentNullException(nameof(accountsChanged));
        _users = project.Users.Select(Clone).ToList();
        RoleBox.ItemsSource = UserAuthorizationService.Roles
            .Where(role => new[] { "Проектировщик", "Инженер ПНР", "Гость" }.Contains(role.Name, StringComparer.OrdinalIgnoreCase))
            .Select(role => role.Name).ToList();
        RefreshUsers();
        ClearEditor();
    }

    private void RefreshUsers(string selectedUserName = null)
    {
        _loading = true;
        ProjectUsersList.ItemsSource = null;
        ProjectUsersList.ItemsSource = _users;
        ProjectUsersList.SelectedItem = _users.FirstOrDefault(user => user.UserName.Equals(selectedUserName, StringComparison.OrdinalIgnoreCase));
        _loading = false;
    }

    private void ClearEditor()
    {
        _selectedUserName = null;
        UserNameBox.IsReadOnly = false;
        UserNameBox.Clear();
        DisplayNameBox.Clear();
        RoleBox.SelectedItem = "Проектировщик";
        PasswordLabel.Text = "Пароль";
        PasswordBox.Clear();
        EnabledBox.IsChecked = true;
        DeleteUserButton.IsEnabled = false;
    }

    private void ProjectUsersList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || ProjectUsersList.SelectedItem is not ProjectUserAccount user) return;
        _selectedUserName = user.UserName;
        UserNameBox.Text = user.UserName;
        UserNameBox.IsReadOnly = true;
        DisplayNameBox.Text = user.DisplayName;
        RoleBox.SelectedItem = user.RoleName;
        PasswordLabel.Text = "Новый пароль (необязательно)";
        PasswordBox.Clear();
        EnabledBox.IsChecked = user.Enabled;
        DeleteUserButton.IsEnabled = true;
    }

    private void AddUser_Click(object sender, RoutedEventArgs e)
    {
        ProjectUsersList.SelectedIndex = -1;
        ClearEditor();
        UserNameBox.Focus();
    }

    private async void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!UserAuthorizationService.IsSuperAdmin)
                throw new UnauthorizedAccessException("Управлять проектными учётными записями может только встроенный Администратор.");

            string userName = UserNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("Укажите логин.");
            if (_selectedUserName is null && (UserAuthorizationService.IsBuiltInUser(userName) || userName.Equals("guest", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Этот логин зарезервирован для встроенной учётной записи.");
            if (_users.Any(user => !user.UserName.Equals(_selectedUserName ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                                   user.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Учётная запись с таким логином уже есть в этом проекте.");

            var updated = _users.Select(Clone).ToList();
            bool creating = string.IsNullOrWhiteSpace(_selectedUserName);
            var account = creating
                ? new ProjectUserAccount { UserName = userName }
                : updated.First(user => user.UserName.Equals(_selectedUserName, StringComparison.OrdinalIgnoreCase));
            account.DisplayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? userName : DisplayNameBox.Text.Trim();
            account.RoleName = RoleBox.SelectedItem?.ToString() ?? "Проектировщик";
            account.Enabled = EnabledBox.IsChecked == true;
            if (creating && string.IsNullOrEmpty(PasswordBox.Password))
                throw new ArgumentException("Для новой учётной записи задайте пароль длиной не менее 5 символов.");
            if (!string.IsNullOrEmpty(PasswordBox.Password))
                UserAuthorizationService.SetProjectAccountPassword(account, PasswordBox.Password);

            if (creating) updated.Add(account);
            await PersistUsersAsync(updated, account.UserName);
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show(this, ex.Message, "Учётные записи проекта", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectUsersList.SelectedItem is not ProjectUserAccount selected) return;
        if (Configurator.Infrastructure.ModernDialog.Show(this,
            $"Удалить учётную запись «{selected.DisplayName}» ({selected.UserName}) из этого проекта?",
            "Удаление учётной записи", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var updated = _users.Where(user => !user.UserName.Equals(selected.UserName, StringComparison.OrdinalIgnoreCase))
                .Select(Clone).ToList();
            await PersistUsersAsync(updated);
            ClearEditor();
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show(this, ex.Message, "Учётные записи проекта", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PersistUsersAsync(List<ProjectUserAccount> updated, string selectedUserName = null)
    {
        if (!UserAuthorizationService.IsSuperAdmin)
            throw new UnauthorizedAccessException("Сохранять учётные записи проекта может только встроенный Администратор.");
        var previous = _project.Users.Select(Clone).ToList();
        bool packageSaved = false;
        bool approvalsSaved = false;
        _project.Users = updated.Select(Clone).ToList();
        try
        {
            await _saveProject(_project);
            packageSaved = true;
            UserAuthorizationService.ReplaceProjectUserApprovals(_project.Manifest.ProjectId, _project.Users);
            approvalsSaved = true;
            _accountsChanged();
            _users = _project.Users.Select(Clone).ToList();
            RefreshUsers(selectedUserName);
            if (selectedUserName is null) ClearEditor();
            else
            {
                _selectedUserName = selectedUserName;
                UserNameBox.IsReadOnly = true;
                PasswordLabel.Text = "Новый пароль (необязательно)";
                PasswordBox.Clear();
                DeleteUserButton.IsEnabled = true;
            }
        }
        catch (Exception saveError)
        {
            _project.Users = previous;
            var rollbackErrors = new List<Exception>();
            if (packageSaved)
            {
                try { await _saveProject(_project); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            if (approvalsSaved)
            {
                try { UserAuthorizationService.ReplaceProjectUserApprovals(_project.Manifest.ProjectId, previous); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            if (rollbackErrors.Count > 0)
                throw new IOException("Не удалось полностью отменить изменение учётных записей. Проверьте Offline-файл и подтверждения Администратора.",
                    new AggregateException(new[] { saveError }.Concat(rollbackErrors)));
            throw;
        }
    }

    private static ProjectUserAccount Clone(ProjectUserAccount user) => new()
    {
        UserName = user.UserName,
        DisplayName = user.DisplayName,
        RoleName = user.RoleName,
        PasswordHash = user.PasswordHash,
        PasswordSalt = user.PasswordSalt,
        PasswordHashVersion = user.PasswordHashVersion,
        PasswordIterations = user.PasswordIterations,
        Enabled = user.Enabled
    };

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
