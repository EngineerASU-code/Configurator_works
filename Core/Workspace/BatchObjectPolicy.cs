using System;
using System.Collections.Generic;
using System.Linq;

namespace Configurator.Core.Workspace;

public static class BatchObjectPolicy
{
    public static string RecordKey(string plcKey, string record)
    {
        var normalizedRecord = (record ?? string.Empty).Trim();
        if (int.TryParse(normalizedRecord, out var numericRecord))
            normalizedRecord = numericRecord.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{(plcKey ?? string.Empty).Trim()}|{normalizedRecord}";
    }

    private static HashSet<string> NormalizeRecordKeys(IEnumerable<string> keys)
        => (keys ?? Array.Empty<string>()).Select(key =>
        {
            var parts = (key ?? string.Empty).Split('|', 2);
            return parts.Length == 2 ? RecordKey(parts[0], parts[1]) : key ?? string.Empty;
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static int NextFreeRecord(string plcKey, IEnumerable<string> existingRecordKeys, IEnumerable<string> pendingRecordKeys)
    {
        var reserved = NormalizeRecordKeys((existingRecordKeys ?? Array.Empty<string>())
            .Concat(pendingRecordKeys ?? Array.Empty<string>()));
        var used = reserved.Select(k => k.Split('|', 2))
            .Where(p => p.Length == 2 && p[0].Equals(plcKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .Select(p => int.TryParse(p[1], out var n) ? n : 0).ToList();
        int next = used.Count == 0 ? 1 : used.Max() + 1;
        while (reserved.Contains(RecordKey(plcKey ?? string.Empty, next.ToString()))) next++;
        return next;
    }

    public static IReadOnlyList<string> ValidateRows(
        IEnumerable<(string Record, string PlcKey, string Name)> rows,
        IEnumerable<string> existingNames,
        IEnumerable<string> existingRecordKeys,
        bool hasRecord,
        bool requirePlc,
        IEnumerable<string> validPlcKeys)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(existingNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var records = NormalizeRecordKeys(existingRecordKeys);
        var plcs = new HashSet<string>(validPlcKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var row in rows ?? Array.Empty<(string, string, string)>())
        {
            i++;
            if (string.IsNullOrWhiteSpace(row.Name)) errors.Add($"Строка {i}: заполните Name.");
            else if (!names.Add(row.Name.Trim())) errors.Add($"Строка {i}: имя «{row.Name.Trim()}» уже существует или повторяется.");
            if (requirePlc && (plcs.Count == 0 || !plcs.Contains(row.PlcKey ?? string.Empty)))
                errors.Add($"Строка {i}: выберите доступный PLC.");
            if (hasRecord)
            {
                if (string.IsNullOrWhiteSpace(row.Record)) errors.Add($"Строка {i}: заполните Record.");
                else if (!records.Add(RecordKey(row.PlcKey ?? string.Empty, row.Record.Trim())))
                    errors.Add($"Строка {i}: Record «{row.Record}» уже занят для выбранного PLC.");
            }
        }
        return errors;
    }
}
