using System.Globalization;

namespace Configurator.Core.Workspace;

internal sealed class ScopedRecordSuggestion
{
    internal static bool IsAreasTable(string table) =>
        string.Equals(table, "dbo.Areas", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(table, "Areas", StringComparison.OrdinalIgnoreCase);

    internal static string AreaNameForRecord(int record) => record.ToString("D3", CultureInfo.InvariantCulture);

    private readonly SortedSet<decimal> _occupied = new();

    internal void Add(object value)
    {
        if (decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out decimal record) && record >= 0 && record == decimal.Truncate(record))
            _occupied.Add(record);
    }

    internal int GetValue()
    {
        decimal? previous = null;
        foreach (decimal record in _occupied)
        {
            if (previous.HasValue && previous.Value < int.MaxValue && record > previous.Value + 1)
                return (int)previous.Value + 1;
            previous = record;
        }

        if (previous >= int.MaxValue)
            throw new InvalidOperationException("Невозможно подобрать свободный Record в диапазоне Int32.");
        return (int)(previous ?? 0) + 1;
    }
}
