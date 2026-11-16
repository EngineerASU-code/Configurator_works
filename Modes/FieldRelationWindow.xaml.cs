using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Configurator.Core.Architecture;

namespace Configurator;

public partial class FieldRelationWindow : Window
{
    private readonly DatabaseService _db;
    private readonly string _database;
    private readonly FieldDefinition _field;
    private List<string> _tables = new();
    private List<ColumnInfo> _columns = new();

    public FieldRelationWindow(DatabaseService db, string database, FieldDefinition field)
    {
        InitializeComponent();
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _database = database ?? "";
        _field = field ?? throw new ArgumentNullException(nameof(field));
        TxtTitle.Text = $"Связь поля «{field.Name}»";
        Loaded += async (_, _) => await LoadTablesAsync();
    }

    private async Task LoadTablesAsync()
    {
        try
        {
            var tables = await _db.GetTableListAsync();
            _tables = tables.Rows.Cast<DataRow>()
                .Select(r => $"{Convert.ToString(r[0])}.{Convert.ToString(r[1])}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            CmbTable.ItemsSource = _tables;
            if (!string.IsNullOrWhiteSpace(_field.LookupTable))
                CmbTable.SelectedItem = _tables.FirstOrDefault(x => x.Equals(_field.LookupTable, StringComparison.OrdinalIgnoreCase));
            if (CmbTable.SelectedItem == null)
                CmbTable.SelectedItem = _tables.FirstOrDefault(x => x.EndsWith("_Type", StringComparison.OrdinalIgnoreCase) || x.EndsWith("_Types", StringComparison.OrdinalIgnoreCase));
            if (CmbTable.SelectedItem == null) TxtStatus.Text = "Выберите таблицу, которая хранит допустимые значения этого поля.";
        }
        catch (Exception ex) { TxtStatus.Text = $"Не удалось получить таблицы: {ex.Message}"; }
    }

    private async void Table_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbTable.SelectedItem is not string table) return;
        try
        {
            _columns = await _db.GetTableColumnsAsync(table);
            var names = _columns.Select(x => x.Name).ToList();
            CmbKey.ItemsSource = names;
            CmbDisplay.ItemsSource = names;
            CmbKey.SelectedItem = names.FirstOrDefault(x => x.Equals(_field.LookupKeyField, StringComparison.OrdinalIgnoreCase))
                                  ?? names.FirstOrDefault(x => x.Equals("Record", StringComparison.OrdinalIgnoreCase))
                                  ?? names.FirstOrDefault();
            CmbDisplay.SelectedItem = names.FirstOrDefault(x => x.Equals(_field.LookupDisplayField, StringComparison.OrdinalIgnoreCase))
                                      ?? names.FirstOrDefault(x => x.Equals("Name", StringComparison.OrdinalIgnoreCase))
                                      ?? CmbKey.SelectedItem;
            TxtStatus.Text = $"Поле «{_field.Name}» будет использовать значения из списка «{table}» ({CmbDisplay.SelectedItem ?? CmbKey.SelectedItem}).";
        }
        catch (Exception ex) { TxtStatus.Text = $"Не удалось получить поля таблицы: {ex.Message}"; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CmbTable.SelectedItem is not string table || CmbKey.SelectedItem is not string key)
        {
            Configurator.Infrastructure.ModernDialog.Show("Выберите таблицу и ключ связи.", "Связь поля", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _field.LookupTable = table;
        _field.LookupKeyField = key;
        _field.LookupDisplayField = CmbDisplay.SelectedItem as string ?? key;
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _field.LookupTable = "";
        _field.LookupKeyField = "";
        _field.LookupDisplayField = "";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
