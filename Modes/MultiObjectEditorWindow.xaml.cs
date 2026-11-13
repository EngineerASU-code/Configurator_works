using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;

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
            .Select(x => new BatchObjectPlcOption { Key = x.Key, Title = x.Title }).ToList();
        var plcColumn = RowsGrid.Columns.OfType<DataGridComboBoxColumn>().First();
        plcColumn.ItemsSource = PlcOptions;
        RowsGrid.ItemsSource = _rows;
        AddRow();
    }

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
        RowsGrid.SelectedIndex = _rows.Count - 1;
        RowsGrid.ScrollIntoView(RowsGrid.SelectedItem);
    }

    private void AddRow_Click(object sender, RoutedEventArgs e) => AddRow();

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BatchObjectRow row) _rows.Remove(row);
        if (_rows.Count == 0) AddRow();
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var errors = Configurator.Core.Workspace.BatchObjectPolicy.ValidateRows(
            _rows.Select(r => (r.Record, r.PlcKey, r.Name)),
            _existingNames,
            _existingRecordKeys,
            _hasRecord,
            _requirePlc,
            PlcOptions.Select(p => p.Key));
        if (errors.Count > 0) { ValidationText.Text = string.Join(Environment.NewLine, errors); return; }
        foreach (var row in _rows) row.Name = row.Name.Trim();
        try
        {
            var error = await _submit(_rows.ToList());
            if (!string.IsNullOrWhiteSpace(error)) { ValidationText.Text = error; return; }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ValidationText.Text = "Не удалось добавить объекты: " + ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
