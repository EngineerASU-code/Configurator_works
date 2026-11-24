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
            var users = UserAuthorizationService.Users.Where(user => user.Enabled).ToList();
            UserBox.ItemsSource = users;
            var preferred = UserAuthorizationService.CurrentUser?.UserName ?? "admin";
            UserBox.SelectedItem = users.FirstOrDefault(user =>
                string.Equals(user.UserName, preferred, StringComparison.OrdinalIgnoreCase)) ?? users.FirstOrDefault();
            PasswordBox.Focus();
            UpdatePasswordPrompt();
        };
        UserBox.SelectionChanged += (_, _) => UpdatePasswordPrompt();
    }
    private void UpdatePasswordPrompt()
    {
        PasswordBox.IsEnabled = true;
        PasswordLabel.Text = "Пароль пользователя";
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
