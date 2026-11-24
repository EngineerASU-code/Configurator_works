using System;
using System.Linq;
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
        private readonly Func<string, string> _connectionString;
        private SavedSqlQuery _openedQuery;
        private string _openedSql = "";
        private readonly CancellationTokenSource _lifetime = new();

        public FreeSqlWindow(Func<string, string> connectionString, IReadOnlyList<string> databases, string preferredDatabase = null)
        {
            InitializeComponent();
            Closed += (_, _) => _lifetime.Cancel();
            _connectionString = connectionString;

            CmbDatabase.ItemsSource = databases.ToList();

            if (!string.IsNullOrWhiteSpace(preferredDatabase) && CmbDatabase.Items.Contains(preferredDatabase))
                CmbDatabase.SelectedItem = preferredDatabase;
            else if (CmbDatabase.Items.Count > 0)
                CmbDatabase.SelectedIndex = 0;

            UpdateDatabaseContext();
            Loaded += async (_, _) => await RefreshQueriesAsync();
        }

        private string SelectedDatabase => CmbDatabase.SelectedItem as string;

        private SavedSqlQueryStore QueryStore() => new(_connectionString(SelectedDatabase), SelectedDatabase);

        private async void CmbDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            ResultGrid.ItemsSource = null;
            SetOpenedQuery(null, "");
            UpdateDatabaseContext();
            await RefreshQueriesAsync();
        }

        private void UpdateDatabaseContext()
        {
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
                var result = await db.ExecuteFreeSqlAsync(sql, null, database, _lifetime.Token, ConfirmDmlAsync);

                ResultGrid.ItemsSource = result.Result?.DefaultView;
                bool truncated = result.Result?.ExtendedProperties["Truncated"] is true;
                TxtStatus.Text = result.Result != null
                    ? $"База: {database} · Получено строк: {result.Result.Rows.Count}" +
                      (truncated ? $" (показаны первые {DatabaseService.FreeSqlMaxResultRows}; уточните запрос)" : "")
                    : $"База: {database} · Затронуто строк: {result.AffectedRows}";
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (FreeSqlRolledBackException rollback)
            {
                ResultGrid.ItemsSource = null;
                TxtStatus.Text = $"Изменения отменены · строк было бы затронуто: {rollback.AffectedRows}";
            }
            catch (Exception ex)
            {
                ResultGrid.ItemsSource = null;
                TxtStatus.Text = "Ошибка выполнения";
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
            var yes = new Button { Content = "Применить", MinWidth = 90 };
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

        private void SetBusy(bool busy)
        {
            CmbDatabase.IsEnabled = !busy;
            TxtSql.IsEnabled = !busy;
            BtnExecute.IsEnabled = !busy;
            QueryActions.IsEnabled = !busy;
            QueryLibrary.IsEnabled = !busy;
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshQueriesAsync();

        private async Task RefreshQueriesAsync()
        {
            SavedQueries.ItemsSource = null;
            if (SelectedDatabase == null)
            {
                TxtLibraryState.Text = "Выберите базу проекта.";
                return;
            }

            SetBusy(true);
            TxtLibraryState.Text = "Загрузка…";
            try
            {
                ShowQueries(await QueryStore().ListAsync(), _openedQuery?.Id);
            }
            catch (Exception ex)
            {
                TxtLibraryState.Text = "Не удалось загрузить список.";
                TxtLibraryState.ToolTip = UserErrorMessage.Format(ex);
            }
            finally { SetBusy(false); }
        }

        private void ShowQueries(List<SavedSqlQuery> queries, int? selectedId)
        {
            SavedQueries.ItemsSource = queries;
            SavedQueries.SelectedItem = queries.FirstOrDefault(query => query.Id == selectedId);
            TxtLibraryState.Text = queries.Count == 0 ? "В этой базе пока нет сохранённых запросов." : "";
            TxtLibraryState.ToolTip = null;
        }

        private void SavedQueries_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            BtnOpenQuery.IsEnabled = SavedQueries.SelectedItem is SavedSqlQuery;

        private async void BtnSaveQuery_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDatabase == null || string.IsNullOrWhiteSpace(TxtQueryName.Text) ||
                string.IsNullOrWhiteSpace(TxtSql.Text))
            {
                ModernDialog.Show(this, "Выберите базу, задайте имя запроса и введите SQL.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool update = sender == BtnUpdateQuery;
            SetBusy(true);
            try
            {
                string sql = TxtSql.Text;
                var store = QueryStore();
                var saved = await store.SaveAsync(update ? _openedQuery?.Id : null, TxtQueryName.Text, sql,
                    update ? _openedQuery?.Name : null, update ? _openedSql : null);
                SetOpenedQuery(saved, sql);
                TxtQueryName.Text = saved.Name;
                TxtStatus.Text = "Запрос сохранён в базе проекта.";

                // Saving has already succeeded even if a separate refresh fails.
                try { ShowQueries(await store.ListAsync(), saved.Id); }
                catch (Exception ex)
                {
                    TxtLibraryState.Text = "Не удалось обновить список.";
                    TxtLibraryState.ToolTip = UserErrorMessage.Format(ex);
                }
            }
            catch (Exception ex)
            {
                ModernDialog.Show(this, UserErrorMessage.Format(ex), "Ошибка сохранения запроса",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private async void BtnOpenQuery_Click(object sender, RoutedEventArgs e)
        {
            if (SavedQueries.SelectedItem is not SavedSqlQuery query || !ConfirmReplaceDraft()) return;
            SetBusy(true);
            try
            {
                var (opened, sql) = await QueryStore().ReadAsync(query.Id);
                TxtSql.Text = sql;
                TxtQueryName.Text = opened.Name;
                SetOpenedQuery(opened, sql);
                ResultGrid.ItemsSource = null;
                TxtStatus.Text = "Запрос открыт из базы проекта.";
                TxtSql.Focus();
            }
            catch (Exception ex)
            {
                ModernDialog.Show(this, UserErrorMessage.Format(ex), "Ошибка открытия запроса",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private void SetOpenedQuery(SavedSqlQuery query, string sql)
        {
            _openedQuery = query;
            _openedSql = sql;
            TxtCurrentQuery.Text = query == null ? "Новый запрос" : $"Открыт запрос «{query.Name}»";
            BtnUpdateQuery.IsEnabled = query != null;
        }

        private bool ConfirmReplaceDraft()
        {
            if (TxtSql.Text == _openedSql && TxtQueryName.Text == (_openedQuery?.Name ?? ""))
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
            ResultGrid.ItemsSource = null;
            UpdateDatabaseContext();
            TxtSql.Focus();
        }
    }
}
