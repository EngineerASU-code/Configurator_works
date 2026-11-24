using System.ComponentModel;
using System.Data;
using System.Globalization;

namespace Configurator;

internal static class OfflineColumnView
{
    internal static DataView Create(DataTable table, IReadOnlyList<ColumnInfo> columns, TableColumnViewSettings settings)
    {
        if (settings?.IsActive != true) return new DataView(table);
        var filters = settings.Filters.Where(filter => table.Columns.Contains(filter.Column)).ToArray();
        var query = table.AsEnumerable().Where(row => filters.All(filter =>
            Matches(row[filter.Column], filter, columns.First(c => c.Name.Equals(filter.Column, StringComparison.OrdinalIgnoreCase)))));
        OrderedEnumerableRowCollection<DataRow> ordered = null;
        var comparer = Comparer<object>.Create(Compare);
        foreach (var sort in settings.Sorts.Where(sort => table.Columns.Contains(sort.Column)))
        {
            var column = columns.First(c => c.Name.Equals(sort.Column, StringComparison.OrdinalIgnoreCase));
            object Key(DataRow row) => ValueForComparison(row[column.Name], column);
            bool descending = sort.Direction == ListSortDirection.Descending;
            ordered = ordered == null
                ? (descending ? query.OrderByDescending(Key, comparer) : query.OrderBy(Key, comparer))
                : (descending ? ordered.ThenByDescending(Key, comparer) : ordered.ThenBy(Key, comparer));
        }
        return (ordered ?? query).AsDataView();
    }

    private static bool Matches(object value, TableFilter filter, ColumnInfo column)
    {
        bool isNull = value == DBNull.Value;
        if (filter.Operator == "Is Null") return isNull;
        if (filter.Operator == "Is Not Null") return !isNull;
        if (isNull) return false;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (filter.Operator == "Contains") return text.Contains(filter.Value, StringComparison.OrdinalIgnoreCase);
        if (filter.Operator == "Not Contains") return !text.Contains(filter.Value, StringComparison.OrdinalIgnoreCase);
        if (filter.Operator == "Starts With") return text.StartsWith(filter.Value, StringComparison.OrdinalIgnoreCase);
        if (filter.Operator == "Ends With") return text.EndsWith(filter.Value, StringComparison.OrdinalIgnoreCase);

        var left = filter.CompareAsText ? text : ValueForComparison(value, column);
        var right = filter.TypedValue ?? filter.Value;
        if (left.GetType() != right.GetType()) return false;
        int comparison = Compare(left, right);
        return filter.Operator switch
        {
            "Equals" => comparison == 0,
            "Not Equals" => comparison != 0,
            "Greater" => comparison > 0,
            "Greater Or Equal" => comparison >= 0,
            "Less" => comparison < 0,
            "Less Or Equal" => comparison <= 0,
            _ => false
        };
    }

    private static object ValueForComparison(object value, ColumnInfo column)
    {
        if (value == DBNull.Value) return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        try { return EditRowWindow.ConvertValue(text, column); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return text; }
    }

    private static int Compare(object first, object second)
    {
        if (first == null) return second == null ? 0 : -1;
        if (second == null) return 1;
        if (first is string left)
            return second is string right ? StringComparer.OrdinalIgnoreCase.Compare(left, right) : 1;
        if (second is string) return -1;
        return ((IComparable)first).CompareTo(second);
    }
}
