using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace Configurator;

public sealed class BatchObjectRow
{
    public string Record { get; set; } = "";
    public string PlcKey { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class BatchObjectPlcOption
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
}

public partial class MultiObjectEditorWindow : Window
{
    private readonly HashSet<string> _existingNames;
    private readonly HashSet<string> _existingRecordKeys;
    private readonly bool _hasRecord;
    private readonly bool _requirePlc;
    private readonly Func<IReadOnlyList<BatchObjectRow>, Task<string>> _submit;
    private readonly ObservableCollection<BatchObjectRow> _rows = new();
    private bool _submitting;

    public IReadOnlyList<BatchObjectRow> Rows => _rows.ToList();
    public IReadOnlyList<BatchObjectPlcOption> PlcOptions { get; }

    public MultiObjectEditorWindow(string className, IEnumerable<(string Key, string Title)> plcOptions,
        IEnumerable<string> existingNames, IEnumerable<string> existingRecordKeys, bool hasRecord, bool requirePlc,
        Func<IReadOnlyList<BatchObjectRow>, Task<string>> submit)
    {
        InitializeComponent();
        Title = $"Добавить несколько объектов — {className}";
        TitleText.Text = $"Массовое добавление: {className}";
        _existingNames = new HashSet<string>(existingNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _existingRecordKeys = new HashSet<string>(existingRecordKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _hasRecord = hasRecord;
        _requirePlc = requirePlc;
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        PlcOptions = (plcOptions ?? Array.Empty<(string, string)>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => new BatchObjectPlcOption { Key = x.Key, Title = string.IsNullOrWhiteSpace(x.Title) ? x.Key : x.Title }).ToList();
        RecordColumn.Visibility = hasRecord ? Visibility.Visible : Visibility.Collapsed;
        PlcColumn.Visibility = requirePlc || PlcOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hasRecord)
            InputHint.Text += " Номер Record подбирается автоматически и доступен для редактирования.";
        RowsGrid.ItemsSource = _rows;
        Loaded += (_, _) => FitToOwnerMonitor();
        AddRow();
    }

    private void FitToOwnerMonitor()
    {
        var workArea = GetOwnerWorkArea();
        double maxHeight = Math.Max(1, workArea.Height - 40);
        double maxWidth = Math.Max(1, workArea.Width - 40);
        Width = Math.Min(960, maxWidth);
        MinWidth = Math.Min(720, Width);
        MinHeight = Math.Min(420, maxHeight);
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;

        Height = Math.Min(Height, maxHeight);

        if (IsLoaded && Owner != null)
        {
            Left = Math.Clamp(Owner.Left + (Owner.ActualWidth - Width) / 2, workArea.Left, workArea.Right - Width);
            Top = Math.Clamp(Owner.Top + (Owner.ActualHeight - Height) / 2, workArea.Top, workArea.Bottom - Height);
        }
    }

    private Rect GetOwnerWorkArea()
    {
        if (Owner == null) return SystemParameters.WorkArea;

        var ownerHandle = new WindowInteropHelper(Owner).Handle;
        var monitor = MonitorFromWindow(ownerHandle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(ownerHandle, out var ownerRect))
            return SystemParameters.WorkArea;

        uint dpi = 96;
        try { dpi = GetDpiForWindow(ownerHandle); }
        catch (EntryPointNotFoundException) { }
        double scale = Math.Max(1, dpi) / 96d;
        return new Rect(
            Owner.Left + (info.Work.Left - ownerRect.Left) / scale,
            Owner.Top + (info.Work.Top - ownerRect.Top) / scale,
            (info.Work.Right - info.Work.Left) / scale,
            (info.Work.Bottom - info.Work.Top) / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    private string FindFreeRecord(string plcKey)
    {
        if (!_hasRecord) return string.Empty;
        var pending = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Record))
            .Select(r => RecordKey(r.PlcKey, r.Record));
        return Configurator.Core.Workspace.BatchObjectPolicy
            .NextFreeRecord(plcKey, _existingRecordKeys, pending).ToString();
    }

    private static string RecordKey(string plc, string record)
        => Configurator.Core.Workspace.BatchObjectPolicy.RecordKey(plc, record);

    private void AddRow()
    {
        var plc = PlcOptions.FirstOrDefault();
        _rows.Add(new BatchObjectRow { PlcKey = plc?.Key ?? "", Record = FindFreeRecord(plc?.Key ?? "") });
        SetValidation(string.Empty);
        RowsGrid.SelectedIndex = _rows.Count - 1;
        RowsGrid.ScrollIntoView(RowsGrid.SelectedItem);
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        AddRow();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_rows.Count == 0) return;
            RowsGrid.CurrentCell = new DataGridCellInfo(_rows[^1], NameColumn);
            RowsGrid.BeginEdit();
        }));
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BatchObjectRow row) _rows.Remove(row);
        if (_rows.Count == 0) AddRow();
        SetValidation(string.Empty);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_submitting)
        {
            e.Handled = true;
            return;
        }
        bool editingText = RowsGrid.IsKeyboardFocusWithin
            && (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem);
        if (e.Key == Key.Escape)
        {
            var openCombo = FindVisualChildren<ComboBox>(RowsGrid).FirstOrDefault(combo => combo.IsDropDownOpen);
            if (openCombo != null)
            {
                openCombo.IsDropDownOpen = false;
                e.Handled = true;
                return;
            }
            if (RowsGrid.IsKeyboardFocusWithin
                && (RowsGrid.CancelEdit(DataGridEditingUnit.Cell) || RowsGrid.CancelEdit(DataGridEditingUnit.Row)))
            {
                e.Handled = true;
                return;
            }
            DialogResult = false;
            e.Handled = true;
            return;
        }

        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.Enter)
        {
            Accept_Click(BtnAccept, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (!editingText && e.Key == Key.Insert)
        {
            AddRow_Click(BtnAddRow, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (!editingText && e.Key == Key.Delete && RowsGrid.SelectedItem is BatchObjectRow row)
        {
            _rows.Remove(row);
            if (_rows.Count == 0) AddRow();
            SetValidation(string.Empty);
            e.Handled = true;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void SetValidation(string message)
    {
        ValidationText.Text = message ?? string.Empty;
        ValidationPanel.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_submitting) return;
        SetValidation(string.Empty);
        if (!RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !RowsGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var errors = Configurator.Core.Workspace.BatchObjectPolicy.ValidateRows(
            _rows.Select(r => (r.Record, r.PlcKey, r.Name)),
            _existingNames,
            _existingRecordKeys,
            _hasRecord,
            _requirePlc,
            PlcOptions.Select(p => p.Key));
        if (errors.Count > 0) { SetValidation(string.Join(Environment.NewLine, errors)); return; }
        foreach (var row in _rows) row.Name = row.Name.Trim();
        _submitting = true;
        RowsGrid.IsEnabled = BtnAddRow.IsEnabled = BtnAccept.IsEnabled = BtnCancel.IsEnabled = false;
        bool saved = false;
        try
        {
            var error = await _submit(_rows.ToList());
            if (!string.IsNullOrWhiteSpace(error)) { SetValidation(error); return; }
            saved = true;
        }
        catch (Exception ex)
        {
            SetValidation("Не удалось добавить объекты: " + ex.Message);
        }
        finally
        {
            _submitting = false;
            RowsGrid.IsEnabled = BtnAddRow.IsEnabled = BtnAccept.IsEnabled = BtnCancel.IsEnabled = true;
        }
        if (saved) DialogResult = true;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) => e.Cancel = _submitting;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
