using System;
using System.Windows;

namespace Configurator;

public partial class BuiltInEngineerWindow : Window
{
    public BuiltInEngineerWindow() => InitializeComponent();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.IsSuperAdmin)
        {
            ErrorText.Text = "Управлять встроенной учётной записью может только Администратор.";
            return;
        }
        if (!string.Equals(PasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            ErrorText.Text = "Пароли не совпадают.";
            ConfirmPasswordBox.Clear();
            ConfirmPasswordBox.Focus();
            return;
        }
        try
        {
            UserAuthorizationService.ActivateBuiltInCommissioningEngineer(PasswordBox.Password);
            SessionTestLog.Trace("УЧЁТНЫЕ ЗАПИСИ", "Администратор включил встроенную учётную запись Инженер-ПНР и установил новый пароль.");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            PasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            PasswordBox.Focus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
