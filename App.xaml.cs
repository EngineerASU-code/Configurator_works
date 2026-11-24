using System;
using System.Globalization;
using System.Windows;
using Configurator.Themes;

namespace Configurator
{
    public partial class App : global::System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Keep the process alive while modal setup/login windows are replaced by MainWindow.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Тема применяется до создания главного окна, поэтому первый кадр
            // приложения не появляется в "не той" палитре.
            try { ThemeManager.InitializeDefault(); }
            catch { /* Ресурсы темы имеют безопасные значения из App.xaml. */ }
            var initialization = Task.Run(UserAuthorizationService.Initialize);
            var welcome = new WorkspaceStartupDialog();
            SetRequestedWindowPosition(welcome, e.Args);
            MainWindow = welcome;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            bool opening = false;
            welcome.StartRequested += async (_, _) =>
            {
                if (opening) return;
                opening = true;
                try
                {
                    await initialization;
                }
                catch (Exception ex)
                {
                    global::System.Windows.MessageBox.Show(
                        "Не удалось безопасно прочитать хранилище учётных записей. Приложение не будет запускаться и не перезапишет файл.\n\n" + ex.Message,
                        "Ошибка авторизации", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(-1);
                    return;
                }
                if (!welcome.IsVisible) return;
                opening = false;
                var dialog = UserAuthorizationService.NeedsInitialSetup
                    ? (Window)new AccessSetupWindow()
                    : new LoginWindow();
                dialog.Owner = welcome;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                if (dialog.ShowDialog() != true || UserAuthorizationService.CurrentUser == null)
                    return;

                try
                {
                    var editor = new global::Configurator.MainWindow();
                    var desiredState = editor.WindowState;
                    editor.WindowState = WindowState.Normal;
                    editor.Owner = welcome;
                    editor.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    bool explicitPosition = SetRequestedWindowPosition(editor, e.Args);
                    MainWindow = editor;
                    editor.Show();
                    editor.Owner = null;
                    if (!explicitPosition) editor.WindowState = desiredState;
                    welcome.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось открыть главное окно Configurator.\n\n" + ex.Message,
                        "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(-2);
                }
            };
            welcome.Show();
        }
        private static bool SetRequestedWindowPosition(Window window, string[] args)
        {
            if (args.Length != 3 || args[0] != "--window-position" ||
                !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int left) ||
                !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int top))
                return false;

            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!desktop.Contains(left, top)) return false;

            window.WindowState = WindowState.Normal;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            return true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
        }
    }
}
