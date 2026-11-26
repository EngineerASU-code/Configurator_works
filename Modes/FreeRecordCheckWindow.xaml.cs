#nullable enable
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Configurator.Core.Projects;
using Configurator.Core.Workspace;
using Configurator.Infrastructure;
using Configurator.Infrastructure.Projects;

namespace Configurator;

public partial class FreeRecordCheckWindow : Window
{
    private readonly Func<string, string>? _getConnectionString;
    private readonly ConfiguratorProject? _project;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private Func<RecordCheckScope, CancellationToken, Task<RecordCheckResult>>? _check;

    internal FreeRecordCheckWindow(Func<string, string> getConnectionString, IEnumerable<string> databases, string selectedDatabase)
    {
        _getConnectionString = getConnectionString;
        SetUp(databases, selectedDatabase);
    }

    internal FreeRecordCheckWindow(ConfiguratorProject project)
    {
        _project = project;
        SetUp([project.Manifest.Name], project.Manifest.Name);
        DatabaseLabel.Content = "Проект";
    }

    private void SetUp(IEnumerable<string> databases, string selectedDatabase)
    {
        InitializeComponent();
        var names = databases.ToList();
        CmbDatabase.ItemsSource = names;
        CmbDatabase.SelectedItem = names.FirstOrDefault(name => name.Equals(selectedDatabase, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
        Loaded += LoadWindow;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private async void LoadWindow(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadWindow;
        await LoadDatabaseAsync();
    }

    private async void CmbDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_busy) await LoadDatabaseAsync();
    }

    private async Task LoadDatabaseAsync()
    {
        if (CmbDatabase.SelectedItem is not string name) return;
        ResetResult();
        await RunAsync("Загружаем PLC…", async ct =>
        {
            _check = null;
            CmbPlc.ItemsSource = null;
            CmbClass.ItemsSource = null;
            IReadOnlyList<RecordCheckScope> scopes;
            if (_project != null)
            {
                var source = new OfflineProjectDataSource(_project);
                using var workspace = new ProjectWorkspace(source,
                    architecture: new ProjectArchitectureDataSource(_project), allowWrite: false);
                var classes = await RecordCheckCatalog.LoadAsync(workspace, ct);
                scopes = RecordCheckCatalog.LocalScopes(source, classes, ct);
                _check = (scope, token) => Task.FromResult(RecordCheckCatalog.CheckLocal(source, scope, token));
            }
            else
            {
                var connection = _getConnectionString!(name);
                var source = new OnlineProjectDataSource(connection, name);
                using var workspace = new ProjectWorkspace(source,
                    architecture: new ProjectArchitectureDataSource(source.Server, source.Database), allowWrite: false);
                var classes = await RecordCheckCatalog.LoadAsync(workspace, ct);
                var database = new DatabaseService(connection);
                scopes = await database.GetRecordCheckScopesAsync(classes, ct);
                _check = database.CheckRecordsAsync;
            }
            ct.ThrowIfCancellationRequested();
            var controllers = scopes.GroupBy(scope => Convert.ToString(scope.Plc, CultureInfo.InvariantCulture) ?? "")
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .OrderBy(group => group.Key, StringComparer.CurrentCulture)
                .Select(group => new PlcChoice(group.Key, group.ToList())).ToList();
            CmbPlc.ItemsSource = controllers;
            TxtStatus.Text = controllers.Count == 0 ? "PLC с записями для проверки не найдены." : "Выберите PLC.";
        });
    }

    private void CmbPlc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        ResetResult();
        CmbClass.ItemsSource = (CmbPlc.SelectedItem as PlcChoice)?.Classes;
        TxtStatus.Text = CmbPlc.SelectedItem != null ? "Выберите класс." : "Выберите PLC.";
        UpdateControls();
    }

    private void CmbClass_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        ResetResult();
        TxtStatus.Text = CmbClass.SelectedItem != null ? "" : "Выберите класс.";
        UpdateControls();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (!BtnCheck.IsEnabled || CmbClass.SelectedItem is not RecordCheckScope scope || CmbPlc.SelectedItem is not PlcChoice plc) return;
        ResetResult();
        await RunAsync("Проверяем Record…", async ct =>
        {
            var result = await _check!(scope, ct);
            ct.ThrowIfCancellationRequested();
            TxtResultHeader.Text = $"{scope.Class.Title} · PLC {plc.Label}";
            if (result.MinRecord == null)
            {
                TxtResult.Text = "Записи не найдены.";
            }
            else
            {
                TxtResult.Text = $"Диапазон Record  {result.MinRecord}–{result.MaxRecord}\n\n";
                var summary = result.Gaps.Count == 0 ? "Свободных Record нет." : $"Свободные Record ({result.FreeCount})";
                TxtResult.Inlines.Add(new System.Windows.Documents.Run(summary)
                    { FontSize = 22, FontWeight = FontWeights.SemiBold });
                if (result.Gaps.Count > 0)
                    TxtResult.Inlines.Add(new System.Windows.Documents.Run("\n" + string.Join(", ", result.Gaps)) { FontSize = 22 });
            }
            TxtStatus.Text = "Проверка завершена";
        });
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> action)
    {
        var ct = _lifetime.Token;
        _busy = true;
        UpdateControls();
        TxtStatus.Text = status;
        try
        {
            await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) TxtStatus.Text = UserErrorMessage.Format(ex);
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested) UpdateControls();
        }
    }

    private void ResetResult()
    {
        TxtResultHeader.Text = "Результат проверки";
        TxtResult.Text = "";
    }

    private void UpdateControls()
    {
        CmbDatabase.IsEnabled = !_busy;
        CmbClass.IsEnabled = !_busy && CmbClass.HasItems;
        CmbPlc.IsEnabled = !_busy && CmbPlc.HasItems;
        BtnCheck.IsEnabled = !_busy && _check != null && CmbClass.SelectedItem != null;
    }

    private sealed record PlcChoice(string Label, IReadOnlyList<RecordCheckScope> Classes);
}
