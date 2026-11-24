#nullable enable
namespace Configurator.Core.Workspace;

internal readonly record struct RecordGap(int First, int Last)
{
    public long Count => (long)Last - First + 1;
    public override string ToString() => First == Last ? $"{First}" : $"{First}–{Last}";
}

internal sealed record RecordCheckResult(int? MinRecord, int? MaxRecord, IReadOnlyList<RecordGap> Gaps)
{
    public long FreeCount => Gaps.Sum(gap => gap.Count);

    internal static RecordCheckResult FromOccupied(SortedSet<int> records)
    {
        if (records.Count == 0) return new(null, null, []);

        var gaps = new List<RecordGap>();
        int previous = records.Min;
        foreach (int record in records)
        {
            if ((long)record - previous > 1) gaps.Add(new RecordGap(previous + 1, record - 1));
            previous = record;
        }
        return new(records.Min, records.Max, gaps);
    }
}
