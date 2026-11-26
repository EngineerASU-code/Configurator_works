using System;
using System.Linq;
using System.Windows.Input;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Configurator.Infrastructure;
using Configurator.Infrastructure.Database;

namespace Configurator
{
    public partial class FreeSqlWindow : Window
    {
        public event Action<string, IReadOnlyList<ChangeJournalRowChange>> ChangesCommitted;
        private readonly Func<string, string> _connectionString;
        private readonly LocalSqlLibrary _library = new();
        private LocalQuery _openedQuery;
        private string _openedSql = "";
        private readonly CancellationTokenSource _lifetime = new();

        public FreeSqlWindow(Func<string, string> connectionString, IReadOnlyList<string> databases, string preferredDatabase = null)
        {
            InitializeComponent();
            Width = Math.Min(Width, SystemParameters.WorkArea.Width * 0.94);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.92);
            UpdateEditor();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
                e.Handled = true;
                if (BtnSaveQuery.IsEnabled) BtnSaveQuery_Click(this, e);
            };
            KeyDown += Configurator.Infrastructure.ModernDialog.CloseToolOnEnter;
            Closed += (_, _) => _lifetime.Cancel();
            _connectionString = connectionString;

            foreach (var database in databases) CmbDatabase.Items.Add(database);

            if (!string.IsNullOrWhiteSpace(preferredDatabase) && CmbDatabase.Items.Contains(preferredDatabase))
                CmbDatabase.SelectedItem = preferredDatabase;
            else if (CmbDatabase.Items.Count > 0)
                CmbDatabase.SelectedIndex = 0;

