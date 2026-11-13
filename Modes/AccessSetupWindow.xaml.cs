using System;
using System.Windows;

namespace Configurator;

public partial class AccessSetupWindow : Window
{
    public AccessSetupWindow()
    {
        InitializeComponent();
        if (!UserAuthorizationService.HasAdministratorAccount)
        {
            CurrentPasswordLabel.Visibility = Visibility.Collapsed;
            CurrentPasswordBox.Visibility = Visibility.Collapsed;
            CurrentPasswordBox.IsEnabled = false;
        }
        Loaded += (_, _) => (UserAuthorizationService.HasAdministratorAccount ? CurrentPasswordBox : NewPasswordBox).Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(NewPasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            ErrorText.Text = "Пароли не совпадают.";
            ConfirmPasswordBox.Clear();
            ConfirmPasswordBox.Focus();
            return;
        }
        try
        {
            UserAuthorizationService.CompleteInitialSetup(CurrentPasswordBox.Password, NewPasswordBox.Password);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            if (UserAuthorizationService.HasAdministratorAccount) CurrentPasswordBox.Focus(); else NewPasswordBox.Focus();
        }
    }
}
