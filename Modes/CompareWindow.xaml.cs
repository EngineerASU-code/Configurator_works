#nullable enable
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Configurator.Core.Compare;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Microsoft.Win32;

namespace Configurator;

public partial class CompareWindow : Window
{
    private readonly string[] _onlineDatabases;
    private readonly Func<string, string>? _resolveConnectionString;
    private readonly Func<CompareDifference, bool>? _isTransferAuthorized;
    private readonly bool _canUseOnline;
    private readonly bool _canRequestWrite;
    private readonly List<CompareDifference> _graphDifferences = new();
    private readonly List<CompareDifference> _relationDifferences = new();
    private readonly List<CompareDifference> _fieldDifferences = new();
    private readonly List<CompareDifference> _objectDifferences = new();
    private CompareSource? _sourceA;
    private CompareSource? _sourceB;
    private CompareResult? _result;
    private CancellationTokenSource? _compareCancellation;
    private Func<CompareTransferPreview, CancellationToken, Task>? _applyTransferAsync;
    private Func<CompareMetadataTransferPreview, CancellationToken, Task>? _applyMetadataTransferAsync;
    private string? _offlinePathA;
    private string? _offlinePathB;
    private string? _selectedOnlineDatabase;
    private bool _onlineOnA = true;
    private bool _isInitializing = true;
    private bool _isUpdatingDatabases;
    private bool _isBusy;
    private bool _resultIsStale;
    private bool _closed;
    private bool _closeAfterTransfer;

    public CompareWindow(
        IEnumerable<string>? onlineDatabases = null,
        string? selectedDatabase = null,
        Func<string, string>? resolveConnectionString = null,
        Func<CompareDifference, bool>? isTransferAuthorized = null,
        bool canUseOnline = true,
        bool canShowMode2 = true,
        bool canShowMode3 = true,
        bool canShowGraph = true,
        bool canShowRelations = true,
        bool canShowFields = true,
        bool canRequestWrite = false,
        bool canShowProjectSettings = true)
    {
        InitializeComponent();

        _onlineDatabases = (onlineDatabases ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _resolveConnectionString = resolveConnectionString;
        _isTransferAuthorized = isTransferAuthorized;
        _canUseOnline = canUseOnline && _onlineDatabases.Length > 0 && resolveConnectionString is not null;
        _canRequestWrite = canRequestWrite;

        ScenarioBox.ItemsSource = _canUseOnline
            ? new[] { new ScenarioChoice("Онлайн и офлайн", true), new ScenarioChoice("Офлайн и офлайн", false) }
            : new[] { new ScenarioChoice("Офлайн и офлайн", false) };
        ScenarioBox.SelectedIndex = 0;
        OnlineDatabaseA.ItemsSource = _onlineDatabases;
        OnlineDatabaseB.ItemsSource = _onlineDatabases;
        FilterBox.ItemsSource = new[]
        {
            "Все различия", "Только A", "Только B", "Изменённые",
            "Неоднозначные", "Несопоставимые", "Ошибки", "Перенос доступен"
        };
        FilterBox.SelectedIndex = 0;

        _selectedOnlineDatabase = _onlineDatabases.FirstOrDefault(name =>
            name.Equals(selectedDatabase, StringComparison.OrdinalIgnoreCase)) ?? _onlineDatabases.FirstOrDefault();
        OnlineDatabaseA.SelectedItem = _selectedOnlineDatabase;
        OnlineDatabaseB.SelectedItem = _selectedOnlineDatabase;

        var sections = new List<ComparisonSection>();
        if (canShowMode3) sections.Add(new("Объекты и данные", _objectDifferences));
        if (canShowMode2 && canShowGraph) sections.Add(new("Граф и таблицы", _graphDifferences));
        if (canShowMode2 && canShowRelations) sections.Add(new("Связи", _relationDifferences));
        if (canShowMode2 && canShowFields) sections.Add(new("Классы и поля", _fieldDifferences));
        SectionBox.ItemsSource = sections;
        SectionBox.SelectedIndex = 0;

        _isInitializing = false;
        RefreshScenarioControls();
        UpdateControlState();
        ProgressText.Text = "Выберите источники и запустите сравнение.";
    }

    internal Func<CompareTransferPreview, CancellationToken, Task>? ApplyTransferAsync
    {
        get => _applyTransferAsync;
        set
        {
            _applyTransferAsync = value;
            RefreshTransferAvailability();
        }
    }

    internal Func<CompareMetadataTransferPreview, CancellationToken, Task>? ApplyMetadataTransferAsync
    {
        get => _applyMetadataTransferAsync;
        set { _applyMetadataTransferAsync = value; RefreshTransferAvailability(); }
    }

    private bool IsOnlineOffline => _canUseOnline && ScenarioBox.SelectedItem is ScenarioChoice { UsesOnline: true };

    private void ScenarioBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        RefreshScenarioControls();
        MarkResultStale("Сценарий изменился. Запустите сравнение ещё раз.");
    }

