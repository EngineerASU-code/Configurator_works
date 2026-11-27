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
    private readonly Func<CompareDifference, bool, bool>? _isTransferAuthorized;
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
        Func<CompareDifference, bool, bool>? isTransferAuthorized = null,
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
        KeyDown += Configurator.Infrastructure.ModernDialog.CloseToolOnEnter;

        _onlineDatabases = (onlineDatabases ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _resolveConnectionString = resolveConnectionString;
        _isTransferAuthorized = isTransferAuthorized;
        _canUseOnline = canUseOnline && _onlineDatabases.Length > 0 && resolveConnectionString is not null;
        _canRequestWrite = canRequestWrite;

        ScenarioBox.ItemsSource = _canUseOnline
            ? new[] { new ScenarioChoice("Онлайн и офлайн", true), new ScenarioChoice("Офлайн и офлайн", false), new ScenarioChoice("Онлайн и онлайн", true, true) }
            : new[] { new ScenarioChoice("Офлайн и офлайн", false) };
        ScenarioBox.SelectedIndex = 0;
        OnlineDatabaseA.ItemsSource = _onlineDatabases;
        OnlineDatabaseB.ItemsSource = _onlineDatabases;

        var initialDatabase = _onlineDatabases.FirstOrDefault(name =>
            name.Equals(selectedDatabase, StringComparison.OrdinalIgnoreCase)) ?? _onlineDatabases.FirstOrDefault();
        OnlineDatabaseA.SelectedItem = initialDatabase;
        OnlineDatabaseB.SelectedItem = _onlineDatabases.FirstOrDefault(name => name != initialDatabase) ?? initialDatabase;

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
        {
            source.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            source.AddHook(RepaintAfterDisplayChange);
        }
    }

    private bool _repaintPending;
    private IntPtr _monitor;

    private IntPtr RepaintAfterDisplayChange(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmDpiChanged = 0x02E0, wmExitSizeMove = 0x0232, wmSize = 0x0005;
        const int wmWindowPosChanged = 0x0047;
        bool monitorChanged = false;
        if (message == wmWindowPosChanged)
        {
            var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            monitorChanged = monitor != _monitor;
            _monitor = monitor;
        }
        if (!_repaintPending && (monitorChanged || message is wmDpiChanged or wmExitSizeMove or wmSize))
        {
            _repaintPending = true;
            Dispatcher.InvokeAsync(() =>
            {
                _repaintPending = false;
                if (!_closed)
                    InvalidateRect(hwnd, IntPtr.Zero, false);
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    internal Func<CompareTransferPreview, CancellationToken, Task>? ApplyTransferAsync
    {
        get => _applyTransferAsync;
        set
        {
            _applyTransferAsync = value;
            RefreshTransferAvailability();
        }
    }

    internal event Action<ChangeJournalEntry, string?>? JournalChanged;

    internal Func<CompareMetadataTransferPreview, CancellationToken, Task>? ApplyMetadataTransferAsync
    {
        get => _applyMetadataTransferAsync;
        set { _applyMetadataTransferAsync = value; RefreshTransferAvailability(); }
    }

    private bool UsesOnline => _canUseOnline && ScenarioBox.SelectedItem is ScenarioChoice { UsesOnline: true };

    private bool BothOnline => ScenarioBox.SelectedItem is ScenarioChoice { BothOnline: true };
    private bool IsOnlineSide(bool sideA) => UsesOnline && (BothOnline || sideA == _onlineOnA);

    private void ScenarioBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        RefreshScenarioControls();
        MarkResultStale("Сценарий изменился. Запустите сравнение ещё раз.");
    }

    private void RefreshScenarioControls()
    {
        bool sideAOnline = IsOnlineSide(true);
        bool sideBOnline = IsOnlineSide(false);
        OnlineDatabaseA.Visibility = sideAOnline ? Visibility.Visible : Visibility.Collapsed;
        OfflinePathA.Visibility = sideAOnline ? Visibility.Collapsed : Visibility.Visible;
        OpenOfflineA.Visibility = sideAOnline ? Visibility.Collapsed : Visibility.Visible;
        OnlineDatabaseB.Visibility = sideBOnline ? Visibility.Visible : Visibility.Collapsed;
        OfflinePathB.Visibility = sideBOnline ? Visibility.Collapsed : Visibility.Visible;
        OpenOfflineB.Visibility = sideBOnline ? Visibility.Collapsed : Visibility.Visible;
        FlipButton.IsEnabled = !_isBusy && (UsesOnline || !string.IsNullOrWhiteSpace(_offlinePathA) || !string.IsNullOrWhiteSpace(_offlinePathB));
        UpdatePathDisplay();
    }

    private void OnlineDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingDatabases) return;
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

        SelectOfflineProject(sideA, dialog.FileName);
    }

    internal void SelectOfflineProject(bool sideA, string path)
    {
        if (sideA) _offlinePathA = path;
        else _offlinePathB = path;
        RefreshScenarioControls();
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
        _isUpdatingDatabases = true;
        (OnlineDatabaseA.SelectedItem, OnlineDatabaseB.SelectedItem) = (OnlineDatabaseB.SelectedItem, OnlineDatabaseA.SelectedItem);
        _isUpdatingDatabases = false;
        (_offlinePathA, _offlinePathB) = (_offlinePathB, _offlinePathA);
        _onlineOnA = !_onlineOnA;
        RefreshScenarioControls();
        MarkResultStale("Стороны поменялись местами. Запустите сравнение ещё раз.");
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e) => await CompareAsync();

    private async Task CompareAsync(bool duringTransfer = false)
    {
        if (_isBusy || (_isApplying && !duringTransfer)) return;
        if (!TryGetSelections(out var databaseA, out var databaseB, out var pathA, out var pathB, out var validationMessage))
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
            CancellationToken cancellationToken = _compareCancellation.Token;
            Task<CompareSource> OpenSide(string? database, string? path)
            {
                if (database is null)
                    return Task.Run(() => CompareSourceReader.OpenOfflineAsync(path!, _canRequestWrite, cancellationToken), cancellationToken);
                string connection = _resolveConnectionString!(database);
                return Task.Run(() => CompareSourceReader.OpenOnlineAsync(connection, database, _canRequestWrite, cancellationToken), cancellationToken);
            }

            _sourceA = await OpenSide(databaseA, pathA);
            _sourceB = await OpenSide(databaseB, pathB);

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

    private bool TryGetSelections(out string? databaseA, out string? databaseB, out string? pathA, out string? pathB, out string message)
    {
        bool aOnline = IsOnlineSide(true);
        bool bOnline = IsOnlineSide(false);
        databaseA = aOnline ? OnlineDatabaseA.SelectedItem as string : null;
        databaseB = bOnline ? OnlineDatabaseB.SelectedItem as string : null;
        pathA = aOnline ? null : _offlinePathA;
        pathB = bOnline ? null : _offlinePathB;
        if ((aOnline && databaseA is null) || (bOnline && databaseB is null))
        {
            message = "Выберите подключённую онлайн-базу для каждой онлайн-стороны.";
            return false;
        }
        if (BothOnline && string.Equals(databaseA, databaseB, StringComparison.OrdinalIgnoreCase))
        {
            message = "Выберите две разные онлайн-базы.";
            return false;
        }
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
            (UsesOnline || !string.IsNullOrWhiteSpace(_offlinePathA) || !string.IsNullOrWhiteSpace(_offlinePathB));
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
    }

    private void RefreshRows()
    {
        if (_isInitializing || SectionBox.SelectedItem is not ComparisonSection section) return;
        var rows = new List<CompareRow>();
        AppendRows(section.Rows, rows, 0);
        var view = new System.Windows.Data.ListCollectionView(rows);
        if (section.Rows == _objectDifferences)
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(CompareRow.Group)));
        LeftGrid.ItemsSource = view;
    }

    private void AppendRows(IEnumerable<CompareDifference> differences, List<CompareRow> rows, int depth)
    {
        foreach (var difference in differences)
        {
            if (IsDifference(difference))
            {
                var row = new CompareRow(difference, depth);
                UpdateTransferAvailability(row);
                rows.Add(row);
            }
            if (difference.Children is not null)
                AppendRows(difference.Children, rows, depth + 1);
        }
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

        bool authorized = _isTransferAuthorized?.Invoke(row.Difference, true) == true;
        bool authorizedReverse = _isTransferAuthorized?.Invoke(row.Difference, false) == true;
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
                !_resultIsStale && !_isBusy && !_isApplying && authorizedReverse && bToAAllowed,
                MetadataTransferReason(_resultIsStale, authorizedReverse, bToAAllowed, "Перенести из проекта B в проект A."));
            return;
        }

        var aToB = CompareTransferPreview.Evaluate(_sourceA, _sourceB, row.Difference, aToB: true);
        var bToA = CompareTransferPreview.Evaluate(_sourceA, _sourceB, row.Difference, aToB: false);
        string aToBReason = TransferReason(aToB, authorized, "Перенести из проекта A в проект B.");
        string bToAReason = TransferReason(bToA, authorizedReverse, "Перенести из проекта B в проект A.");
        row.SetTransfer(
            !_resultIsStale && !_isBusy && !_isApplying && _applyTransferAsync is not null && authorized && aToB.IsAllowed,
            _resultIsStale ? "Результат устарел. Запустите сравнение ещё раз." : aToBReason,
            !_resultIsStale && !_isBusy && !_isApplying && _applyTransferAsync is not null && authorizedReverse && bToA.IsAllowed,
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
        double elementWidth = Math.Floor(width * 0.7);
        grid.Columns[0].Width = elementWidth;
        grid.Columns[1].Width = width - elementWidth;
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

    private void SideSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeftGrid is null || RightGrid is null || CopyToA is null || CopyToB is null) return;
        var grid = (DataGrid)sender;
        if (grid.SelectedItems.Count > 0)
            (grid == LeftGrid ? RightGrid : LeftGrid).UnselectAll();
        UpdateArrow(LeftGrid, CopyToB, true);
        UpdateArrow(RightGrid, CopyToA, false);
    }

    private void UpdateArrow(DataGrid grid, Button button, bool aToB)
    {
        var rows = grid.SelectedItems.Cast<CompareRow>().ToArray();
        var blocked = rows.FirstOrDefault(row => !(aToB ? row.CanTransferAtoB : row.CanTransferBtoA));
        button.IsEnabled = rows.Length > 0 && blocked is null && !_isBusy && !_isApplying && !_resultIsStale;
        var hint = blocked ?? rows.FirstOrDefault();
        button.ToolTip = hint is null ? "Выберите различие" :
            aToB ? hint.TransferAtoBReason : hint.TransferBtoAReason;
    }

    private async void TransferAtoB_Click(object sender, RoutedEventArgs e) => await TransferSelectedAsync(true);
    private async void TransferBtoA_Click(object sender, RoutedEventArgs e) => await TransferSelectedAsync(false);

    private async Task TransferSelectedAsync(bool aToB)
    {
        if (_closed || _isBusy || _isApplying || _resultIsStale || _result is null) return;
        var grid = aToB ? LeftGrid : RightGrid;
        var selected = grid.SelectedItems.Cast<CompareRow>().OrderBy(grid.Items.IndexOf)
            .Select(row => row.Difference).ToArray();
        if (selected.Length == 0) return;
        var selectedObjects = selected.Where(row => row.Column is null && row.Pair is not null)
            .Select(row => row.Pair!).ToHashSet();

        SetApplying(true);
        UpdateArrow(LeftGrid, CopyToB, true);
        UpdateArrow(RightGrid, CopyToA, false);
        ProgressText.Text = "Перенос различий…";
        try
        {
            foreach (var original in selected)
            {
                if (_closed || _closeAfterTransfer || _resultIsStale || _result is null) break;
                if (original.Column is not null && original.Pair is { } pair && selectedObjects.Contains(pair))
                    continue;
                var differences = original.MetadataKind is null ? _result.Objects : _result.Architecture;
                var matches = FlattenDifferences(differences).Where(row => SameDifference(original, row)).Take(2).ToArray();
                if (matches.Length != 1)
                {
                    ProgressText.Text = "Выбранное различие изменилось. Проверьте оставшиеся строки.";
                    break;
                }
                if (!await RunTransferAsync(matches[0], aToB)) break;
                if (_resultIsStale && !_closeAfterTransfer && !_closed)
                {
                    await CompareAsync(duringTransfer: true);
                    continue;
                }
                ProgressText.Text = "Перенос выполнен.";
            }
        }
        finally
        {
            SetApplying(false);
            if (_closeAfterTransfer) Close();
            else if (_result is not null)
            {
                PopulateResult(_result);
                RefreshTransferAvailability();
            }
        }

    }

    private static IEnumerable<CompareDifference> FlattenDifferences(IEnumerable<CompareDifference> differences)
    {
        foreach (var difference in differences)
        {
            yield return difference;
            if (difference.Children is not null)
                foreach (var child in FlattenDifferences(difference.Children))
                    yield return child;
        }
    }

    private static bool SameDifference(CompareDifference x, CompareDifference y) =>
        x.Title == y.Title && x.Column == y.Column && x.MetadataKind == y.MetadataKind &&
        x.MetadataKey == y.MetadataKey && x.Status == y.Status && x.A == y.A && x.B == y.B &&
        x.Pair?.A?.Schema.TableName == y.Pair?.A?.Schema.TableName &&
        x.Pair?.B?.Schema.TableName == y.Pair?.B?.Schema.TableName &&
        x.Pair?.RecordA?.Key == y.Pair?.RecordA?.Key && x.Pair?.RecordB?.Key == y.Pair?.RecordB?.Key;

    private async Task<bool> RunTransferAsync(CompareDifference difference, bool aToB)
    {
        if (_sourceA is null || _sourceB is null) return false;
        if (_isTransferAuthorized?.Invoke(difference, aToB) != true)
        {
            const string message = "Нет разрешения на добавление или изменение записи в режиме 3.";
            ProgressText.Text = message;
            SessionTestLog.Trace("ПЕРЕНОС", message);
            return false;
        }

        if (difference.MetadataKind is not null)
        {
            return await RunMetadataTransferAsync(difference, aToB);
        }

        if (_applyTransferAsync is null) return false;
        var preview = CompareTransferPreview.Evaluate(_sourceA, _sourceB, difference, aToB);
        if (!preview.IsAllowed)
        {
            ProgressText.Text = preview.BlockReason ?? "Перенос недоступен.";
            SessionTestLog.Trace("ПЕРЕНОС", $"{(aToB ? "A → B" : "B → A")} · {ProgressText.Text}");
            return false;
        }
        bool applied = false;
        bool changedKey = false;
        try
        {
            try
            {
                await _applyTransferAsync(preview, CancellationToken.None);
            }
            catch (CompareRecordConflictException)
            {
                var conflict = new PasteConflict
                {
                    Selected = true, Number = 1, Key = preview.KeyValues.ToDictionary(k => k.Key, k => k.Value!),
                    Reason = "Record занят в целевой таблице. Введите новый Record."
                };
                var dialog = new PasteConflictsWindow(new() { conflict }, true, async (_, _) =>
                {
                    if (_closeAfterTransfer || _resultIsStale || _isTransferAuthorized?.Invoke(difference, aToB) != true)
                        throw new CompareTransferException("Перенос больше недоступен. Повторите сравнение.");
                    var replacement = preview.WithEdits(new Dictionary<string, string?> { ["Record"] = conflict.NewRecord });
                    await _applyTransferAsync(replacement, CancellationToken.None);
                    changedKey = true;
                    preview = replacement;
                }) { Owner = this };
                if (dialog.ShowDialog() != true)
                {
                    ProgressText.Text = "Перенос отменён.";
                    return false;
                }
            }
            applied = true;
            if (preview.RecordedEntry is { } entry) JournalChanged?.Invoke(entry, preview.Target.PackagePath);
            if (_closeAfterTransfer || _resultIsStale) return false;
            if (changedKey)
            {
                MarkResultStale("Ключ изменился. Обновляем сравнение…");
                return true;
            }
            RefreshOfflineTarget(preview.Target, preview.SavedPackage, aToB);
            var pair = difference.Pair!;
            var refreshed = await Task.Run(() => CompareEngine.RefreshObjectAsync(pair, (aToB ? _sourceB : _sourceA)!, preview, aToB));
            if (_closeAfterTransfer || _resultIsStale) return false;
            var result = _result!;
            var objects = result.Objects.Where(row => !SameObject(row.Pair, pair) || refreshed is not null)
                .Select(row => SameObject(row.Pair, pair) ? refreshed! : row).ToArray();
            _result = result with { Objects = objects, CompletedUtc = DateTimeOffset.UtcNow };
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
        return applied && !_resultIsStale;
    }

    private static bool SameObject(CompareRecordPair? candidate, CompareRecordPair pair) =>
        candidate is not null && ReferenceEquals(candidate.A, pair.A) && ReferenceEquals(candidate.B, pair.B) &&
        ReferenceEquals(candidate.RecordA, pair.RecordA) && ReferenceEquals(candidate.RecordB, pair.RecordB);

    private void RefreshOfflineTarget(CompareSource target, (ConfiguratorProject Project, string Fingerprint)? savedPackage, bool aToB)
    {
        if (!target.Workspace.IsOffline) return;
        if (target.PackagePath is not { } path || savedPackage is not { } saved)
            throw new CompareTransferException("Не удалось подтвердить новую версию локального проекта.");

        var updated = CompareSourceReader.CreateOfflineSource(path, saved.Project, saved.Fingerprint, _canRequestWrite);
        if (aToB) _sourceB = updated;
        else _sourceA = updated;
        (target.Workspace as IDisposable)?.Dispose();
    }

    private async Task<bool> RunMetadataTransferAsync(CompareDifference difference, bool aToB)
    {
        if (_sourceA is null || _sourceB is null || _applyMetadataTransferAsync is null) return false;
        bool applied = false;
        try
        {
            var preview = await CompareMetadataTransferPreview.EvaluateAsync(_sourceA, _sourceB, difference, aToB);
            if (!preview.IsAllowed) { MarkResultStale(preview.BlockReason ?? "Перенос недоступен."); return false; }
            if (_closeAfterTransfer || _resultIsStale || _isTransferAuthorized?.Invoke(difference, aToB) != true) return false;
            await _applyMetadataTransferAsync(preview, CancellationToken.None);
            applied = true;
            if (preview.RecordedEntry is { } entry) JournalChanged?.Invoke(entry, preview.Target.PackagePath);
            if (_closeAfterTransfer || _resultIsStale) return false;
            if (difference.MetadataKind is not (CompareMetadataKind.ProjectTextList or CompareMetadataKind.ThemePreset))
            {
                MarkResultStale("Структура проекта изменилась. Обновляем сравнение…");
                return true;
            }
            RefreshOfflineTarget(preview.Target, preview.SavedPackage, aToB);
            var settings = await Task.Run(() => CompareEngine.CompareProjectSettingsAsync(_sourceA!, _sourceB!, CancellationToken.None));
            if (_closeAfterTransfer || _resultIsStale) return false;
            var result = _result!;
            _result = result with
            {
                Architecture = result.Architecture.Where(row => row.MetadataKind is not
                    (CompareMetadataKind.ProjectTextList or CompareMetadataKind.ThemePreset)).Concat(settings).ToArray(),
                CompletedUtc = DateTimeOffset.UtcNow
            };
        }
        catch (CompareTransferException ex) { MarkResultStale($"{ex.Message} Результат устарел. Запустите сравнение ещё раз."); }
        catch (IOException) { MarkResultStale("Не удалось записать настройки. Результат устарел; проверьте проект и запустите сравнение ещё раз."); }
        catch (UnauthorizedAccessException) { MarkResultStale("Нет доступа для записи. Результат устарел; проверьте права и запустите сравнение ещё раз."); }
        catch { MarkResultStale("Перенос не завершён. Проверьте целевой источник и запустите сравнение ещё раз."); }
        return applied && !_resultIsStale;
    }

    internal void RefreshAfterUndo()
    {
        if (_isBusy) _compareCancellation?.Cancel();
        MarkResultStale("Действие отменено. Обновление сравнения…");
        if (!_closed && !_isBusy && !_isApplying && _result is not null) _ = CompareAsync();
    }

    private void MarkResultStale(string message)
    {
        if (_result is null) return;
        _resultIsStale = true;
        ProgressText.Text = message;
        if (!_isApplying) RefreshTransferAvailability();
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

    private sealed record ScenarioChoice(string Label, bool UsesOnline, bool BothOnline = false)
    {
        public override string ToString() => Label;
    }

    private sealed class CompareRow
    {
        public CompareRow(CompareDifference difference, int depth)
        {
            Difference = difference;
            Title = new string(' ', Math.Min(depth, 8) * 2) + difference.Title;
            A = difference.A ?? "—";
            B = difference.B ?? "—";
            if (IsObjectHeader && difference.Pair is { } pair)
            {
                A = ObjectLabel(pair.RecordA);
                B = ObjectLabel(pair.RecordB);
            }
        }

        private static string ObjectLabel(ProjectRecord? record)
        {
            if (record is null) return "Отсутствует";
            foreach (string field in new[] { "Name", "Tag", "Name_L1", "NameL1" })
            {
                var value = record.Values.FirstOrDefault(entry => entry.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "Без имени";
        }

        public bool? HasRecordA => Difference.Pair is { } pair ? pair.RecordA is not null : null;
        public bool? HasRecordB => Difference.Pair is { } pair ? pair.RecordB is not null : null;
        public CompareDifference Difference { get; }
        public bool IsObjectHeader => Difference.Pair is not null && Difference.Column is null;
        public string Group => Difference.Pair?.A?.Class?.Name ?? Difference.Pair?.B?.Class?.Name
            ?? Difference.Pair?.A?.Schema.TableName ?? Difference.Pair?.B?.Schema.TableName ?? "Диагностика сопоставления";
        public string Title { get; }
        public string A { get; }
        public string B { get; }
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