            UpdateDatabaseContext();
            Loaded += (_, _) => RefreshQueries();
        }

        internal void SetDatabaseAvailable(string database, bool available)
        {
            var existing = CmbDatabase.Items.OfType<string>().FirstOrDefault(name =>
                string.Equals(name, database, StringComparison.OrdinalIgnoreCase));
            if (available && existing == null) CmbDatabase.Items.Add(database);
            if (!available && existing != null) CmbDatabase.Items.Remove(existing);
        }

        private string SelectedDatabase => CmbDatabase.SelectedItem as string;

        private void CmbDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultGrid == null || BtnExecute == null) return;
            SetResult(null);
            UpdateDatabaseContext();
        }

        private void UpdateDatabaseContext()
        {
            BtnExecute.IsEnabled = SelectedDatabase != null && TxtSql.IsEnabled;
            string database = CmbDatabase.SelectedItem?.ToString();
            TxtStatus.Text = string.IsNullOrWhiteSpace(database)
                ? "База данных не выбрана"
                : $"Контекст: {database}";
        }

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            string database = CmbDatabase.SelectedItem?.ToString();

            if (string.IsNullOrWhiteSpace(database))
            {
                Configurator.Infrastructure.ModernDialog.Show("Выберите базу данных.", "Свободный ввод",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string sql = TxtSql.Text.Trim();
            if (string.IsNullOrWhiteSpace(sql))
            {
                Configurator.Infrastructure.ModernDialog.Show("Введите SQL-запрос.", "Свободный ввод",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                SetBusy(true);
                TxtStatus.Text = $"Выполнение в базе: {database}";

                // Ключевой момент: connection string создаётся СТРОГО из выбранной БД.
                // DatabaseService дополнительно проверяет фактический DB_NAME().
                var db = new DatabaseService(_connectionString(database));
                // INSERT/UPDATE/DELETE: show the affected-row count before COMMIT.
                var result = await db.ExecuteFreeSqlCoreAsync(sql, null, database, _lifetime.Token, ConfirmDmlAsync,
                    DatabaseService.FreeSqlConfirmationTimeout, changes => Dispatcher.Invoke(() => ChangesCommitted?.Invoke(database, changes)));

                if (database != SelectedDatabase) return;
                SetResult(result.Result?.DefaultView);
                bool truncated = result.Result?.ExtendedProperties["Truncated"] is true;
                TxtStatus.Text = result.Result != null
                    ? $"База: {database} · Получено строк: {result.Result.Rows.Count}" +
                      (truncated ? $" (показаны первые {DatabaseService.FreeSqlMaxResultRows}; уточните запрос)" : "")
                    : $"База: {database} · Затронуто строк: {result.AffectedRows}";
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (FreeSqlRolledBackException rollback)
            {
                SetResult(null);
                TxtStatus.Text = $"Изменения отменены · строк было бы затронуто: {rollback.AffectedRows}";
            }
            catch (Exception ex)
            {
                SetResult(null);
                TxtStatus.Text = UserErrorMessage.Format(ex);
                Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "SQL ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task<bool> ConfirmDmlAsync(int affected)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(DatabaseService.FreeSqlConfirmationTimeout);
            try
            {
                return await Dispatcher.InvokeAsync(() => ShowDmlConfirmation(affected, deadline.Token)).Task.Unwrap()
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; }
        }

        private Task<bool> ShowDmlConfirmation(int affected, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromResult(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialog = new Window { Title = "Свободный ввод", Owner = this, Width = 460,
                SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = $"Будет изменено строк: {affected}. Подтвердите в течение 60 секунд.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var no = new Button { Content = "Отмена", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            var yes = new Button { Content = "Применить", IsDefault = true, MinWidth = 90 };
            buttons.Children.Add(no); buttons.Children.Add(yes); panel.Children.Add(buttons); dialog.Content = panel;
            var registration = ct.Register(() =>
            {
                completion.TrySetResult(false);
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(dialog.Close));
            });
            no.Click += (_, _) => { completion.TrySetResult(false); dialog.Close(); };
            yes.Click += (_, _) => { completion.TrySetResult(true); dialog.Close(); };
            dialog.Closed += (_, _) => { registration.Dispose(); completion.TrySetResult(false); };
            dialog.Show();
            return completion.Task;
        }

        private GridLength _resultHeight = new(1, GridUnitType.Star);

        private void SetResult(System.Data.DataView rows)
        {
            if (OutputSplitter.Visibility == Visibility.Visible) _resultHeight = OutputRow.Height;
            ResultGrid.ItemsSource = rows;
            OutputRow.MinHeight = rows == null ? 0 : 90;
            OutputRow.Height = rows == null ? new GridLength(0) : _resultHeight;
            OutputSplitter.Visibility = rows == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetBusy(bool busy)
        {
            CmbDatabase.IsEnabled = !busy;
            TxtSql.IsEnabled = !busy;
            BtnExecute.IsEnabled = !busy && SelectedDatabase != null;
            QueryActions.IsEnabled = !busy;
            QueryLibrary.IsEnabled = !busy;
            UpdateEditor();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) {
            if (!ConfirmReplaceDraft()) return;
            RefreshQueries();
            if (SavedQueries.SelectedItem is LocalQuery query) OpenQuery(query);
            else if (_openedQuery != null) SetOpenedQuery(null, "");
        }

        private void RefreshQueries()
        {
            try { ShowQueries(_library.Read(), _openedQuery?.Id); }
            catch (Exception ex)
            {
                TxtLibraryState.Text = UserErrorMessage.Format(ex);
            }
        }

        private void ShowQueries(List<LocalQuery> queries, Guid? selectedId)
        {
            _syncSelection = true;
            SavedQueries.ItemsSource = queries;
            SavedQueries.SelectedItem = queries.FirstOrDefault(query => query.Id == selectedId);
            _syncSelection = false;
            TxtLibraryState.Text = queries.Count == 0 ? "В локальной библиотеке пока нет сохранённых запросов." : "";
            TxtLibraryState.ToolTip = null;
        }

        private bool _syncSelection;
        private LocalQuery _menuQuery;
        private void LibraryMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _menuQuery = e.CursorLeft < 0 ? SavedQueries.SelectedItem as LocalQuery :
                ItemsControl.ContainerFromElement(SavedQueries, e.OriginalSource as DependencyObject) is ListBoxItem item
                    ? item.Content as LocalQuery : null;
            DeleteQuery.IsEnabled = _menuQuery != null;
        }

        private void DeleteQuery_Click(object sender, RoutedEventArgs e)
        {
            var query = _menuQuery;
            if (query == null || ModernDialog.Show(this, $"Удалить локальный запрос «{query.Name}»?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                _library.Delete(query);
                if (_openedQuery?.Id == query.Id)
                {
                    if (!HasDraftChanges())
                    {
                        TxtSql.Clear();
                        TxtQueryName.Clear();
                    }
                    SetOpenedQuery(null, "");
                    SetResult(null);
                }
                TxtStatus.Text = "Запрос удалён из локальной библиотеки.";
                RefreshQueries();
            }
            catch (Exception ex) { TxtStatus.Text = UserErrorMessage.Format(ex); }
        }

        private void EditorChanged(object sender, TextChangedEventArgs e) => UpdateEditor();
        private void EditorScrolled(object sender, ScrollChangedEventArgs e) => UpdateEditor();

        private void UpdateEditor()
        {
            if (BtnSaveQuery == null) return;
            BtnSaveQuery.IsEnabled = TxtSql.IsEnabled && !string.IsNullOrWhiteSpace(TxtSql.Text)
                && !string.IsNullOrWhiteSpace(TxtQueryName.Text);
            string[] hints = { "SELECT column1, column2", "FROM dbo.TableName", "WHERE condition" };
            string[] lines = TxtSql.Text.Split('\n');
            SqlHint.Text = string.Join("\n", hints.Select((hint, i) =>
                i >= lines.Length || lines[i].TrimEnd('\r').Length == 0 ? hint : ""));
            SqlHint.RenderTransform = new System.Windows.Media.TranslateTransform(-TxtSql.HorizontalOffset, -TxtSql.VerticalOffset);
        }

        private void SavedQueries_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncSelection || SavedQueries.SelectedItem is not LocalQuery query) return;
            if (!ConfirmReplaceDraft())
            {
                _syncSelection = true;
                SavedQueries.SelectedItem = _openedQuery;
                _syncSelection = false;
                return;
            }
            OpenQuery(query);
        }

        private void BtnSaveQuery_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saved = _library.Save(_openedQuery,
                    TxtQueryName.Text.Trim(), TxtSql.Text);
                TxtQueryName.Text = saved.Name;
                SetOpenedQuery(saved, saved.Sql);
                TxtStatus.Text = "Запрос сохранён в локальной библиотеке.";
                RefreshQueries();
            }
            catch (Exception ex)
            {
                ModernDialog.Show(this, UserErrorMessage.Format(ex), "Ошибка сохранения запроса",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenQuery(LocalQuery query)
        {
            TxtSql.Text = query.Sql;
            TxtQueryName.Text = query.Name;
            SetOpenedQuery(query, query.Sql);
            SetResult(null);
            TxtStatus.Text = "Запрос открыт из локальной библиотеки.";
            TxtSql.Focus();
        }

        private void SetOpenedQuery(LocalQuery query, string sql)
        {
            _openedQuery = query;
            _openedSql = sql;
            TxtCurrentQuery.Text = query == null ? "Новый запрос" : $"Открыт запрос «{query.Name}»";

        }

        private bool HasDraftChanges() =>
            TxtSql.Text != _openedSql || TxtQueryName.Text != (_openedQuery?.Name ?? "");

        private bool ConfirmReplaceDraft()
        {
            if (!HasDraftChanges())
                return true;
            return ModernDialog.Show(this, "Заменить несохранённые изменения?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmReplaceDraft()) return;
            TxtSql.Clear();
            TxtQueryName.Clear();
            SetOpenedQuery(null, "");
            SavedQueries.SelectedItem = null;
            SetResult(null);
            UpdateDatabaseContext();
            TxtSql.Focus();
        }
    }
}