    private void RefreshScenarioControls()
    {
        bool sideAOnline = IsOnlineOffline && _onlineOnA;
        bool sideBOnline = IsOnlineOffline && !_onlineOnA;
        OnlineDatabaseA.Visibility = sideAOnline ? Visibility.Visible : Visibility.Collapsed;
        OfflinePathA.Visibility = sideAOnline ? Visibility.Collapsed : Visibility.Visible;
        OpenOfflineA.Visibility = sideAOnline ? Visibility.Collapsed : Visibility.Visible;
        OnlineDatabaseB.Visibility = sideBOnline ? Visibility.Visible : Visibility.Collapsed;
        OfflinePathB.Visibility = sideBOnline ? Visibility.Collapsed : Visibility.Visible;
        OpenOfflineB.Visibility = sideBOnline ? Visibility.Collapsed : Visibility.Visible;
        FlipButton.IsEnabled = !_isBusy && (IsOnlineOffline || !string.IsNullOrWhiteSpace(_offlinePathA) || !string.IsNullOrWhiteSpace(_offlinePathB));
        UpdatePathDisplay();
    }

    private void OnlineDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingDatabases || sender is not ComboBox comboBox) return;
        _selectedOnlineDatabase = comboBox.SelectedItem as string;
        _isUpdatingDatabases = true;
        OnlineDatabaseA.SelectedItem = _selectedOnlineDatabase;
        OnlineDatabaseB.SelectedItem = _selectedOnlineDatabase;
        _isUpdatingDatabases = false;
        MarkResultStale("Источник онлайн изменился. Запустите сравнение ещё раз.");
    }

    private void OpenOfflineA_Click(object sender, RoutedEventArgs e) => ChooseOfflineProject(true);
    private void OpenOfflineB_Click(object sender, RoutedEventArgs e) => ChooseOfflineProject(false);

    private void ChooseOfflineProject(bool sideA)
    {
        var dialog = new OpenFileDialog
        {
            Title = sideA ? "Выберите офлайн-проект A" : "Выберите офлайн-проект B",
            Filter = "Локальный проект Configurator (*.cfgproj)|*.cfgproj",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        if (sideA) _offlinePathA = dialog.FileName;
        else _offlinePathB = dialog.FileName;
        UpdatePathDisplay();
        MarkResultStale("Файл офлайн-проекта изменился. Запустите сравнение ещё раз.");
    }

    private void UpdatePathDisplay()
    {
        OfflinePathA.Text = string.IsNullOrWhiteSpace(_offlinePathA) ? "Выберите .cfgproj" : _offlinePathA;
        OfflinePathB.Text = string.IsNullOrWhiteSpace(_offlinePathB) ? "Выберите .cfgproj" : _offlinePathB;
        OfflinePathA.ToolTip = _offlinePathA;
        OfflinePathB.ToolTip = _offlinePathB;
    }

    private void FlipButton_Click(object sender, RoutedEventArgs e)
    {
        (_offlinePathA, _offlinePathB) = (_offlinePathB, _offlinePathA);
        _onlineOnA = !_onlineOnA;
        RefreshScenarioControls();
        MarkResultStale("Стороны поменялись местами. Запустите сравнение ещё раз.");
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _isApplying) return;
        if (!TryGetSelections(out var database, out var pathA, out var pathB, out var validationMessage))
        {
            ProgressText.Text = validationMessage;
            return;
        }

        ReleaseSources();
        ClearResult();
        _resultIsStale = false;
        _compareCancellation = new CancellationTokenSource();
        SetBusy(true);
        ProgressText.Text = "Открытие источников…";

        try
        {
            bool onlineOffline = IsOnlineOffline;
            bool onlineOnA = _onlineOnA;
            bool allowWrite = _canRequestWrite;
            string? connectionString = onlineOffline ? _resolveConnectionString!(database!) : null;
            string? onlineName = database;
            CancellationToken cancellationToken = _compareCancellation.Token;

            if (onlineOffline && onlineOnA)
            {
                _sourceA = await Task.Run(() => CompareSourceReader.OpenOnlineAsync(
                    connectionString!, onlineName!, allowWrite, cancellationToken), cancellationToken);
                _sourceB = await Task.Run(() => CompareSourceReader.OpenOfflineAsync(
                    pathB!, allowWrite, cancellationToken), cancellationToken);
            }
            else if (onlineOffline)
            {
                _sourceA = await Task.Run(() => CompareSourceReader.OpenOfflineAsync(
                    pathA!, allowWrite, cancellationToken), cancellationToken);
                _sourceB = await Task.Run(() => CompareSourceReader.OpenOnlineAsync(
                    connectionString!, onlineName!, allowWrite, cancellationToken), cancellationToken);
            }
            else
            {
                _sourceA = await Task.Run(() => CompareSourceReader.OpenOfflineAsync(
                    pathA!, allowWrite, cancellationToken), cancellationToken);
                _sourceB = await Task.Run(() => CompareSourceReader.OpenOfflineAsync(
                    pathB!, allowWrite, cancellationToken), cancellationToken);
            }

            var sourceA = _sourceA ?? throw new InvalidOperationException("Source A was not opened.");
            var sourceB = _sourceB ?? throw new InvalidOperationException("Source B was not opened.");
            var progress = new Progress<string>(message =>
            {
                if (!_closed) ProgressText.Text = message switch
                {
                    "Reading project metadata" => "Чтение архитектуры и метаданных…",
                    "Comparing object data" => "Сравнение объектов и значений…",
                    _ => "Выполняется сравнение…"
                };
            });
            _result = await Task.Run(() => CompareEngine.CompareAsync(
                sourceA, sourceB, progress, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PopulateResult(_result);
            ProgressText.Text = ContainsErrors(_result.Architecture.Concat(_result.Objects))
                ? "Сравнение выполнено частично. Часть данных не удалось прочитать."
                : HasVisibleDifferences()
                    ? "Сравнение завершено."
                    : "Различий не найдено.";
        }
        catch (OperationCanceledException)
        {
            ReleaseSources();
            ClearResult();
            ProgressText.Text = "Сравнение отменено.";
        }
        catch
        {
            ReleaseSources();
            ClearResult();
            ProgressText.Text = "Не удалось прочитать источники или завершить сравнение. Проверьте доступность источников.";
        }
        finally
        {
            _compareCancellation?.Dispose();
            _compareCancellation = null;
            SetBusy(false);
            RefreshTransferAvailability();
        }
    }

    private bool TryGetSelections(out string? database, out string? pathA, out string? pathB, out string message)
    {
        database = null;
        pathA = null;
        pathB = null;
        if (IsOnlineOffline)
        {
            database = _selectedOnlineDatabase;
            if (string.IsNullOrWhiteSpace(database))
            {
                message = "Выберите подключённую онлайн-базу.";
                return false;
            }
        }

        bool aOnline = IsOnlineOffline && _onlineOnA;
        bool bOnline = IsOnlineOffline && !_onlineOnA;
        if (!aOnline) pathA = _offlinePathA;
        if (!bOnline) pathB = _offlinePathB;
        if ((!aOnline && string.IsNullOrWhiteSpace(pathA)) || (!bOnline && string.IsNullOrWhiteSpace(pathB)))
        {
            message = "Выберите файл .cfgproj для каждой стороны с офлайн-источником.";
            return false;
        }
        if (!aOnline && !bOnline && Path.GetFullPath(pathA!).Equals(Path.GetFullPath(pathB!), StringComparison.OrdinalIgnoreCase))
        {
            message = "Для сценария «Офлайн и офлайн» выберите два разных файла.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateControlState();
    }

    private bool _isApplying;

    internal bool IsApplyingTransfer => _isApplying;

    internal void CloseForContextChange()
    {
        if (!_isApplying)
        {
            Close();
            return;
        }

        _closeAfterTransfer = true;
        Hide();
    }

    private void SetApplying(bool applying)
    {
        _isApplying = applying;
        UpdateControlState();
    }

    private void UpdateControlState()
    {
        bool locked = _isBusy || _isApplying;
        ScenarioBox.IsEnabled = !locked;
        OnlineDatabaseA.IsEnabled = !locked;
        OnlineDatabaseB.IsEnabled = !locked;
        OpenOfflineA.IsEnabled = !locked;
        OpenOfflineB.IsEnabled = !locked;
        CompareButton.IsEnabled = !locked;
        FlipButton.IsEnabled = !locked &&
            (IsOnlineOffline || !string.IsNullOrWhiteSpace(_offlinePathA) || !string.IsNullOrWhiteSpace(_offlinePathB));
        ProgressBar.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PopulateResult(CompareResult result)
    {
        _graphDifferences.Clear();
        _relationDifferences.Clear();
        _fieldDifferences.Clear();
        _objectDifferences.Clear();
        _graphDifferences.AddRange(SelectArchitecture(result.Architecture,
            "Tables", "Таблицы", "table", "таблиц", "physical metadata", "физическ"));
        _relationDifferences.AddRange(SelectArchitecture(result.Architecture,
            "Relations", "Связи", "relation", "связ"));
        _fieldDifferences.AddRange(SelectArchitecture(result.Architecture,
            "Classes", "Классы", "class", "класс"));
        _objectDifferences.AddRange(result.Objects);
        _resultIsStale = false;
        SetSourceHeader(HeaderA, "A", _sourceA!);
        SetSourceHeader(HeaderB, "B", _sourceB!);
    }

    private static void SetSourceHeader(TextBlock header, string side, CompareSource source)
    {
        string name = source.PackagePath is string path ? Path.GetFileName(path) : source.Workspace.DisplayName;
        header.Text = $"{side} · {name}";
        header.ToolTip = source.PackagePath ?? source.Workspace.DisplayName;
    }

    private static IEnumerable<CompareDifference> SelectArchitecture(
        IReadOnlyList<CompareDifference> differences,
        string englishGroupName,
        string russianGroupName,
        params string[] issueTerms)
    {
        foreach (var difference in differences)
            if (MatchesGroup(difference.Title, englishGroupName, russianGroupName) ||
                ((difference.Status is CompareStatus.Error or CompareStatus.NotComparable) &&
                 issueTerms.Any(term => difference.Title.Contains(term, StringComparison.OrdinalIgnoreCase))))
                yield return difference;
    }

    private static bool ContainsErrors(IEnumerable<CompareDifference> differences) =>
        differences.Any(difference => difference.Status == CompareStatus.Error ||
            (difference.Children is not null && ContainsErrors(difference.Children)));

    private static bool MatchesGroup(string title, string englishName, string russianName) =>
        title.Equals(englishName, StringComparison.OrdinalIgnoreCase) ||
        title.Equals(russianName, StringComparison.OrdinalIgnoreCase);

    private bool HasVisibleDifferences() =>
        SectionBox.Items.Cast<ComparisonSection>().Any(section => section.Rows.Any(IsDifference));

    private static bool IsDifference(CompareDifference difference) => difference.Status != CompareStatus.Equal;

    private void ClearResult()
    {
        _result = null;
        _graphDifferences.Clear();
        _relationDifferences.Clear();
        _fieldDifferences.Clear();
        _objectDifferences.Clear();
        LeftGrid.ItemsSource = null;
        HeaderA.Text = "A · Проект";
        HeaderB.Text = "B · Проект";
        HeaderA.ToolTip = HeaderB.ToolTip = null;
        ClearDetails();
    }

    private void RefreshRows()
    {
        if (_isInitializing || SectionBox.SelectedItem is not ComparisonSection section) return;
        ClearDetails();
        var view = new System.Windows.Data.ListCollectionView(FilterRows(section.Rows));
        if (section.Rows == _objectDifferences)
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(CompareRow.Group)));
        LeftGrid.ItemsSource = view;
    }

    private List<CompareRow> FilterRows(IEnumerable<CompareDifference> differences)
    {
        var rows = new List<CompareRow>();
        AppendMatchingRows(differences, rows, null, 0, FilterBox.SelectedItem as string);
        return rows;
    }

    private void AppendMatchingRows(
        IEnumerable<CompareDifference> differences,
        List<CompareRow> destination,
        string? parentObjectIdentity,
        int depth,
        string? filter)
    {
        foreach (var difference in differences)
        {
            string? objectIdentity = GetObjectIdentity(difference) ?? parentObjectIdentity;
            var row = new CompareRow(difference, depth, objectIdentity);
            UpdateTransferAvailability(row);

            var children = new List<CompareRow>();
            if (difference.Children is not null)
                AppendMatchingRows(difference.Children, children, objectIdentity, depth + 1, filter);

            bool matchesFilter = IsDifference(difference) && MatchesFilter(row, filter);
            if (!matchesFilter && children.Count == 0) continue;

            if (IsDifference(difference))
            {
                row.IsContext = !matchesFilter;
                destination.Add(row);
            }
            destination.AddRange(children);
        }
    }

    private static bool MatchesFilter(CompareRow row, string? filter) => filter switch
    {
        "Только A" => row.Difference.Status == CompareStatus.OnlyA,
        "Только B" => row.Difference.Status == CompareStatus.OnlyB,
        "Изменённые" => row.Difference.Status == CompareStatus.Changed,
        "Неоднозначные" => row.Difference.Status == CompareStatus.Ambiguous,
        "Несопоставимые" => row.Difference.Status == CompareStatus.NotComparable,
        "Ошибки" => row.Difference.Status == CompareStatus.Error,
        "Перенос доступен" => row.CanTransferAtoB || row.CanTransferBtoA,
        _ => IsDifference(row.Difference)
    };

    private static string? GetObjectIdentity(CompareDifference difference)
    {
        if (difference.Pair is not { } pair) return null;
        if (difference.Column is null) return difference.Title;
        return null;
    }

    private void RefreshTransferAvailability()
    {
        if (_result is null) return;
        RefreshRows();
    }

    private void UpdateTransferAvailability(CompareRow row)
    {
        if (_sourceA is null || _sourceB is null)
        {
            row.SetTransfer(false, "Сначала запустите сравнение.", false, "Сначала запустите сравнение.");
            return;
        }

        bool authorized = _isTransferAuthorized?.Invoke(row.Difference) == true;
        if (row.Difference.MetadataKind is not null)
        {
            bool statusCanTransfer = row.Difference.Status is CompareStatus.Changed or CompareStatus.OnlyA or CompareStatus.OnlyB;
            bool sourceAExists = row.Difference.Status != CompareStatus.OnlyB;
            bool sourceBExists = row.Difference.Status != CompareStatus.OnlyA;
            bool canApplyDeletion = row.Difference.MetadataKind is CompareMetadataKind.ManualRelation or CompareMetadataKind.ProjectTextList or CompareMetadataKind.ThemePreset;
            bool aToBAllowed = statusCanTransfer && (sourceAExists || canApplyDeletion) && _sourceB.CanWrite && !_sourceB.Workspace.IsReadOnly && _applyMetadataTransferAsync is not null;
            bool bToAAllowed = statusCanTransfer && (sourceBExists || canApplyDeletion) && _sourceA.CanWrite && !_sourceA.Workspace.IsReadOnly && _applyMetadataTransferAsync is not null;
            if (row.Difference.MetadataKind == CompareMetadataKind.ClassDefinition && row.Difference.Status == CompareStatus.OnlyA) bToAAllowed = false;
            if (row.Difference.MetadataKind == CompareMetadataKind.ClassDefinition && row.Difference.Status == CompareStatus.OnlyB) aToBAllowed = false;
            row.SetTransfer(!_resultIsStale && !_isBusy && !_isApplying && authorized && aToBAllowed,
                MetadataTransferReason(_resultIsStale, authorized, aToBAllowed, "Перенести из проекта A в проект B."),
                !_resultIsStale && !_isBusy && !_isApplying && authorized && bToAAllowed,
                MetadataTransferReason(_resultIsStale, authorized, bToAAllowed, "Перенести из проекта B в проект A."));
            return;
        }

        var aToB = CompareTransferPreview.Evaluate(_sourceA, _sourceB, row.Difference, aToB: true);
        var bToA = CompareTransferPreview.Evaluate(_sourceA, _sourceB, row.Difference, aToB: false);
        string aToBReason = TransferReason(aToB, authorized, "Перенести из проекта A в проект B.");
        string bToAReason = TransferReason(bToA, authorized, "Перенести из проекта B в проект A.");
        bool canRequest = !_resultIsStale && !_isBusy && !_isApplying && _applyTransferAsync is not null;
        row.CanRequestAtoB = canRequest && row.Difference.Pair?.RecordA is not null;
        row.CanRequestBtoA = canRequest && row.Difference.Pair?.RecordB is not null;
        row.SetTransfer(
            !_resultIsStale && !_isBusy && !_isApplying && _applyTransferAsync is not null && authorized && aToB.IsAllowed,
            _resultIsStale ? "Результат устарел. Запустите сравнение ещё раз." : aToBReason,
            !_resultIsStale && !_isBusy && !_isApplying && _applyTransferAsync is not null && authorized && bToA.IsAllowed,
            _resultIsStale ? "Результат устарел. Запустите сравнение ещё раз." : bToAReason);
    }

    private static string MetadataTransferReason(bool stale, bool authorized, bool available, string direction) =>
        stale ? "Результат устарел. Запустите сравнение ещё раз." :
        !authorized ? $"{direction} Нет разрешения изменять этот тип настроек." :
        !available ? $"{direction} Перенос этого элемента недоступен." : direction;

    private string TransferReason(CompareTransferPreview preview, bool authorized, string direction)
    {
        if (!preview.IsAllowed) return $"{direction} {preview.BlockReason ?? "Перенос недоступен."}";
        if (!authorized) return $"{direction} Нет разрешения на перенос этого элемента в режиме 3.";
        if (_applyTransferAsync is null) return $"{direction} Перенос этого элемента недоступен.";
        return $"{direction} {preview.Description}";
    }

    private void DifferenceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeftGrid.SelectedItem is not CompareRow row)
        {
            ClearDetails();
            return;
        }
        string title = string.IsNullOrWhiteSpace(row.ObjectIdentity) ||
            row.Difference.Title.StartsWith(row.ObjectIdentity, StringComparison.OrdinalIgnoreCase)
            ? row.Difference.Title
            : $"{row.ObjectIdentity} → {row.Difference.Title}";
        DetailTitle.Text = title;
        DetailStatus.Text = row.StatusText;
        var blocked = new List<string>();
        if (row.CanRequestBtoA && !row.CanTransferBtoA) blocked.Add(row.TransferBtoAReason);
        if (row.CanRequestAtoB && !row.CanTransferAtoB) blocked.Add(row.TransferAtoBReason);
        DetailTransfer.Text = string.Join(Environment.NewLine, blocked);
        DetailTransfer.Visibility = blocked.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailA.Text = row.Difference.Status == CompareStatus.OnlyB
            ? "Отсутствует"
            : row.Difference.DetailAValue ?? row.Difference.A ?? "Нет данных";
        DetailB.Text = row.Difference.Status == CompareStatus.OnlyA
            ? "Отсутствует"
            : row.Difference.DetailBValue ?? row.Difference.B ?? "Нет данных";
        if (row.Difference.Column is null && row.Difference.Pair is { } pair)
        {
            static string Describe(Configurator.Core.Projects.ProjectRecord? record) =>
                record is null ? "Отсутствует" : string.Join(Environment.NewLine,
                    record.Values.Select(value => $"{value.Key}: {value.Value?.ToString() ?? "NULL"}"));
            DetailA.Text = Describe(pair.RecordA);
            DetailB.Text = Describe(pair.RecordB);
        }
    }

    private void ClearDetails()
    {
        DetailTitle.Text = "Выберите различие, чтобы просмотреть значения.";
        DetailStatus.Text = string.Empty;
        DetailTransfer.Visibility = Visibility.Collapsed;
        DetailA.Text = string.Empty;
        DetailB.Text = string.Empty;
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRows();

    private void SectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRows();

    private void SideGrid_Loaded(object sender, RoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.Template.FindName("DG_ScrollViewer", grid) is not ScrollViewer viewer ||
            viewer.Template.FindName("PART_ScrollContentPresenter", viewer) is not ScrollContentPresenter viewport) return;
        // Grouped WPF rows can retain their previous extent after the window shrinks.
        // Size the columns to the visible area, excluding the vertical scrollbar.
        viewport.SizeChanged -= SideViewport_SizeChanged;
        viewport.SizeChanged += SideViewport_SizeChanged;
        FitColumns(grid, viewport.ActualWidth);
    }

    private void SideViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && VisualTreeHelper.FindParent<DataGrid>((ScrollContentPresenter)sender) is { } grid)
            FitColumns(grid, e.NewSize.Width);
    }

    private static void FitColumns(DataGrid grid, double width)
    {
        if (width <= 0) return;
        double dataWidth = Math.Floor(width / (grid.Columns.Count == 2 ? 2 : 2.5));
        grid.Columns[0].Width = dataWidth;
        grid.Columns[1].Width = grid.Columns.Count == 2 ? width - dataWidth : dataWidth;
        if (grid.Columns.Count == 3) grid.Columns[2].Width = width - 2 * dataWidth;
    }

    private void SideGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || e.OriginalSource is not ScrollViewer origin) return;
        var other = sender == LeftGrid ? RightGrid : LeftGrid;
        if (other?.Template.FindName("DG_ScrollViewer", other) is ScrollViewer peer &&
            Math.Abs(peer.VerticalOffset - origin.VerticalOffset) > 0.01)
            peer.ScrollToVerticalOffset(origin.VerticalOffset);
    }

    private sealed record ComparisonSection(string Label, List<CompareDifference> Rows)
    {
        public override string ToString() => Label;
    }

    private async void TransferAtoB_Click(object sender, RoutedEventArgs e) => await RunTransferAsync(sender, aToB: true);
    private async void TransferBtoA_Click(object sender, RoutedEventArgs e) => await RunTransferAsync(sender, aToB: false);

    private async Task RunTransferAsync(object sender, bool aToB)
    {
        if (_resultIsStale || _isBusy || _isApplying || _closed || _sourceA is null || _sourceB is null ||
            sender is not FrameworkElement { DataContext: CompareRow row })
            return;

        if (_isTransferAuthorized?.Invoke(row.Difference) != true)
        {
            const string message = "Нет разрешения на добавление или изменение записи в режиме 3.";
            ProgressText.Text = message;
            SessionTestLog.Trace("ПЕРЕНОС", message);
            return;
        }

        if (row.Difference.MetadataKind is not null)
        {
            await RunMetadataTransferAsync(row, aToB);
            return;
        }

        if (_applyTransferAsync is null) return;
        var preview = CompareTransferPreview.Evaluate(_sourceA, _sourceB, row.Difference, aToB);
        if (!preview.IsAllowed)
        {
            ProgressText.Text = preview.BlockReason ?? "Перенос недоступен.";
            SessionTestLog.Trace("ПЕРЕНОС", $"{(aToB ? "A → B" : "B → A")} · {ProgressText.Text}");
            return;
        }
        var editor = new CompareWriteWindow(preview) { Owner = this };
        if (editor.ShowDialog() != true) return;
        preview = editor.Preview;

        if (_closed || _isBusy || _isApplying || _isTransferAuthorized?.Invoke(row.Difference) != true)
        {
            ProgressText.Text = "Разрешение изменилось. Запустите сравнение и проверьте доступ ещё раз.";
            MarkResultStale("Результат устарел после изменения разрешений.");
            return;
        }

        bool applied = false;
        SetApplying(true);
        RefreshTransferAvailability();
        try
        {
            await _applyTransferAsync(preview, CancellationToken.None);
            applied = true;
            MarkResultStale("Перенос выполнен. Обновляем сравнение…");
        }
        catch (CompareTransferException ex)
        {
            MarkResultStale($"{ex.Message} Результат устарел. Запустите сравнение ещё раз.");
        }
        catch (IOException)
        {
            MarkResultStale("Не удалось записать данные в целевой проект. Результат устарел; проверьте проект и запустите сравнение ещё раз.");
        }
        catch (UnauthorizedAccessException)
        {
            MarkResultStale("Нет доступа для записи в целевой проект. Результат устарел; проверьте права и запустите сравнение ещё раз.");
        }
        catch
        {
            MarkResultStale("Перенос не завершён. Результат устарел, проверьте целевой источник и запустите сравнение ещё раз.");
        }
        finally
        {
            SetApplying(false);
            if (_closeAfterTransfer) Close();
            else if (applied) CompareButton_Click(CompareButton, new RoutedEventArgs());
            else RefreshTransferAvailability();
        }
    }

    private async Task RunMetadataTransferAsync(CompareRow row, bool aToB)
    {
        if (_sourceA is null || _sourceB is null || _applyMetadataTransferAsync is null) return;
        bool applied = false;
        SetApplying(true);
        try
        {
            var preview = await CompareMetadataTransferPreview.EvaluateAsync(_sourceA, _sourceB, row.Difference, aToB);
            if (!preview.IsAllowed) { MarkResultStale(preview.BlockReason ?? "Перенос недоступен."); return; }
            if (_closeAfterTransfer) return;
            var direction = aToB ? "A → B" : "B → A";
            string message = $"{preview.Description}\n\nСейчас в цели\n{(aToB ? row.B : row.A)}\n\nИз источника\n{(aToB ? row.A : row.B)}";
            if (MessageBox.Show(this, message, $"Записать изменения · {direction}", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (_closeAfterTransfer || _isTransferAuthorized?.Invoke(row.Difference) != true) return;
            await _applyMetadataTransferAsync(preview, CancellationToken.None);
            applied = true;
        }
        catch (CompareTransferException ex) { MarkResultStale($"{ex.Message} Результат устарел. Запустите сравнение ещё раз."); }
        catch (IOException) { MarkResultStale("Не удалось записать настройки. Результат устарел; проверьте проект и запустите сравнение ещё раз."); }
        catch (UnauthorizedAccessException) { MarkResultStale("Нет доступа для записи. Результат устарел; проверьте права и запустите сравнение ещё раз."); }
        catch { MarkResultStale("Перенос не завершён. Проверьте целевой источник и запустите сравнение ещё раз."); }
        finally
        {
            SetApplying(false);
            if (_closeAfterTransfer) Close();
            else if (applied) CompareButton_Click(CompareButton, new RoutedEventArgs());
            else RefreshTransferAvailability();
        }
    }

    private void MarkResultStale(string message)
    {
        if (_result is null) return;
        _resultIsStale = true;
        ProgressText.Text = message;
        RefreshTransferAvailability();
    }

    private void ReleaseSources()
    {
        (_sourceA?.Workspace as IDisposable)?.Dispose();
        if (!ReferenceEquals(_sourceA, _sourceB)) (_sourceB?.Workspace as IDisposable)?.Dispose();
        _sourceA = null;
        _sourceB = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _compareCancellation?.Cancel();
        ReleaseSources();
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isApplying)
        {
            e.Cancel = true;
            ProgressText.Text = "Запись выполняется. Дождитесь завершения операции.";
            return;
        }
        if (_isBusy) _compareCancellation?.Cancel();
        base.OnClosing(e);
    }

    private sealed record ScenarioChoice(string Label, bool UsesOnline)
    {
        public override string ToString() => Label;
    }

    private sealed class CompareRow
    {
        public CompareRow(CompareDifference difference, int depth, string? objectIdentity)
        {
            Difference = difference;
            ObjectIdentity = objectIdentity;
            Title = new string(' ', Math.Min(depth, 8) * 2) + difference.Title;
            A = difference.A ?? "—";
            B = difference.B ?? "—";
            _statusText = difference.Status switch
            {
                CompareStatus.Equal => "СОВПАДАЕТ",
                CompareStatus.OnlyA => "ТОЛЬКО A",
                CompareStatus.OnlyB => "ТОЛЬКО B",
                CompareStatus.Changed => "ИЗМЕНЕНО",
                CompareStatus.Ambiguous => "НЕОДНОЗНАЧНО",
                CompareStatus.NotComparable => "НЕ СОПОСТАВИМО",
                CompareStatus.Error => "ОШИБКА",
                _ => "НЕИЗВЕСТНО"
            };
        }

        public CompareDifference Difference { get; }
        public bool IsObjectHeader => Difference.Pair is not null && Difference.Column is null;
        public string Group => Difference.Pair?.A?.Class?.Name ?? Difference.Pair?.B?.Class?.Name
            ?? Difference.Pair?.A?.Schema.TableName ?? Difference.Pair?.B?.Schema.TableName ?? "Диагностика сопоставления";
        public string? ObjectIdentity { get; }
        public string Title { get; }
        public string A { get; }
        public string B { get; }
        public string StatusText => IsContext ? $"КОНТЕКСТ · {_statusText}" : _statusText;
        private readonly string _statusText;
        public bool IsContext { get; set; }
        public bool CanRequestAtoB { get; set; }
        public bool CanRequestBtoA { get; set; }
        public bool CanTransferAtoB { get; private set; }
        public bool CanTransferBtoA { get; private set; }
        public string TransferAtoBReason { get; private set; } = string.Empty;
        public string TransferBtoAReason { get; private set; } = string.Empty;
        public void SetTransfer(bool canAtoB, string reasonAtoB, bool canBtoA, string reasonBtoA)
        {
            CanTransferAtoB = canAtoB;
            TransferAtoBReason = reasonAtoB;
            CanTransferBtoA = canBtoA;
            TransferBtoAReason = reasonBtoA;
        }
    }
}
