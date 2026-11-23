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
            UserBox.ItemsSource = UserAuthorizationService.Users.ToList();
            UserBox.SelectedValue = UserAuthorizationService.CurrentUser?.UserName ?? "admin";
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
        var userName = UserBox.SelectedValue?.ToString() ?? "";
        var password = PasswordBox.Password;
        if (UserAuthorizationService.Login(userName, password, out var error)) { DialogResult = true; return; }
        ErrorText.Text = error; PasswordBox.Clear(); PasswordBox.Focus();
    }
}
