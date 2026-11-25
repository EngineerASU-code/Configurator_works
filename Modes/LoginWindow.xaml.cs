using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
namespace Configurator;
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        WindowStartupLocation = Owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Loaded += (_, _) =>
        {
            UserAuthorizationService.InitializeIfNeeded();
            UserBox.ItemsSource = GetLoginChoices(UserAuthorizationService.Users);
            UserBox.SelectedIndex = 0;
            if (PasswordBox.IsEnabled) PasswordBox.Focus();
            else UserBox.Focus();
        };
        UserBox.SelectionChanged += (_, _) => UpdatePasswordPrompt();
    }
    internal static UserAccount[] GetLoginChoices(IEnumerable<UserAccount> users) => users
        .Where(user => user.Enabled || !UserAuthorizationService.IsCommissioningEngineer(user.UserName))
        .OrderBy(user => string.Equals(user.UserName, "admin", StringComparison.OrdinalIgnoreCase))
        .ThenBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void UpdatePasswordPrompt()
    {
        bool enabled = UserBox.SelectedItem is UserAccount { Enabled: true };
        PasswordBox.Clear();
        bool needsPassword = UserAuthorizationService.RequiresPassword(UserBox.SelectedItem as UserAccount);
        PasswordBox.IsEnabled = enabled && needsPassword;
        PasswordBox.Visibility = PasswordLabel.Visibility = needsPassword ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.IsEnabled = enabled;
        ErrorText.Text = enabled ? "" : "Учётная запись отключена. Для включения обратитесь к администратору.";
    }
    private void Login_Click(object sender, RoutedEventArgs e) => Login();
    private void PasswordBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Login(); }
    private void Login()
    {
        if (UserBox.SelectedItem is not UserAccount account)
        {
            ErrorText.Text = "Выберите пользователя для входа.";
            UserBox.Focus();
            return;
        }
        var userName = account.UserName;
        var password = PasswordBox.Password;
        if (UserAuthorizationService.Login(userName, password, out var error)) { DialogResult = true; return; }
        ErrorText.Text = error; PasswordBox.Clear(); PasswordBox.Focus();
    }
}
