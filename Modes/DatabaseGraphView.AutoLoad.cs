using System;
using System.Windows;
using System.Windows.Threading;

namespace Configurator;

public partial class DatabaseGraphView
{
    private DispatcherTimer _autoGraphTimer;
    private bool _autoGraphLoading;
    private string _autoGraphDatabase = "";
    private bool _autoGraphInitialRefreshDone;

    private static readonly bool _autoGraphHandlersRegistered = RegisterAutoGraphHandlers();

    private static bool RegisterAutoGraphHandlers()
    {
        EventManager.RegisterClassHandler(typeof(DatabaseGraphView), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(AutoGraphLoaded), true);
        EventManager.RegisterClassHandler(typeof(DatabaseGraphView), FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(AutoGraphUnloaded), true);
        return true;
    }

    private static void AutoGraphLoaded(object sender, RoutedEventArgs e)
        => ((DatabaseGraphView)sender).StartAutoGraphRefresh();

    private static void AutoGraphUnloaded(object sender, RoutedEventArgs e)
        => ((DatabaseGraphView)sender)._autoGraphTimer?.Stop();

    private void StartAutoGraphRefresh()
    {
        if (_autoGraphTimer != null) return;
        _autoGraphTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _autoGraphTimer.Tick += async (_, _) =>
        {
            if (_autoGraphLoading || !IsVisible || string.IsNullOrWhiteSpace(_database)) return;
            bool databaseChanged = !string.Equals(_autoGraphDatabase, _database, StringComparison.OrdinalIgnoreCase);
            bool needsInitialLoad = !_autoGraphInitialRefreshDone;
            bool needsDataLoad = _tables.Count == 0;
            if (!databaseChanged && !needsInitialLoad && !needsDataLoad) return;

            _autoGraphLoading = true;
            try
            {
                await RefreshAsync();
                _autoGraphDatabase = _database;
                _autoGraphInitialRefreshDone = true;
                StartCompactGraphSync();
            }
            finally { _autoGraphLoading = false; }
        };
        _autoGraphTimer.Start();
    }
}
