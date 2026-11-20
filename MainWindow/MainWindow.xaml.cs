using Configurator.Core.Projects;
using Configurator.Themes;
using System;
using System.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Configurator
{
    public partial class MainWindow : Window
    {
        private ConnectionSettings _connection = new ConnectionSettings();
        private Dictionary<string, TableViewModel> _vms = new Dictionary<string, TableViewModel>();
        private TableViewModel _activeVm;
        private int _currentMode = 1;
        private bool _mode2ProjectSelectedOnEntry;
        private bool _mode3ProjectSelectedOnEntry;
        private bool _offlineSelectionRequired;
        private bool _workspaceModeChosen;
        // Prevent permission refresh from opening a mode/project dialog while LoginWindow is still modal.
        private bool _loginDialogOpen;
        private bool _startupInitializationPending = true;
        private bool _startupInitialized;
        private string _activeDbName = "";
        private string _selectedDbName = "";
        private DisplayFieldsBuilder _fieldsBuilder;
        private HashSet<string> _failedDatabases = new HashSet<string>();
        private HashSet<string> _loadedTreeNodes = new HashSet<string>();
        private int _loadGeneration = 0;
        private CancellationTokenSource _reconnectCts;
        private CancellationTokenSource _dataLoadCts;
        private bool _fieldsPanelExpanded = false;
        private readonly ActionLogService _actionLog = new ActionLogService();
        private readonly SessionTestLog _sessionTestLog = new SessionTestLog();
        private readonly ChangeJournalService _changeJournal = new ChangeJournalService();
        private GraphRelationRestorePayload _pendingLogicalRelationRestore;
        private TemplateArchitectureView _templateEditor;
        private ProjectModeView _projectEditor;

        private readonly LeftPanel _leftPanel;
        private readonly RightPanel _rightPanel;

        private string VmCacheKey(string dbName) => $"{_connection.Server}|{dbName}";

        public MainWindow()
        {
            InitializeComponent();
            SessionTestLog.Current = _sessionTestLog;
            WindowState = WindowState.Maximized;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _leftPanel = LeftPanelControl;
            _rightPanel = RightPanelControl;
            _templateEditor = TemplateArchitectureControl;
            _projectEditor = ProjectModeControl;
            _offlineAutoSaveTimer.Tick += OfflineAutoSaveTimer_Tick;
            PreviewMouseDown += MainWindow_PreviewMouseDownForTestLog;
            System.Windows.Application.Current.DispatcherUnhandledException += MainWindow_DispatcherUnhandledExceptionForTestLog;
            _sessionTestLog.Add("СТАРТ", "Главное окно создано");
            Closed += (_, _) =>
            {
                System.Windows.Application.Current.DispatcherUnhandledException -= MainWindow_DispatcherUnhandledExceptionForTestLog;
                _sessionTestLog.Add("СЕССИЯ", "Программа закрывается; диагностический лог очищается без автосохранения");
                _sessionTestLog.Clear();
                _offlineAutoSaveTimer.Stop();
                foreach (var reserved in _offlineProjectLocks.Values)
                    try { reserved.Dispose(); } catch { /* process shutdown must not be blocked by cleanup */ }
                _offlineProjectLocks.Clear();
            };
            _templateEditor.ProjectSelectionRequested += ProjectSelectionRequested;
            _projectEditor.ProjectSelectionRequested += ProjectSelectionRequested;
            _projectEditor.OfflineProjectChanged += (_, _) => { if (_isOfflineWorkspace) _offlineProjectDirty = true; };
            _projectEditor.ChangeJournalRequested += ProjectEditor_ChangeJournalRequested;
            _templateEditor.DatabaseStructureChanged += TemplateEditor_DatabaseStructureChanged;
            _templateEditor.ActionLogged += TemplateEditor_ActionLogged;
            _templateEditor.LogicalRelationDeleted += payload => _pendingLogicalRelationRestore = payload;

            ThemeManager.InitializeDefault();
            _fieldsBuilder = new DisplayFieldsBuilder(_rightPanel.FieldsContainer);
            _actionLog.Changed += ActionLog_Changed;
            _changeJournal.Changed += ActionLog_Changed;

            WireUpEvents();
            // At process start there is deliberately no active identity. Do not evaluate
            // screen visibility or database permissions until LoginWindow succeeds.
            UserAuthorizationService.Changed += UserAuthorizationService_Changed;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            // До выбора источника данных Offline не активен: для него нужен открытый .cfgproj.
            _isOfflineWorkspace = false;
            OfflineModeButton.IsEnabled = true;
            _workspaceModeChosen = false;
            _rightPanel.DataTableGrid.AutoGeneratingColumn += DataTableGrid_AutoGeneratingColumn;
            UpdateWorkspaceModeUi();
            WorkspaceModeHint.Text = "";
            InitializeUnauthenticatedUi();
            Loaded += async (s, e) => await InitializeAsync();
        }

        private void UpdateWorkspaceModeUi()
        {
            if (OnlineModeButton == null || OfflineModeButton == null) return;

            // Offline can always be requested from Online; a project must be selected before its editor is unlocked.
            OfflineModeButton.IsEnabled = true;
            OnlineModeButton.Content = (_workspaceModeChosen && !_isOfflineWorkspace) ? "● ONLINE" : "ONLINE";
            OfflineModeButton.Content = (_workspaceModeChosen && _isOfflineWorkspace) ? "● OFFLINE" : "OFFLINE";
            OnlineModeButton.ToolTip = _isOfflineWorkspace
                ? "Перейти к актуальным данным SQL Server"
                : "Текущий режим Online";
            OfflineModeButton.ToolTip = _isOfflineWorkspace
                ? "Текущий режим Offline. Работа ведётся с локальным проектом."
                : "Выбрать локальный проект .cfgproj и перейти в Offline.";

            ApplyWorkspaceModeButtonStyle(OnlineModeButton, _workspaceModeChosen && !_isOfflineWorkspace, true);
            ApplyWorkspaceModeButtonStyle(OfflineModeButton, _workspaceModeChosen && _isOfflineWorkspace, false);
            // File actions are source-specific: Online creates/opens a snapshot; Offline only saves
            // the already-open package. Never let an Offline user accidentally export or replace it.
            if (MenuExportProject != null) MenuExportProject.IsEnabled = !_isOfflineWorkspace;
            if (MenuSaveLocalCopy != null) MenuSaveLocalCopy.IsEnabled = !_isOfflineWorkspace;
            if (MenuCloseOfflineProject != null) MenuCloseOfflineProject.IsEnabled = _isOfflineWorkspace && _openedLocalProject != null;
            if (MenuDatabase != null) MenuDatabase.IsEnabled = !_isOfflineWorkspace && !_offlineSelectionRequired && _currentMode == 1;
            if (_leftPanel != null)
                _leftPanel.SetAddDbButtonText(_isOfflineWorkspace ? "＋  Добавить проект" : "＋  Добавить базу");
            if (OnlineModeBadge != null) OnlineModeBadge.Visibility = Visibility.Collapsed;
            if (WorkspaceModeHint != null) WorkspaceModeHint.Text = "";
            if (WorkspaceSelectionOverlay != null)
                WorkspaceSelectionOverlay.Visibility = _offlineSelectionRequired ? Visibility.Visible : Visibility.Collapsed;
            if (_offlineSelectionRequired)
            {
                EditorModeView.Visibility = Visibility.Collapsed;
                Mode2View.Visibility = Visibility.Collapsed;
                Mode3View.Visibility = Visibility.Collapsed;
                NavDatabase.IsEnabled = NavTemplates.IsEnabled = NavProject.IsEnabled = NavFbEditor.IsEnabled = false;
                MenuDatabase.IsEnabled = false;
                CurrentProjectText.IsEnabled = false;
            }
        }

        private static void ApplyWorkspaceModeButtonStyle(Button button, bool selected, bool onlineMode)
        {
            if (selected && onlineMode)
            {
                button.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 245, 229));
                button.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 105, 61));
                button.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(47, 158, 99));
                button.BorderThickness = new Thickness(1.5);
                button.FontWeight = FontWeights.Bold;
            }
            else if (selected)
            {
                // Online and Offline use the same positive selected-state language.
                button.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 245, 229));
                button.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 105, 61));
                button.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(47, 158, 99));
                button.BorderThickness = new Thickness(1.5);
                button.FontWeight = FontWeights.Bold;
            }
            else
            {
                // Keep the inactive fill explicit: clearing local values lets the global
                // button template/hover trigger override the mode-state color unpredictably.
                button.Background = System.Windows.Media.Brushes.White;
                button.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(65, 76, 89));
                button.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(205, 213, 222));
                button.BorderThickness = new Thickness(1);
                button.FontWeight = FontWeights.SemiBold;
            }
        }

        private void DataTableGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            var sourceLabel = "";
            e.Column.Header = sourceLabel + e.PropertyName;

            if (e.Column is DataGridTextColumn textColumn)
            {
                var textStyle = new Style(typeof(TextBlock));
                textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0)));
                textStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
                textColumn.ElementStyle = textStyle;
            }
            else if (e.Column is DataGridCheckBoxColumn checkBoxColumn)
            {
                var checkBoxStyle = new Style(typeof(CheckBox));
                checkBoxStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0)));
                checkBoxStyle.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left));
                checkBoxColumn.ElementStyle = checkBoxStyle;
            }
        }

        private async Task InitializeAsync()
        {
            _rightPanel.TableTitle.Text = "Авторизуйтесь для начала работы";
            try
            {
                _connection.LoadFromFile();
            }
            catch (Exception ex)
            {
                Configurator.Infrastructure.ModernDialog.Show($"Внимание: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}\n\nНастройки будут созданы заново.",
                    "Загрузка настроек", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // MainWindow is already visible at this point. Keep CurrentUser empty and
            // show authentication before any database connectivity or mode-visibility checks.
            if (UserAuthorizationService.CurrentUser == null)
            {
                var login = new LoginWindow { Owner = this };
                _loginDialogOpen = true;
                bool authenticated;
                try { authenticated = login.ShowDialog() == true; }
                finally { _loginDialogOpen = false; }

                if (!authenticated)
                {
                    _startupInitializationPending = true;
                    StatusText.Text = "Не авторизован. Войдите через кнопку пользователя, чтобы продолжить.";
                    RefreshActionLogUi();
                    return;
                }
            }

            await ContinueStartupAfterAuthorizationAsync();
        }

        private void InitializeUnauthenticatedUi()
        {
            _workspaceModeChosen = false;
            _isOfflineWorkspace = false;
            _offlineSelectionRequired = true;
            if (CurrentUserText != null)
            {
                CurrentUserText.Content = "Не авторизован";
                CurrentUserText.ToolTip = "Нажмите, чтобы войти";
            }

            // Hide all mode entry points without calling PermissionPolicy for a null user.
            foreach (var button in new[] { NavDatabase, NavTemplates, NavProject, NavFbEditor })
            {
                button.Visibility = Visibility.Collapsed;
                button.IsEnabled = false;
            }
            EditorModeView.Visibility = Visibility.Collapsed;
            Mode2View.Visibility = Visibility.Collapsed;
            Mode3View.Visibility = Visibility.Collapsed;
            Mode4View.Visibility = Visibility.Collapsed;
            WorkspaceSelectionOverlay.Visibility = Visibility.Collapsed;
            MenuDatabase.IsEnabled = false;
            MenuRemoveDb.IsEnabled = false;
            MenuExportProject.IsEnabled = false;
            MenuSaveLocalCopy.IsEnabled = false;
            OnlineModeButton.IsEnabled = false;
            OfflineModeButton.IsEnabled = false;
            CurrentProjectText.IsEnabled = false;
            StatusText.Text = "Ожидание авторизации";
        }

        private async Task ContinueStartupAfterAuthorizationAsync()
        {
            if (_startupInitialized || UserAuthorizationService.CurrentUser == null)
                return;

            _startupInitializationPending = false;
            _offlineSelectionRequired = false;
            OnlineModeButton.IsEnabled = true;
            OfflineModeButton.IsEnabled = true;
            CurrentProjectText.IsEnabled = true;
            // Apply visibility/permission checks now that the identity exists, but defer
            // mode activation until the user has chosen a workspace. This avoids opening
            // project-selection dialogs ahead of the startup workspace dialog.
            ApplyAuthorizationUi(allowModeActivation: false);

            var startupDialog = new WorkspaceStartupDialog { Owner = this };
            startupDialog.ShowDialog();
            var choice = startupDialog.SelectedAction;

            // The database connectivity checks are intentionally after successful login
            // and only occur for an explicit Online choice.
            if (choice != "open")
                ShowKnownDatabasesLoading();

            if (choice == "open")
            {
                _workspaceModeChosen = false;
                bool opened = await OpenLocalProjectAsync();
                if (!opened)
                {
                    _isOfflineWorkspace = false;
                    _offlineSelectionRequired = true;
                    UpdateWorkspaceModeUi();
                    StatusText.Text = "Выберите локальный проект или явно перейдите в Online";
                }
            }
            else
            {
                _workspaceModeChosen = true;
                _isOfflineWorkspace = false;
                UpdateWorkspaceModeUi();
                WorkspaceModeHint.Text = "";
                await TryReconnectDatabasesAsync();
            }

            if (!_isOfflineWorkspace)
            {
                RebuildTree();
                UpdateAddDbButtonState();
                _rightPanel.TableTitle.Text = "Выберите таблицу слева";
            }

            // Only after the login dialog has closed and the workspace choice is known,
            // activate the first mode the authenticated identity may actually enter.
            // This replaces the old constructor-time ActivateMode(1) call.
            if (!_offlineSelectionRequired)
            {
                int initialMode = new[] { 1, 3, 2, 4 }.FirstOrDefault(candidate =>
                    PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, candidate));
                if (initialMode != 0)
                {
                    _sessionTestLog.Add("СТАРТ", $"После авторизации активируется режим {initialMode}; пользователь={UserAuthorizationService.CurrentUser?.UserName ?? "<null>"}");
                    ActivateMode(initialMode);
                }
                else
                {
                    _sessionTestLog.Add("ПРАВА", "Авторизация успешна, но роли не разрешён вход ни в один режим");
                    StatusText.Text = "У пользователя нет доступа ни к одному режиму. Выберите другого пользователя или обратитесь к администратору.";
                }
            }

            ApplyAuthorizationUi();
            if (StatusText != null && !_offlineSelectionRequired &&
                !StatusText.Text.StartsWith("У пользователя нет доступа", StringComparison.Ordinal))
                StatusText.Text = _isOfflineWorkspace ? "Offline-проект открыт" : "Готово";
            RefreshActionLogUi();
            _startupInitialized = true;
        }

        private async Task TryReconnectDatabasesAsync()
        {
            _connection.ConnectedDatabases.Clear();
            _failedDatabases.Clear();
            _reconnectCts?.Cancel();
            _dataLoadCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
            var ct = _reconnectCts.Token;
            var databases = _connection.KnownDatabases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var checks = databases.Select(async dbName =>
            {
                bool connected = await _connection.TestConnectionAsync(dbName);
                return (dbName, connected);
            }).ToArray();
            var results = await Task.WhenAll(checks);
            if (ct.IsCancellationRequested) return;
            foreach (var result in results)
            {
                _connection.ConnectedDatabases.Add(result.dbName);
                if (!result.connected) _failedDatabases.Add(result.dbName);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _reconnectCts?.Cancel();
            _dataLoadCts?.Cancel();
            _reconnectCts?.Dispose();
            _dataLoadCts?.Dispose();
            try { _connection.SaveToFile(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OnClosed] Save failed: {ex.Message}"); }
            try { _sessionTestLog?.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OnClosed] Diagnostic log finalization failed: {ex.Message}"); }
            global::System.Windows.Application.Current.Shutdown();
            base.OnClosed(e);
        }

        private void WireUpEvents()
        {
            _leftPanel.AddDatabaseClicked += async () => { if (_isOfflineWorkspace) await OpenLocalProjectAsync(); else BtnOpenConnection_Click(null, null); };
            _rightPanel.BtnAdd.Click += BtnAdd_Click;
            _rightPanel.BtnUpdate.Click += BtnUpdate_Click;
            _rightPanel.BtnDuplicate.Click += BtnDuplicate_Click;
            _rightPanel.BtnDelete.Click += BtnDelete_Click;
            _rightPanel.BtnToggleFields.Click += BtnToggleFields_Click;
            _rightPanel.BtnFilter.Click += BtnFilter_Click;
            _rightPanel.BtnFirst.Click += BtnFirst_Click;
            _rightPanel.BtnPrev.Click += BtnPrev_Click;
            _rightPanel.BtnNext.Click += BtnNext_Click;
            _rightPanel.BtnLast.Click += BtnLast_Click;
            _rightPanel.DataTableGrid.SelectionChanged += DataTableGrid_SelectionChanged;
            _rightPanel.DataTableGrid.MouseLeftButtonUp += DataTableGrid_MouseLeftButtonUp;
            _rightPanel.DataTableGrid.PreviewMouseLeftButtonDown += DataTableGrid_PreviewMouseLeftButtonDown;
            _rightPanel.DataTableGrid.MouseDoubleClick += DataTableGrid_MouseDoubleClick;
            _rightPanel.DataTableGrid.CurrentCellChanged += (s, e) => QueueSelectionStateUpdate();
            SetFieldsPanelExpanded(false, false);
            if (_leftPanel.ServerTree != null)
                _leftPanel.ServerTree.SelectedItemChanged += (s, e) => UpdateRemoveDbMenuState();
            // Do not call ActivateMode here: WireUpEvents runs in the constructor,
            // before InitializeAsync displays LoginWindow. Permission checks at this
            // stage see CurrentUser == null and used to show a false access-denied error.
        }


        private async void TemplateEditor_DatabaseStructureChanged(object sender, EventArgs e)
        {
            if (_isOfflineWorkspace) _offlineProjectDirty = true;
            var dbName = _selectedDbName;
            if (string.IsNullOrWhiteSpace(dbName)) return;

            // A schema change initiated from Mode 2 is a local, known change.
            // Refresh Mode 1 immediately instead of waiting for the 15-second schema poll.
            if (_leftPanel.ServerTree != null)
            {
                var dbItem = _leftPanel.ServerTree.Items.OfType<TreeViewItem>()
                    .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), dbName, StringComparison.OrdinalIgnoreCase));
                if (dbItem != null && dbItem.IsExpanded)
                {
                    _loadedTreeNodes.Remove(dbName);
                    await OnDatabaseExpandedAndKeepStateAsync(dbName, dbItem);
                }
            }

            // If the currently opened Mode 1 table was physically dropped, clear its
            // data/editor immediately as well. Otherwise the right side can keep showing
            // a table that no longer exists until the next polling cycle.
            if (_activeVm != null && string.Equals(_activeDbName, dbName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_activeVm.CurrentTable))
            {
                try
                {
                    var tables = await _activeVm.GetTableListAsync();
                    var exists = tables.Rows.Cast<DataRow>().Any(r =>
                        string.Equals($"{Convert.ToString(r[0])}.{Convert.ToString(r[1])}", _activeVm.CurrentTable, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                        ClearActiveTable();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StructureChange] active table check failed: {ex.Message}");
                }
            }

            // Mode 3 gets the same class/table immediately without requiring a mode switch.
            _projectEditor?.SetProjectDatabase(dbName);
        }

        private async Task OnDatabaseExpandedAndKeepStateAsync(string dbName, TreeViewItem dbItem)
        {
            dbItem.Items.Clear();
            await Task.Yield();
            OnDatabaseExpanded(dbName, dbItem);
        }

        private void UpdateRemoveDbMenuState()
        {
            var menuItem = this.FindName("MenuRemoveDb") as MenuItem;
            if (menuItem != null) menuItem.IsEnabled = _leftPanel.ServerTree?.SelectedItem != null;
        }

        private void UpdateAddDbButtonState()
        {
            _leftPanel.SetAddDbButtonEnabled(true);
            if (_connection.ConnectedDatabases.Count > 0) _leftPanel.HidePlaceholder();
            else _leftPanel.ShowPlaceholder();
        }

        private void ApplyAuthorizationUi(bool allowModeActivation = true)
        {
            bool canAdd = UserAuthorizationService.Has(Permission.Mode1Add);
            bool canEdit = UserAuthorizationService.Has(Permission.Mode1Edit);
            bool canDelete = UserAuthorizationService.Has(Permission.Mode1Delete);
            bool canDb = UserAuthorizationService.Has(Permission.ManageDatabases);
            bool canText = UserAuthorizationService.Has(Permission.ManageTextLists);
            bool canExport = UserAuthorizationService.Has(Permission.ExportData);
            bool canSql = UserAuthorizationService.Has(Permission.FreeSql);
            bool canJournalRestore = UserAuthorizationService.Has(Permission.RestoreJournal);
            if (_rightPanel != null) { bool tableMode = _currentMode == 1 && UserAuthorizationService.Has(Permission.Mode1View); bool hasTable = _isOfflineWorkspace ? _offlineCurrentDataTable != null : _activeVm != null; _rightPanel.BtnAdd.IsEnabled = tableMode && canAdd && hasTable; _rightPanel.BtnUpdate.IsEnabled = tableMode && canEdit && hasTable && _rightPanel.DataTableGrid?.SelectedItem != null; _rightPanel.BtnDuplicate.IsEnabled = tableMode && canAdd && hasTable && _rightPanel.DataTableGrid?.SelectedItem != null; _rightPanel.BtnDelete.IsEnabled = tableMode && canDelete && hasTable && _rightPanel.DataTableGrid?.SelectedItems.Count > 0; }
            if (MenuRemoveDb != null) MenuRemoveDb.IsEnabled = canDb && _leftPanel?.ServerTree?.SelectedItem != null;
            UpdateCurrentProjectText();
            if (CurrentUserText != null)
            {
                var user = UserAuthorizationService.CurrentUser;
                CurrentUserText.Content = user == null
                    ? "—"
                    : $"{user.DisplayName} · {user.RoleName}";
                CurrentUserText.ToolTip = user == null ? null : $"Логин: {user.UserName} · Роль: {user.RoleName}";
            }
            if (MenuDatabase != null) MenuDatabase.IsEnabled = !_isOfflineWorkspace && !_offlineSelectionRequired && _currentMode == 1;
            // Visibility permissions are independent of viewing/editing permissions.
            if (NavDatabase != null) NavDatabase.Visibility = PermissionPolicy.CanShowMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 1) ? Visibility.Visible : Visibility.Collapsed;
            if (NavTemplates != null) NavTemplates.Visibility = PermissionPolicy.CanShowMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 2) ? Visibility.Visible : Visibility.Collapsed;
            if (NavProject != null) NavProject.Visibility = PermissionPolicy.CanShowMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 3) ? Visibility.Visible : Visibility.Collapsed;
            if (NavFbEditor != null) NavFbEditor.Visibility = PermissionPolicy.CanShowMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 4) ? Visibility.Visible : Visibility.Collapsed;
            bool navigationUnlocked = !_offlineSelectionRequired;
            if (NavDatabase != null) NavDatabase.IsEnabled = navigationUnlocked && PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 1);
            if (NavTemplates != null) NavTemplates.IsEnabled = navigationUnlocked && PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 2);
            if (NavProject != null) NavProject.IsEnabled = navigationUnlocked && PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 3);
            if (NavFbEditor != null) NavFbEditor.IsEnabled = navigationUnlocked && PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 4);
            if (ModeNavigation != null)
            {
                var visibleModes = new[] { NavDatabase, NavTemplates, NavProject, NavFbEditor }.Where(b => b.Visibility == Visibility.Visible).ToArray();
                var columns = new[] { NavCol1, NavCol2, NavCol3, NavCol4 };
                foreach (var column in columns) column.Width = new GridLength(0);
                for (int i = 0; i < visibleModes.Length; i++)
                {
                    Grid.SetColumn(visibleModes[i], i);
                    columns[i].Width = new GridLength(1, GridUnitType.Star);
                }
                _sessionTestLog.Add("ПРАВА", $"Видимость: Mode1Show={UserAuthorizationService.Has(Permission.Mode1Show)}, Mode2Show={UserAuthorizationService.Has(Permission.Mode2Show)}, Mode2GraphShow={UserAuthorizationService.Has(Permission.Mode2GraphShow)}, Mode2RelationsShow={UserAuthorizationService.Has(Permission.Mode2RelationsShow)}, Mode2FieldsShow={UserAuthorizationService.Has(Permission.Mode2FieldsShow)}, Mode3Show={UserAuthorizationService.Has(Permission.Mode3Show)}, Mode4Show={UserAuthorizationService.Has(Permission.Mode4Show)}; отображаются: {string.Join(", ", visibleModes.Select(b => b.Content?.ToString()))}");
            }
            if (allowModeActivation && !PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, _currentMode))
            {
                int fallback = new[] { 3, 2, 1, 4 }.FirstOrDefault(candidate => PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, candidate));
                if (fallback == 0) fallback = 4;
                ActivateMode(fallback);
            }
            if (_templateEditor != null) _templateEditor.IsEnabled = true;
        }

        private void MainWindow_PreviewMouseDownForTestLog(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var control = source as FrameworkElement ?? FindVisualParent<FrameworkElement>(source);
            if (control == null) return;
            string name = string.IsNullOrWhiteSpace(control.Name) ? control.GetType().Name : control.Name;
            string caption = control switch
            {
                Button button => button.Content?.ToString(),
                MenuItem menu => menu.Header?.ToString(),
                TabItem tab => tab.Header?.ToString(),
                _ => null
            };
            _sessionTestLog.Add("ВЗАИМОДЕЙСТВИЕ", $"Нажат элемент {name}" + (string.IsNullOrWhiteSpace(caption) ? "" : $" ({caption})"));
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = child;
            while (current != null)
            {
                if (current is T result) return result;
                if (current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D)
                    current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                else if (current is FrameworkContentElement content)
                    current = content.Parent;
                else
                    return null;
            }
            return null;
        }

        private void MainWindow_DispatcherUnhandledExceptionForTestLog(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _sessionTestLog.Add("НЕОБРАБОТАННАЯ ОШИБКА", $"{e.Exception.GetType().FullName}: {e.Exception.Message}");
        }

        private void MenuExportSessionLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _sessionTestLog.Add("ЭКСПОРТ ЛОГА", "Пользователь запросил формирование диагностического файла");
                var dialog = new SaveFileDialog
                {
                    Title = "Сформировать лог файл тестирования",
                    Filter = "Текстовый файл (*.txt)|*.txt",
                    FileName = $"Configurator_TestLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DefaultExt = ".txt",
                    AddExtension = true,
                    OverwritePrompt = true
                };
                if (dialog.ShowDialog(this) == true)
                {
                    _sessionTestLog.Export(dialog.FileName);
                    MessageBox.Show(this, $"Лог сохранён:\n{dialog.FileName}", "Лог тестирования", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else _sessionTestLog.Add("ЭКСПОРТ ЛОГА", "Пользователь отменил сохранение файла");
            }
            catch (Exception ex)
            {
                _sessionTestLog.Add("ОШИБКА ЭКСПОРТА ЛОГА", $"{ex.GetType().Name}: {ex.Message}");
                MessageBox.Show(this, $"Не удалось сохранить лог файл.\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UserAuthorizationService_Changed(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) CloseCompareWindow();
            else Dispatcher.BeginInvoke(new Action(CloseCompareWindow));

            // Login()/EnterGuest() raises Changed synchronously before LoginWindow sets
            // DialogResult and closes. Applying permissions here can call ActivateMode(),
            // which may show ProjectSelectionWindow on top of the still-open login dialog.
            if (_loginDialogOpen)
            {
                _sessionTestLog.Add("АВТОРИЗАЦИЯ", "Обновление интерфейса отложено до закрытия окна авторизации");
                return;
            }

            if (Dispatcher.CheckAccess()) ApplyAuthorizationUi();
            else Dispatcher.BeginInvoke(new Action(() => ApplyAuthorizationUi()));
        }

        private async void MenuSwitchUser_Click(object sender, RoutedEventArgs e)
        {
            if (_loginDialogOpen) return;

            var login = new LoginWindow { Owner = this };
            bool authenticated = false;
            _loginDialogOpen = true;
            try
            {
                authenticated = login.ShowDialog() == true;
            }
            finally
            {
                _loginDialogOpen = false;
            }

            // The login modal has now fully returned; mode fallback/project selection is safe.
            if (authenticated)
            {
                if (_startupInitializationPending)
                    await ContinueStartupAfterAuthorizationAsync();
                else
                    ApplyAuthorizationUi();
            }
        }

        private void MenuViewTheme_Click(object sender, RoutedEventArgs e)
        {
            var picker = new ThemePickerWindow { Owner = this };
            picker.ShowDialog();
        }

        private void BtnOpenConnection_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.ManageDatabases)) return;
            var window = new ConnectionWindow { Owner = this };
            window.OnDatabaseAdded = (dbName) =>
            {
                string newServer = window.Settings.Server;
                if (!string.Equals(newServer, _connection.Server, StringComparison.OrdinalIgnoreCase) ||
                    _connection.User != window.Settings.User || _connection.Password != window.Settings.Password)
                    CloseCompareWindow();
                if (!string.IsNullOrEmpty(_connection.Server) && !newServer.Equals(_connection.Server, StringComparison.OrdinalIgnoreCase))
                {
                    _vms.Clear(); _loadedTreeNodes.Clear(); _connection.ConnectedDatabases.Clear(); _connection.KnownDatabases.Clear(); _failedDatabases.Clear(); ClearActiveTable();
                }
                _connection.Server = newServer;
                _connection.User = window.Settings.User;
                _connection.Password = window.Settings.Password;
                if (_connection.ConnectedDatabases.Any(db => db.Equals(dbName, StringComparison.OrdinalIgnoreCase))) return false;
                _connection.ConnectedDatabases.Add(dbName);
                _selectedDbName = dbName;
                UpdateCurrentProjectText();
                if (!_connection.KnownDatabases.Any(db => db.Equals(dbName, StringComparison.OrdinalIgnoreCase))) _connection.KnownDatabases.Add(dbName);
                _failedDatabases.Remove(dbName);
                RebuildTree();
                UpdateAddDbButtonState();
                _templateEditor?.Configure(_connection, GetActiveDatabases(), dbName);
                _projectEditor?.Configure(_connection, GetActiveDatabases(), dbName);
                try { _connection.SaveToFile(); }
                catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show($"Не удалось сохранить настройки: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning); }
                return true;
            };
            window.ShowDialog();
        }

        private void BtnRemoveDb_Click(object sender, RoutedEventArgs e) { if (!UserAuthorizationService.Require(Permission.ManageDatabases)) return; RemoveSelectedDatabase(); UpdateAddDbButtonState(); ApplyAuthorizationUi(); }
        private void BtnToggleFields_Click(object sender, RoutedEventArgs e) { SetFieldsPanelExpanded(!_fieldsPanelExpanded, true); }

        private void BtnFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_activeVm == null || _activeVm.Columns == null || _activeVm.Columns.Count == 0) return;
            var window = new TableFilterWindow(_activeVm.Columns, _activeVm.Filter) { Owner = this };
            if (window.ShowDialog() != true) return;
            _activeVm.SetFilter(window.Result);
            UpdateFilterButtonState();
            _ = LoadDataAsync();
        }

        private void UpdateFilterButtonState()
        {
            if (_rightPanel?.BtnFilter == null) return;
            bool active = _activeVm?.Filter?.IsActive == true;
            _rightPanel.BtnFilter.Content = active ? "⌕  Фильтр •" : "⌕  Фильтр";
            _rightPanel.BtnFilter.ToolTip = active ? $"Фильтр: {_activeVm.Filter.Column} {_activeVm.Filter.Operator}" : "Фильтр текущей таблицы";
        }

        private void SetFieldsPanelExpanded(bool expanded, bool syncMenu)
        {
            _fieldsPanelExpanded = expanded;
            if (_rightPanel.InputPanel != null)
            {
                _rightPanel.InputPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                _rightPanel.InputPanel.UpdateLayout();
                Dispatcher.BeginInvoke(new Action(() => _rightPanel.InputPanel.UpdateLayout()), DispatcherPriority.Loaded);
            }
            if (_rightPanel.BtnToggleFields != null)
            {
                _rightPanel.BtnToggleFields.Content = expanded ? "Поля записи  ▲" : "Поля записи  ▼";
                _rightPanel.BtnToggleFields.ToolTip = expanded ? "Свернуть поля записи" : "Развернуть поля записи";
            }
        }

        private List<string> GetActiveDatabases() => _connection.ConnectedDatabases.Where(db => !_failedDatabases.Contains(db)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private async Task RefreshDatabasesAndSchemaAsync()
        {
            try
            {
                await TryReconnectDatabasesAsync();
                _vms.Clear(); _loadedTreeNodes.Clear(); RebuildTree(); UpdateAddDbButtonState();
                if (!string.IsNullOrWhiteSpace(_selectedDbName) && !_failedDatabases.Contains(_selectedDbName))
                {
                    _templateEditor?.Configure(_connection, GetActiveDatabases(), _selectedDbName);
                    _projectEditor?.Configure(_connection, GetActiveDatabases(), _selectedDbName);
                }
                else
                {
                    string active = GetActiveDatabases().FirstOrDefault() ?? "";
                    _selectedDbName = active;
                    UpdateCurrentProjectText();
                    _templateEditor?.Configure(_connection, GetActiveDatabases(), active);
                    _projectEditor?.Configure(_connection, GetActiveDatabases(), active);
                }
                ClearActiveTable();
            }
            catch (Exception ex)
            {
                Configurator.Infrastructure.ModernDialog.Show($"Не удалось обновить структуру БД: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", "Обновление БД", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ActivateMode(int mode)
        {
            _sessionTestLog.Add("РЕЖИМ", $"Запрошено переключение в режим {mode}; текущий режим до переключения {_currentMode}");
            // Defense in depth: a navigation event must never run permission checks against
            // an empty identity during startup or while the login dialog is still pending.
            if (UserAuthorizationService.CurrentUser == null)
            {
                _sessionTestLog.Add("АВТОРИЗАЦИЯ", $"Переключение в режим {mode} пропущено: пользователь ещё не авторизован");
                return;
            }
            Permission? requiredView = mode switch { 1 => Permission.Mode1View, 2 => Permission.Mode2View, 3 => Permission.Mode3View, 4 => Permission.Mode4View, _ => Permission.Mode1View };
            // Offline is a complete workspace, not a temporary Mode 1-only state.
            // Mode 2 and Mode 3 are configured from the opened .cfgproj below and must
            // never wait for or test an Online SQL connection.
            if (requiredView.HasValue && !UserAuthorizationService.Has(requiredView.Value))
            {
                UserAuthorizationService.Require(requiredView.Value);
                return;
            }
            if (!PermissionPolicy.CanShowMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, mode)) return;

            // The first entry into each project mode always requires an explicit project choice,
            // even if Mode 1 already has a database selected.
            if (!_isOfflineWorkspace && mode == 2 && !_mode2ProjectSelectedOnEntry)
            {
                // During authorization/startup a permitted mode may be the only available
                // destination even when no SQL database is connected yet. Enter the empty
                // mode without showing a modal warning; project selection can be retried
                // explicitly after a database is connected.
                if (GetActiveDatabases().Count > 0)
                {
                    if (!SelectProjectForMode()) return;
                    _mode2ProjectSelectedOnEntry = true;
                }
            }
            else if (!_isOfflineWorkspace && mode == 3 && !_mode3ProjectSelectedOnEntry)
            {
                if (GetActiveDatabases().Count > 0)
                {
                    if (!SelectProjectForMode()) return;
                    _mode3ProjectSelectedOnEntry = true;
                }
            }

            // A table belongs only to Mode 1. Clear its binding before changing modes so
            // the previous table can never remain visible under another mode or Offline.
            if (mode != 1)
            {
                if (_isOfflineWorkspace) CommitOfflineGridToProject();
                ClearActiveTable();
                _offlineCurrentTableName = null;
                _offlineCurrentDataTable = null;
                _offlineCurrentColumns.Clear();
                _offlineRowRecordKeys.Clear();
                SetOfflineEditorButtons();
            }
            _currentMode = mode;
            // Editing is controlled only by explicit per-operation permissions, never by role name.
            if (_templateEditor != null) _templateEditor.IsEnabled = true;
            EditorModeView.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            Mode2View.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
            Mode3View.Visibility = mode == 3 ? Visibility.Visible : Visibility.Collapsed;
            Mode4View.Visibility = mode == 4 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = mode switch { 1 => "БД проекта", 2 => "Шаблоны проекта", 3 => "Объекты проекта", 4 => "Редактор FB-блоков (заблокирован)", _ => "БД проекта" };
            UpdateModeNavigation(mode);
            if (FindName("MenuDatabase") is MenuItem databaseMenu) databaseMenu.IsEnabled = mode == 1 && !_isOfflineWorkspace && !_offlineSelectionRequired;
            string availableProject = string.IsNullOrWhiteSpace(_selectedDbName) || _failedDatabases.Contains(_selectedDbName)
                ? string.Empty : _selectedDbName;
            ConfigureActiveProjectEditor(mode, availableProject);
            ApplyAuthorizationUi();
        }

        /// <summary>Bind the active editor to exactly one workspace source. This must be called
        /// both from ordinary mode navigation and from direct Online/Offline workspace switches.
        /// The latter previously changed only the shell flag, leaving Mode 3 bound to SQL.</summary>
        private void ConfigureActiveProjectEditor(int mode, string availableProject)
        {
            if (mode is not (2 or 3)) return;

            if (Configurator.Core.Workspace.WorkspaceSwitchPolicy.ShouldConfigureOfflineEditor(
                    _isOfflineWorkspace, _openedLocalProject != null, mode))
            {
                _projectDataSource.Set(new OfflineProjectDataSource(_openedLocalProject!));
                if (mode == 2) _templateEditor?.ConfigureOffline(_openedLocalProject!);
                else _projectEditor?.ConfigureOffline(_openedLocalProject!);
                SessionTestLog.Trace("WORKSPACE-SOURCE", $"editor-bound; mode={mode}; source=offline; file='{_openedLocalProjectPath}'; onlineProviderCalls=0");
                return;
            }

            _projectDataSource.Set(null);
            if (mode == 2) _templateEditor?.Configure(_connection, GetActiveDatabases(), availableProject);
            else _projectEditor?.Configure(_connection, GetActiveDatabases(), availableProject);
            SessionTestLog.Trace("WORKSPACE-SOURCE", $"editor-bound; mode={mode}; source=online; database='{availableProject}'");
        }

        private void UpdateModeNavigation(int mode)
        {
            NavDatabase.Style = (Style)FindResource(mode == 1 ? "BtnPrimary" : "BtnSecondary");
            NavTemplates.Style = (Style)FindResource(mode == 2 ? "BtnPrimary" : "BtnSecondary");
            NavProject.Style = (Style)FindResource(mode == 3 ? "BtnPrimary" : "BtnSecondary");
            NavFbEditor.Style = (Style)FindResource(mode == 4 ? "BtnPrimary" : "BtnSecondary");
        }

        private void ProjectSelectionRequested(object sender, EventArgs e)
        {
            SelectProjectForMode();
        }

        private bool SelectProjectForMode()
        {
            while (true)
            {
                var databases = GetActiveDatabases();
                if (databases.Count == 0)
                {
                    Configurator.Infrastructure.ModernDialog.Show("Сначала подключите хотя бы одну базу данных, затем выберите проект.", "Выбор проекта", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                var window = new ProjectSelectionWindow(databases, _selectedDbName) { Owner = this };
                bool selected = window.ShowDialog() == true;
                if (selected)
                {
                    if (string.IsNullOrWhiteSpace(window.SelectedDatabase)) return false;
                    _selectedDbName = window.SelectedDatabase;
                    UpdateCurrentProjectText();
                    // Modes 2 and 3 share one project context: selecting a project initializes both.
                    _mode2ProjectSelectedOnEntry = true;
                    _mode3ProjectSelectedOnEntry = true;
                    _templateEditor.SetProjectDatabase(_selectedDbName);
                    _projectEditor.SetProjectDatabase(_selectedDbName);
                    return true;
                }
                if (!window.ConnectNewRequested) return false;
                BtnOpenConnection_Click(this, new RoutedEventArgs());
                if (string.IsNullOrWhiteSpace(_selectedDbName) || !GetActiveDatabases().Any(x => x.Equals(_selectedDbName, StringComparison.OrdinalIgnoreCase)))
                    _selectedDbName = GetActiveDatabases().FirstOrDefault() ?? _selectedDbName;
                UpdateCurrentProjectText();
            }
        }

        private void UpdateCurrentProjectText()
        {
            if (CurrentProjectText == null) return;
            if (_isOfflineWorkspace && _openedLocalProject != null)
            {
                CurrentProjectText.Content = _openedLocalProject.Manifest.Name + " · Offline";
                CurrentProjectText.ToolTip = "Текущий локальный проект. Изменения сохраняются в .cfgproj и не отправляются в SQL Server.";
                RecordSessionContext($"{_openedLocalProject.Manifest.Name} [id:{_openedLocalProject.Manifest.ProjectId}]", "Offline");
                return;
            }
            // A remembered database name is not proof that a project is active.
            // Show it only while the selected database belongs to the current Online session.
            bool selectedOnlineProjectIsActive = !_offlineSelectionRequired
                && _connection != null
                && !string.IsNullOrWhiteSpace(_selectedDbName)
                && _connection.ConnectedDatabases.Any(db => db.Equals(_selectedDbName, StringComparison.OrdinalIgnoreCase));
            string activeProjectName = selectedOnlineProjectIsActive ? _selectedDbName : string.Empty;
            CurrentProjectText.Content = string.IsNullOrWhiteSpace(activeProjectName) ? "не выбран" : activeProjectName;
            CurrentProjectText.ToolTip = string.IsNullOrWhiteSpace(activeProjectName) ? "Нажмите, чтобы выбрать проект" : $"Текущий проект: {activeProjectName}. Нажмите, чтобы сменить.";
            string projectIdentity = selectedOnlineProjectIsActive ? $"{_connection.Server}/{activeProjectName}" : activeProjectName;
            RecordSessionContext(projectIdentity, _workspaceModeChosen ? "Online" : "не выбран");
        }

        private void RecordSessionContext(string project, string source) =>
            _sessionTestLog.RecordContext(UserAuthorizationService.CurrentUser?.UserName, project, source);

        private void FooterProject_Click(object sender, RoutedEventArgs e)
        {
            if (_isOfflineWorkspace)
            {
                ShowOfflineProjectMenu();
                return;
            }
            SelectProjectForMode();
            UpdateCurrentProjectText();
        }

        private void ShowOfflineProjectMenu()
        {
            var menu = new ContextMenu();
            var heading = new MenuItem { Header = "Открытые Offline-проекты", IsEnabled = false };
            menu.Items.Add(heading);
            menu.Items.Add(new Separator());

            foreach (var entry in _offlineProjects.OrderBy(x => x.Value.Manifest.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string path = entry.Key;
                string name = string.IsNullOrWhiteSpace(entry.Value.Manifest.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(path)
                    : entry.Value.Manifest.Name;
                var item = new MenuItem
                {
                    Header = string.Equals(path, _openedLocalProjectPath, StringComparison.OrdinalIgnoreCase) ? "✓  " + name : name,
                    ToolTip = path,
                    IsCheckable = false
                };
                item.Click += async (_, _) => await OpenLocalProjectAsync(path);
                menu.Items.Add(item);
            }

            if (_offlineProjects.Count == 0)
                menu.Items.Add(new MenuItem { Header = "Нет открытых проектов", IsEnabled = false });

            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = "＋  Открыть / добавить проект…" };
            add.Click += async (_, _) => await OpenLocalProjectAsync();
            menu.Items.Add(add);
            menu.PlacementTarget = CurrentProjectText;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void ShowOfflineProjectSwitcher()
        {
            // Kept as a compatibility entry point for older call sites; always use the
            // system file picker rather than maintaining a second project-selection UI.
            ChooseOfflineProject_Click(this, new RoutedEventArgs());
        }

        private void NavDatabase_Click(object sender, RoutedEventArgs e) => ActivateMode(1);
        private void NavTemplates_Click(object sender, RoutedEventArgs e) => ActivateMode(2);
        private void NavProject_Click(object sender, RoutedEventArgs e) => ActivateMode(3);
        private void NavFbEditor_Click(object sender, RoutedEventArgs e) => ActivateMode(4);
        private void MenuViewFullscreen_Click(object sender, RoutedEventArgs e) { WindowState = WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal; }

        private void MenuOptionsCheckFields_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.CheckTools)) return;
            var activeDatabases = GetActiveDatabases();
            if (activeDatabases.Count == 0) { Configurator.Infrastructure.ModernDialog.Show("Сначала подключите хотя бы одну активную базу данных.", "Проверить свободные поля", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var window = new FreeRecordCheckWindow(_connection, activeDatabases, _selectedDbName) { Owner = this }; window.ShowDialog();
        }

        private void MenuOptionsCheckAddresses_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.CheckTools)) return;
            var activeDatabases = GetActiveDatabases();
            if (activeDatabases.Count == 0) { Configurator.Infrastructure.ModernDialog.Show("Сначала подключите хотя бы одну активную базу данных.", "Проверить свободные адреса", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var window = new FreeAddressCheckWindow(_connection, activeDatabases, _selectedDbName) { Owner = this }; window.ShowDialog();
        }

        private void MenuOptionsCheckDuplicateAddresses_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.CheckTools)) return;
            var activeDatabases = GetActiveDatabases();
            if (activeDatabases.Count == 0) { Configurator.Infrastructure.ModernDialog.Show("Сначала подключите хотя бы одну активную базу данных.", "Проверить дублирование адресов", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var window = new DuplicateCheckWindow(_connection, activeDatabases, _selectedDbName, true) { Owner = this }; window.ShowDialog();
        }

        private void MenuOptionsCheckDuplicateRecords_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.CheckTools)) return;
            var activeDatabases = GetActiveDatabases();
            if (activeDatabases.Count == 0) { Configurator.Infrastructure.ModernDialog.Show("Сначала подключите хотя бы одну активную базу данных.", "Проверить дублирование Recordов", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var window = new DuplicateCheckWindow(_connection, activeDatabases, _selectedDbName, false) { Owner = this }; window.ShowDialog();
        }

        private void MenuDatabaseFreeSql_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.FreeSql)) return;
            if (_connection.ConnectedDatabases.Count == 0) { Configurator.Infrastructure.ModernDialog.Show("Сначала подключите хотя бы одну базу данных.", "Свободный ввод", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var window = new FreeSqlWindow(_connection, _selectedDbName) { Owner = this }; window.ShowDialog();
        }

        private void MenuHelpHotkeys_Click(object sender, RoutedEventArgs e) { var window = new HotkeysWindow { Owner = this }; window.ShowDialog(); }
        private void MenuHelpAbout_Click(object sender, RoutedEventArgs e)
        {
            Configurator.Infrastructure.ModernDialog.Show("Configurator\nФундамент Online/Offline · Переносимый проект · Основа безопасной синхронизации\n\nProgram/Matrix работают как расширяемые иерархии.", "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void MenuFileExportProject_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.ExportData)) return;
            string database = !string.IsNullOrWhiteSpace(_selectedDbName) ? _selectedDbName : _connection.ConnectedDatabases.FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(database)) { Configurator.Infrastructure.ModernDialog.Show("Сначала подключите и выберите базу проекта.", "Экспорт проекта", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Экспорт базы проекта", Filter = "SQL Server backup (*.bak)|*.bak|Все файлы (*.*)|*.*", DefaultExt = ".bak", AddExtension = true, FileName = database + ".bak" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                StatusText.Text = $"Экспорт проекта: {database}...";
                var db = new DatabaseService(_connection.ToConnectionString(database));
                await db.BackupDatabaseAsync(database, dialog.FileName);
                Configurator.Infrastructure.ModernDialog.Show("Резервная копия базы проекта создана.", "Экспорт проекта", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { StatusText.Text = "Ошибка экспорта проекта"; Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Экспорт проекта", MessageBoxButton.OK, MessageBoxImage.Error); }
        }


        private void MenuFileSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.ManageTextLists)) return;
            var activeDatabases = GetActiveDatabases();
            if (activeDatabases.Count == 0) { Configurator.Infrastructure.ModernDialog.Show("Сначала подключите хотя бы одну активную базу данных.", "Текстовые списки", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var window = new LookupSettingsWindow(_connection, activeDatabases, _selectedDbName) { Owner = this }; window.ShowDialog();
        }

        private void MenuFileExit_Click(object sender, RoutedEventArgs e) => Close();
        private async void MenuHelpLookupSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!UserAuthorizationService.Require(Permission.ManageTextLists)) return;
            Configurator.Core.Projects.ConfiguratorProject project = _openedLocalProject;
            string projectPath = _openedLocalProjectPath;
            if (project == null)
            {
                var pick = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Выберите .cfgproj, для которого настраиваются справочники",
                    Filter = "Локальный проект Configurator (*.cfgproj)|*.cfgproj",
                    CheckFileExists = true, Multiselect = false
                };
                if (pick.ShowDialog(this) != true) return;
                projectPath = pick.FileName;
                try { project = await _projectPackageService.OpenAsync(projectPath); }
                catch (Exception ex)
                {
                    Configurator.Infrastructure.ModernDialog.Show(this, Configurator.Infrastructure.UserErrorMessage.Format(ex), "Не удалось открыть .cfgproj", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            var window = new LookupGenerationSettingsWindow(project) { Owner = this };
            if (window.ShowDialog() != true) return;
            if (_isOfflineWorkspace && ReferenceEquals(project, _openedLocalProject))
            {
                _offlineProjectDirty = true;
                _offlineGridDirty = true;
                Configurator.Core.Architecture.LookupGenerationSettingsRuntime.Current = Configurator.Infrastructure.Projects.LookupGenerationSettingsStore.Load(project);
                StatusText.Text = "Настройки генерации справочников изменены · сохраняются в .cfgproj";
            }
            else
            {
                try
                {
                    await _projectPackageService.SaveAsync(projectPath, project, requireExisting: true);
                    Configurator.Core.Architecture.LookupGenerationSettingsRuntime.Current = Configurator.Infrastructure.Projects.LookupGenerationSettingsStore.Load(project);
                    StatusText.Text = "Настройки справочников сохранены в выбранный .cfgproj и применены для Online";
                }
                catch (Exception ex)
                {
                    Configurator.Infrastructure.ModernDialog.Show(this, Configurator.Infrastructure.UserErrorMessage.Format(ex), "Не удалось сохранить настройки", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void MenuHelpDocs_Click(object sender, RoutedEventArgs e) => Configurator.Infrastructure.ModernDialog.Show("Документация будет добавлена в следующей версии.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
    private void TemplateEditor_ActionLogged(string description, string source, string details)
    {
        ConfigureJournalContext();
        if (_isOfflineWorkspace) _offlineProjectDirty = true;
        _actionLog.Record(description, null, "Структура шаблонов", source, details);
        var restorePayload = description?.Equals("Удалена логическая связь", StringComparison.OrdinalIgnoreCase) == true
            ? _pendingLogicalRelationRestore : null;
        _pendingLogicalRelationRestore = null;
        var journalSaved = _changeJournal.Record(new ChangeJournalEntry
        {
            Database = _selectedDbName ?? "",
            Server = _connection?.Server ?? "",
            Category = "Структура шаблонов",
            User = UserAuthorizationService.CurrentUser?.DisplayName ?? Environment.UserName,
            UserRole = UserAuthorizationService.CurrentUser?.RoleName ?? "",
            Mode = _currentMode,
            ProjectType = _isOfflineWorkspace ? "Offline" : "Online",
            ProjectId = _isOfflineWorkspace ? (_openedLocalProject?.Manifest.ProjectId ?? "") : "online:" + (_connection?.Server ?? "") + "/" + (_selectedDbName ?? ""),
            ProjectName = _isOfflineWorkspace ? (_openedLocalProject?.Manifest.Name ?? "") : (_selectedDbName ?? ""),
            EntityType = "Структура/связь",
            EntityIdentity = source ?? "",
            Result = description?.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase) == true ? "Ошибка" : "Успешно",
            Source = source ?? "",
            Description = description ?? "",
            Details = details ?? "",
            RestoreKind = restorePayload == null ? "" : "LogicalRelation",
            RestorePayloadJson = restorePayload == null ? "" : Newtonsoft.Json.JsonConvert.SerializeObject(restorePayload),
            OperationKind = restorePayload == null ? ChangeJournalOperationKind.Composite : ChangeJournalOperationKind.LogicalRelation,
            Changes = new List<ChangeJournalRowChange>()
        });
        if (!journalSaved)
            Configurator.Infrastructure.ModernDialog.Show(
                "Изменение структуры уже выполнено, но запись в журнал не сохранена. Проверьте доступность папки ChangeJournal.",
                "Предупреждение журнала изменений", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshActionLogUi();
    }

    }
}
