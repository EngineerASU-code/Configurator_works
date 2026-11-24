namespace Configurator
{
    public class TableFilter
    {
        public string Column { get; set; } = "";
        public string Operator { get; set; } = "Contains";
        public string Value { get; set; } = "";
        internal object TypedValue { get; init; }
        internal bool CompareAsText { get; init; }

        public bool IsActive => !string.IsNullOrWhiteSpace(Column) &&
                                 (!string.IsNullOrWhiteSpace(Value) || Operator is "Is Null" or "Is Not Null");
    }

    internal sealed record TableColumnSort(string Column, System.ComponentModel.ListSortDirection Direction, bool CompareAsText = false);

    internal sealed class TableColumnViewSettings
    {
        private readonly Dictionary<string, TableFilter> _filters = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TableColumnSort> _sorts = new();

        internal TableFilter[] Filters => _filters.Values.ToArray();
        internal TableColumnSort[] Sorts => _sorts.ToArray();
        internal bool HasFilters => _filters.Count > 0;
        internal bool IsActive => HasFilters || _sorts.Count > 0;
        internal TableFilter FilterFor(string column) => _filters.GetValueOrDefault(column);
        internal TableColumnSort SortFor(string column) => _sorts.FirstOrDefault(s => s.Column.Equals(column, StringComparison.OrdinalIgnoreCase));

        internal void Clear()
        {
            _filters.Clear();
            _sorts.Clear();
        }

        internal void Set(string column, TableFilter filter, TableColumnSort sort)
        {
            if (filter?.IsActive == true) _filters[column] = filter;
            else _filters.Remove(column);

            int index = _sorts.FindIndex(s => s.Column.Equals(column, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (sort != null) _sorts.Add(sort);
            }
            else if (sort == null) _sorts.RemoveAt(index);
            else _sorts[index] = sort;
        }
    }
}
