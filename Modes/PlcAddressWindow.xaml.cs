#nullable enable
using System.Windows;
using System.Windows.Controls;
using Configurator.Core.Projects;
using Configurator.Core.Workspace;
using Configurator.Infrastructure;

namespace Configurator;

internal sealed record AddressMapSource(string Name, Func<CancellationToken, Task<ProjectWorkspace>> Open,
    bool CanEdit = false, string? Path = null, string? Server = null, string? Database = null,
    Func<AddressOwner, string, CancellationToken, Task<ChangeJournalRowChange>>? ReassignOffline = null)
{
    public override string ToString() => Name;
}

public partial class PlcAddressWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private ProjectWorkspace? _workspace;
    private readonly Func<string, CancellationToken, Task<ProjectWorkspace>> _openFile;
    private readonly Action<ChangeJournalEntry>? _changeJournalRequested;
    private SortedDictionary<string, PlcAddressMap>? _maps;
    private List<AddressByteRow> _rows = [];
    private sealed record Cell(string Caption, string State, string Detail, char Area, IReadOnlyList<string> Owners) : System.ComponentModel.INotifyPropertyChanged
    {
        public bool IsSelected { get; private set; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    internal PlcAddressWindow(IEnumerable<AddressMapSource> sources, Func<string, CancellationToken, Task<ProjectWorkspace>> openFile,
        Action<ChangeJournalEntry>? changeJournalRequested = null, AddressMapSource? initialSource = null)
    {
        InitializeComponent();
        KeyDown += Configurator.Infrastructure.ModernDialog.CloseToolOnEnter;
        _openFile = openFile;
        _changeJournalRequested = changeJournalRequested;
        foreach (var source in sources) Project.Items.Add(source);
        if (initialSource != null) Loaded += (_, _) => Project.SelectedItem ??= initialSource;
    }

    private void BrowseProject(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Проекты Configurator (*.cfgproj)|*.cfgproj" };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;
        var source = Project.Items.Cast<AddressMapSource>().FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        if (source == null)
        {
            source = new($"Offline · {path}", ct => _openFile(path, ct), Path: path);
            Project.Items.Add(source);
        }
        Project.SelectedItem = source;
    }

    private async void SelectProject(object sender, SelectionChangedEventArgs e)
    {
        if (Project.SelectedItem is not AddressMapSource source) return;
        Project.IsEnabled = Browse.IsEnabled = Plc.IsEnabled = Filter.IsEnabled = false;
        AddressRanges.Text = string.Empty;
        _maps = null;
        Plc.ItemsSource = GridRows.ItemsSource = Issues.ItemsSource = OwnerList.ItemsSource = null;
        OwnerList.Tag = null;
        DetailTitle.Text = "Назначения";
        IssuesTab.Header = "Нераспознанные адреса";
        Filter.SelectedIndex = 0;
        Status.Text = "Чтение адресов проекта…";
        _workspace?.Dispose();
        _workspace = null;
        var ct = _lifetime.Token;
        try
        {
            var workspace = await source.Open(ct);
            if (ct.IsCancellationRequested) { workspace.Dispose(); return; }
            _workspace = workspace;
            await ReloadAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Status.Text = UserErrorMessage.Format(ex); }
        finally
        {
            Project.IsEnabled = Browse.IsEnabled = true;
            if (_maps == null) Project.SelectedItem = null;
        }
    }

    private async Task ReloadAsync(string? selectedPlc = null)
    {
        var ct = _lifetime.Token;
        AddressRanges.Text = string.Empty;
        _maps = null;
        Plc.ItemsSource = GridRows.ItemsSource = OwnerList.ItemsSource = Issues.ItemsSource = null;
        OwnerList.Tag = null;
        DetailTitle.Text = "Назначения";
        IssuesTab.Header = "Нераспознанные адреса";
        Filter.IsEnabled = Plc.IsEnabled = false;
        try
        {
            _maps = await Task.Run(() => PlcAddressCatalog.LoadAsync(_workspace!, ct), ct);
            if (ct.IsCancellationRequested) return;
            Plc.ItemsSource = _maps.Keys.ToList();
            if (selectedPlc != null && _maps.ContainsKey(selectedPlc)) Plc.SelectedItem = selectedPlc;
            else if (_maps.Count > 0) Status.Text = "Выберите PLC.";
            else Status.Text = "В проекте нет PLC или адресуемых элементов с указанным PLC.";
            Plc.IsEnabled = _maps.Count > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Status.Text = UserErrorMessage.Format(ex); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _workspace?.Dispose();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void SelectPlc(object sender, SelectionChangedEventArgs e)
    {
        if (Plc.SelectedItem is not string plc || _maps == null) return;
        Filter.IsEnabled = true;
        var map = _maps[plc];
        _rows = map.Rows();
        Issues.ItemsSource = map.Issues;
        IssuesTab.Header = $"Нераспознанные адреса ({map.Issues.Count})";
        RefreshRows();
    }

    private void SelectFilter(object sender, SelectionChangedEventArgs e)
    {
        if (Plc.SelectedItem != null) RefreshRows();
    }

    private void RefreshRows()
    {
        OwnerList.Tag = null;
        OwnerList.ItemsSource = null;
        DetailTitle.Text = "Назначения";
        var map = _maps![(string)Plc.SelectedItem];
        var type = (SignalType.SelectedItem as ComboBoxItem)?.Content as string;
        var matching = SignalType.SelectedIndex > 0 && type != null
            ? _rows.Where(row => map.ContainsSignal(row, type)).ToList() : _rows;
        var visible = matching.Where(r => Filter.SelectedIndex switch { 1 => r.HasFree, 2 => r.HasConflict, _ => true }).ToList();
        var ranges = matching.GroupBy(r => r.Area).Select(g => $"{g.Key}{g.First().First}–{g.Key}{g.Last().Last}");
        AddressRanges.Text = string.Join("     ·     ", ranges);
        Status.Text = _rows.Count == 0 ? "Распознанных адресов у этого PLC нет." : string.Empty;
        if (_rows.Count > 0 && visible.Count == 0) Status.Text = "По этому фильтру байтов нет.";
        if (Issues.Items.Count > 0) Status.Text += "   Карта неполная. Проверьте нераспознанные адреса.";
        var groups = map.SpanGroups();
        int plainRun = 0;
        bool previousGrouped = true;
        var rows = visible.Select(row =>
        {
            bool grouped = groups.TryGetValue((row.Area, row.First), out var span);
            if (!grouped && previousGrouped) plainRun++;
            previousGrouped = grouped;
            return new
            {
            row.Label,
            Span = span ?? $"#{plainRun}",
            Grouped = grouped,
            Cells = row.Bits.Select((owners, bit) =>
            {
                var state = owners.Count switch { 0 => "Свободен", 1 => "Занят", _ => "Конфликт" };
                var address = row.First == row.Last ? $"{row.Label}.{bit}" : $"{row.Area}{row.First}.{bit}–{row.Area}{row.Last}.{bit} (бит {bit} каждого байта)";
                return new Cell($"{bit} · {state}", state, $"{address} · {state}", row.Area, owners);
            }).ToList()
            };
        }).ToList();
        var view = new System.Windows.Data.ListCollectionView(rows);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Span"));
        GridRows.ItemsSource = view;
    }

    private void SelectBit(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Cell cell }) return;
        if (OwnerList.Tag is Cell previous) previous.SetSelected(false);
        cell.SetSelected(true);
        OwnerList.Tag = cell;
        OwnerList.ItemsSource = cell.Owners;
        DetailTitle.Text = cell.Detail.Split(Environment.NewLine)[0];
        if (Project.SelectedItem is AddressMapSource { CanEdit: true } && cell.State == "Конфликт")
            DetailTitle.Text += "   ·   Дважды нажмите на объект, чтобы изменить адрес";
    }

    private async void EditSelectedOwner(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(OwnerList, source) is not ListBoxItem { Content: string owner } ||
            OwnerList.Tag is not Cell cell) return;
        await EditConflictAsync(cell, owner);
    }

    private async void OwnerKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || OwnerList.Tag is not Cell cell || OwnerList.SelectedItem is not string owner) return;
        e.Handled = true;
        await EditConflictAsync(cell, owner);
    }

    private async void EditBit(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: Cell cell }) await EditConflictAsync(cell, null);
    }

    private async Task EditConflictAsync(Cell cell, string? preferredOwner)
    {
        if (cell.State != "Конфликт" || Plc.SelectedItem is not string plc || _maps == null) return;
        if (Project.SelectedItem is not AddressMapSource { CanEdit: true } selectedSource)
        {
            MessageBox.Show(this, "Для изменения адресов выберите Online-базу или активный Offline-проект и войдите с правом редактирования.",
                "Карта адресов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var map = _maps[plc];
        if (map.Issues.Count != 0)
        {
            MessageBox.Show(this, "Сначала исправьте нераспознанные адреса: карта может быть неполной.",
                "Карта адресов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new Window { Title = "Разрешение конфликта", Owner = this, Width = 620, Height = 410,
            MinWidth = 500, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var help = new TextBlock { Text = "Выберите назначение, которое нужно изменить, затем свободный совместимый адрес.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var accept = new Button { Content = "Изменить", MinWidth = 100, IsEnabled = false, IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 100, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        bottom.Children.Add(accept); bottom.Children.Add(cancel); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var note = new TextBlock { Text = "Выберите назначение. Изменение доступно для одного адреса в редактируемом поле со стабильным ключом.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(note, Dock.Bottom); panel.Children.Add(note);
        var choices = new ComboBox { Margin = new Thickness(0, 10, 0, 0), MinWidth = 180 };
        DockPanel.SetDock(choices, Dock.Bottom); panel.Children.Add(choices);
        var owners = new ListBox { ItemsSource = cell.Owners };
        panel.Children.Add(owners); dialog.Content = panel;
        AddressOwner? selectedOwner = null;
        owners.SelectionChanged += (_, _) =>
        {
            selectedOwner = owners.SelectedItem is string text ? map.FindOwner(text) : null;
            choices.ItemsSource = selectedOwner == null ? null : map.FreeAddresses(selectedOwner, cell.Area)
                .Select(value => PlcAddressMap.DescribeAddress(value, cell.Area, selectedOwner.Analog)).ToList();
            choices.SelectedIndex = choices.Items.Count > 0 ? 0 : -1;
            note.Text = choices.Items.Count > 0 ? "Показаны первые доступные адреса той же области и ширины." : "Для этого назначения подходящие адреса недоступны: проверьте поле, ключ и формат адреса.";
            accept.IsEnabled = choices.SelectedItem != null;
        };
        choices.SelectionChanged += (_, _) => accept.IsEnabled = selectedOwner != null && choices.SelectedItem != null;
        if (preferredOwner != null) owners.SelectedItem = preferredOwner;
        accept.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || selectedOwner == null || choices.SelectedItem is not PlcAddressMap.Destination destination) return;
        var origin = PlcAddressMap.DescribeAddress(selectedOwner.Raw, cell.Area, selectedOwner.Analog);
        if (MessageBox.Show(this, $"Изменить адрес назначения «{selectedOwner.Display}» с «{origin}» на «{destination}»?",
            "Подтверждение изменения", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var context = $"{_workspace!.DisplayName} · PLC {plc} · {selectedOwner.Display} · {origin} → {destination}";
        bool committed = false;
        try
        {
            IsEnabled = false;
            Status.Text = "Проверка свободного диапазона и сохранение…";
            SessionTestLog.Trace("КАРТА PLC", $"Начато: {context}");
            if (!UserAuthorizationService.Has(Permission.CheckTools) || !UserAuthorizationService.Has(Permission.Mode1Edit))
                throw new UnauthorizedAccessException("Нет права изменять адреса проекта.");
            var token = _lifetime.Token;
            var change = selectedSource.ReassignOffline is { } reassignOffline
                ? await reassignOffline(selectedOwner, destination.Value, token)
                : await Task.Run(() => _workspace!.ReassignPlcAddressAsync(selectedOwner, destination.Value, token), token);
            committed = true;
            if (selectedSource is { Database: not null } && _changeJournalRequested != null)
            {
                var identity = change.IdentityAfter.Select(pair => $"{pair.Key}={pair.Value?.ToObject() ?? "NULL"}");
                _changeJournalRequested(new ChangeJournalEntry
                {
                    Server = selectedSource.Server ?? "",
                    Database = selectedSource.Database,
                    ProjectName = selectedSource.Database,
                    Mode = 1,
                    ProjectType = "Online",
                    Category = "Переназначение адреса",
                    Source = change.TableName,
                    Description = $"Адрес PLC {selectedOwner.Display}: {origin} → {destination.Value}",
                    Details = $"Поле: {selectedOwner.Field} · PLC: {selectedOwner.Plc}",
                    EntityType = "Назначение PLC",
                    EntityIdentity = string.Join(", ", identity),
                    OperationKind = ChangeJournalOperationKind.Update,
                    Changes = new List<ChangeJournalRowChange> { change }
                });
            }
            bool offline = selectedSource.ReassignOffline != null;
            var savedIn = offline ? "Offline-проекте" : "базе данных";
            SessionTestLog.Trace("КАРТА PLC", $"Сохранено в {savedIn}: {context}, {timer.ElapsedMilliseconds} мс");
            Status.Text = "Сохранено. Перечитываем карту…";
            await ReloadAsync(plc);
            if (token.IsCancellationRequested) return;
            if (_maps == null)
            {
                Status.Text = "Запись сохранена. Ошибка перечитывания карты: " + Status.Text;
            }
            else
            {
                var address = Convert.ToString(change.After[selectedOwner.Field].ToObject(), System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                var moved = _maps.TryGetValue(plc, out var refreshed)
                    ? refreshed.FindOwner(selectedOwner.Display + " · " + address) : null;
                Status.Text = moved?.Record.Key == selectedOwner.Record.Key
                    ? $"{selectedOwner.Display}: {origin} → {destination}. Подтверждено при обновлении карты."
                    : $"Запись сохранена, текущее назначение не подтверждено при перечитывании: {context}.";
            }
            SessionTestLog.Trace("КАРТА PLC", $"{Status.Text} · всего {timer.ElapsedMilliseconds} мс");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SessionTestLog.Trace("КАРТА PLC", $"Ошибка (commit={committed}): {context} · {UserErrorMessage.Format(ex)}");
            MessageBox.Show(this, UserErrorMessage.Format(ex), "Не удалось изменить адрес", MessageBoxButton.OK, MessageBoxImage.Warning);
            await ReloadAsync(plc);
        }
        finally { IsEnabled = true; }
    }

}
