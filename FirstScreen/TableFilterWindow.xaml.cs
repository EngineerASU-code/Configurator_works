using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Configurator;

public partial class TableFilterWindow : Window
{
    private sealed record FilterOperator(string Key, string Label);
    private readonly ColumnInfo _column;
    private readonly bool _typedComparison;

    internal TableFilter Result { get; private set; }
    internal TableColumnSort Sort { get; private set; }

    internal TableFilterWindow(ColumnInfo column, TableFilter current, TableColumnSort sort)
    {
        _column = column;
        InitializeComponent();
        ColumnTitle.Text = column.Name;
        var type = column.DataType?.ToLowerInvariant();
        bool ordered = type is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or
            "money" or "smallmoney" or "float" or "real" or "date" or "datetime" or "datetime2" or
            "smalldatetime" or "datetimeoffset" or "time";
        _typedComparison = ordered || type is "bit" or "uniqueidentifier";
        var operators = new List<FilterOperator> { new("", "Без фильтра"), new("Equals", "Равно"), new("Not Equals", "Не равно") };
        if (ordered)
            operators.AddRange(new FilterOperator[] { new("Greater", "Больше"), new("Greater Or Equal", "Больше или равно"),
                new("Less", "Меньше"), new("Less Or Equal", "Меньше или равно") });
        else if (!_typedComparison)
            operators.AddRange(new FilterOperator[] { new("Contains", "Содержит"), new("Not Contains", "Не содержит"),
                new("Starts With", "Начинается с"), new("Ends With", "Заканчивается на") });
        operators.Add(new("Is Null", "Пусто (NULL)"));
        operators.Add(new("Is Not Null", "Не пусто (NOT NULL)"));
        CmbOperator.ItemsSource = operators;
        CmbOperator.SelectedValue = current?.Operator ?? "";
        if (CmbOperator.SelectedIndex < 0) CmbOperator.SelectedIndex = 0;
        TxtValue.Text = current?.Value ?? "";
        CmbSort.ItemsSource = new[] { "Без сортировки", "По возрастанию", "По убыванию" };
        CmbSort.SelectedIndex = sort == null ? 0 : sort.Direction == ListSortDirection.Ascending ? 1 : 2;
    }

    private void CmbOperator_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtValue != null)
            TxtValue.IsEnabled = CmbOperator.SelectedValue is string op && op is not ("" or "Is Null" or "Is Not Null");
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var op = CmbOperator.SelectedValue as string ?? "";
        string value = TxtValue.Text.Trim();
        object typedValue = null;
        if (TxtValue.IsEnabled)
        {
            if (value.Length == 0)
            {
                ErrorText.Text = "Введите значение фильтра.";
                return;
            }
            if (_typedComparison)
            {
                try
                {
                    var type = _column.DataType.ToLowerInvariant();
                    var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                    string input = type is "decimal" or "numeric" or "money" or "smallmoney" or "real" or "float"
                        ? value.Replace(separator, ".")
                        : value;
                    typedValue = EditRowWindow.ConvertValue(input, _column);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    ErrorText.Text = $"Введите значение типа {_column.DataType}. Для даты используйте формат ГГГГ-ММ-ДД.";
                    return;
                }
            }
        }

        Result = op.Length == 0 ? null : new TableFilter
        {
            Column = _column.Name, Operator = op, Value = TxtValue.IsEnabled ? value : "",
            TypedValue = typedValue, CompareAsText = !_typedComparison
        };
        Sort = CmbSort.SelectedIndex == 0 ? null : new TableColumnSort(_column.Name,
            CmbSort.SelectedIndex == 1 ? ListSortDirection.Ascending : ListSortDirection.Descending,
            _column.UsesTextOrdering);
        DialogResult = true;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
