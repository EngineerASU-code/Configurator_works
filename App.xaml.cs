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
            try
            {
                UserAuthorizationService.Initialize();
            }
            catch (Exception ex)
            {
                global::System.Windows.MessageBox.Show(
                    "Не удалось безопасно прочитать хранилище учётных записей. Приложение не будет запускаться и не перезапишет файл.\n\n" + ex.Message,
                    "Ошибка авторизации", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
                return;
            }

            if (UserAuthorizationService.NeedsInitialSetup)
            {
                var setup = new global::Configurator.AccessSetupWindow();
                if (setup.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }

                // Initial setup creates the administrator account but must not leave an
                // authenticated session behind. MainWindow must be shown first, then the
                // user explicitly authenticates through LoginWindow.
                UserAuthorizationService.Logout();
            }

            // Show the main Configurator start screen before authentication.
            // Offline project opening remains available without an Online login.
            try
            {
                var mainWindow = new global::Configurator.MainWindow();
                SetRequestedWindowPosition(mainWindow, e.Args);
                MainWindow = mainWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                global::System.Windows.MessageBox.Show(
                    "Не удалось открыть главное окно Configurator. Сеанс авторизации сохранён только в памяти; данные проекта не изменялись этим сообщением.\n\n" + ex,
                    "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-2);
            }
        }

        private static void SetRequestedWindowPosition(Window window, string[] args)
        {
            if (args.Length != 3 || args[0] != "--window-position" ||
                !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int left) ||
                !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int top))
                return;

            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!desktop.Contains(left, top)) return;

            window.WindowState = WindowState.Normal;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
        }
    }
}
