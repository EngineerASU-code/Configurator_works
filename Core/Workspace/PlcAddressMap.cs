#nullable enable
using System.Globalization;
using System.Text.RegularExpressions;
using Configurator.Core.Projects;

namespace Configurator.Core.Workspace;

internal sealed record AddressByteRow(char Area, int First, int Last, IReadOnlyList<string>[] Bits)
{
    public string Label => First == Last ? $"{Area}{First}" : $"{Area}{First}–{Area}{Last}";
    public bool HasFree => Bits.Any(bit => bit.Count == 0);
    public bool HasConflict => Bits.Any(bit => bit.Count > 1);
}

internal sealed record AddressOwner(string TableName, string Field, string Plc, string Raw,
    ProjectTableSchema Schema, ProjectRecord Record, string Display, char? DefaultArea, bool Analog);

internal sealed class PlcAddressMap
{
    private static readonly Regex Address = new(
        @"^%?(?<area>[IQ])?\s*(?<width>[XBWD])?\s*(?<offset>[0-9]+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Split whitespace between addresses, while preserving spaces within "IW 40" and "I 40".
    private static readonly Regex Separator = new(@"[,;/|\r\n]+|(?<=\d)\s+(?=%?[IQ0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Dictionary<(char Area, int Offset), List<string>[]> _bytes = new();
    private readonly HashSet<(char Area, int Offset, bool Analog)> _signalBytes = new();

    internal bool ContainsSignal(AddressByteRow row, string type) =>
        row.Area == (type[1] == 'I' ? 'I' : 'Q') && _signalBytes.Contains((row.Area, row.First, type[0] == 'A'));

    private readonly Dictionary<string, List<AddressOwner>> _owners = new(StringComparer.Ordinal);
    private readonly SortedDictionary<(char Area, int Start, int Width), int> _spans = new();

    internal Dictionary<(char, int), string> SpanGroups()
    {
        var groups = new Dictionary<(char, int), string>();
        var ranges = _spans.ToArray();
        for (int i = 0; i < ranges.Length;)
        {
            var (area, start, width) = ranges[i].Key;
            int end = start + width;
            var labels = new List<string>();
            do
            {
                var range = ranges[i++];
                var (_, first, size) = range.Key;
                end = Math.Max(end, first + size);
                var count = range.Value > 1 ? $" × {range.Value}" : "";
                labels.Add($"{area}{first}–{area}{first + size - 1} · {size} байта{count}");
            } while (i < ranges.Length && ranges[i].Key.Area == area && ranges[i].Key.Start < end);
            string label = string.Join("   /   ", labels);
            for (int offset = start; offset < end; offset++) groups[(area, offset)] = label;
        }
        return groups;
    }

    internal AddressOwner? FindOwner(string description) =>
        _owners.TryGetValue(description, out var owners) && owners.Count == 1 ? owners[0] : null;
    internal List<string> Issues { get; } = new();

    internal void Add(string raw, string owner, char? defaultArea, bool analog, AddressOwner? identity = null)
    {
        foreach (var part in Separator.Split(raw))
        {
            var token = part.Trim().ToUpperInvariant();
            if (token.Length == 0) continue;
            var match = Address.Match(token);
            var area = match.Groups["area"].Success ? match.Groups["area"].Value[0] : defaultArea;
            var width = match.Groups["width"].Value;
            var hasBit = match.Groups["bit"].Success;
            var size = width switch { "D" => 4, "W" => 2, "" when analog && !hasBit => 2, _ => 1 };
            if (!match.Success || area == null || (hasBit && width is not ("" or "X")) || (width == "X" && !hasBit)
                || !int.TryParse(match.Groups["offset"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int start)
                || start > 65536 - size)
            {
                Issues.Add($"{owner} · {part.Trim()}");
                continue;
            }

            for (int offset = start; offset < start + size; offset++)
                _signalBytes.Add((area.Value, offset, analog));

            var firstBit = hasBit ? match.Groups["bit"].Value[0] - '0' : 0;
            var endBit = hasBit ? firstBit + 1 : 8;
            var source = $"{owner} · {part.Trim()}";
            if (identity != null)
            {
                if (!_owners.TryGetValue(source, out var identities)) _owners[source] = identities = new();
                identities.Add(identity);
            }
            if (size > 1)
            {
                var span = (area.Value, start, size);
                _spans[span] = _spans.GetValueOrDefault(span) + 1;
            }
            for (var offset = start; offset < start + size; offset++)
            {
                var key = (area.Value, offset);
                if (!_bytes.TryGetValue(key, out var bits))
                    _bytes[key] = bits = Enumerable.Range(0, 8).Select(_ => new List<string>()).ToArray();
                for (var bit = firstBit; bit < endBit; bit++) bits[bit].Add(source);
            }
        }
    }

    internal sealed record Destination(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    internal static Destination DescribeAddress(string value, char defaultArea, bool analog)
    {
        var match = Address.Match(value.Trim());
        if (!match.Success) return new(value, value);
        var area = match.Groups["area"].Success ? char.ToUpperInvariant(match.Groups["area"].Value[0]) : defaultArea;
        var width = match.Groups["width"].Value.ToUpperInvariant();
        int start = int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture);
        if (match.Groups["bit"].Success)
            return new(value, $"{area}{start}.{match.Groups["bit"].Value} · 1 бит");
        int size = width switch { "D" => 4, "W" => 2, "" when analog => 2, _ => 1 };
        return new(value, size == 1 ? $"{area}B{start} · 1 байт" :
            $"{area}{(size == 2 ? "W" : "D")}{start} · {area}{start}–{area}{start + size - 1} · {size} байта");
    }

    // Only single-address values can be changed without rewriting unrelated assignments.
    internal IReadOnlyList<string> FreeAddresses(AddressOwner owner, char selectedArea, int limit = 256)
    {
        if (Issues.Count != 0 || !owner.Schema.HasStableKey || owner.Schema.KeyColumns.Count == 0 ||
            !ProjectWriteGuard.IsDataTableWritable(owner.TableName)) return Array.Empty<string>();
        var column = owner.Schema.Columns.FirstOrDefault(c => c.Name.Equals(owner.Field, StringComparison.OrdinalIgnoreCase));
        if (column == null || column.IsReadOnly || column.IsComputed || column.IsIdentity) return Array.Empty<string>();
        var match = Address.Match(owner.Raw.Trim());
        if (!match.Success) return Array.Empty<string>();
        var area = match.Groups["area"].Success ? char.ToUpperInvariant(match.Groups["area"].Value[0]) : owner.DefaultArea;
        if (area != selectedArea) return Array.Empty<string>();
        var width = match.Groups["width"].Value.ToUpperInvariant();
        bool bit = match.Groups["bit"].Success;
        if ((bit && width is not ("" or "X")) || (width == "X" && !bit)) return Array.Empty<string>();
        int size = width switch { "D" => 4, "W" => 2, "" when owner.Analog && !bit => 2, _ => 1 };
        var result = new List<string>();
        for (int offset = 0; offset <= 65536 - size && result.Count < limit; offset++)
        {
            if (bit)
            {
                for (int b = 0; b < 8 && result.Count < limit; b++)
                    if (!_bytes.TryGetValue((selectedArea, offset), out var bits) || bits[b].Count == 0)
                        result.Add($"{selectedArea}{offset}.{b}");
            }
            else if (Enumerable.Range(offset, size).All(i => !_bytes.TryGetValue((selectedArea, i), out var bits) || bits.All(x => x.Count == 0)))
                result.Add(owner.Analog && !match.Groups["area"].Success && width.Length == 0
                    ? offset.ToString(CultureInfo.InvariantCulture)
                    : $"{selectedArea}{(size == 4 ? "D" : size == 2 ? "W" : "B")}{offset}");
        }
        return result;
    }

    internal List<AddressByteRow> Rows()
    {
        var rows = new List<AddressByteRow>();
        IReadOnlyList<string>[] empty = Enumerable.Repeat<IReadOnlyList<string>>(Array.Empty<string>(), 8).ToArray();
        foreach (var group in _bytes.OrderBy(x => x.Key.Area).ThenBy(x => x.Key.Offset).GroupBy(x => x.Key.Area))
        {
            int next = group.First().Key.Offset;
            foreach (var entry in group)
            {
                var offset = entry.Key.Offset;
                if (next < offset) rows.Add(new(group.Key, next, offset - 1, empty));
                rows.Add(new(group.Key, offset, offset, entry.Value));
                next = offset + 1;
            }
        }
        return rows;
    }
}
