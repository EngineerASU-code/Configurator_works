using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Configurator.Core.Projects;

internal static class UnifiedLanguage
{
    public static IEnumerable<(string Source, string Target)> Pairs(IEnumerable<string> names)
    {
        var columns = names.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
        foreach (string source in columns.Keys)
            if (source.EndsWith("L1", StringComparison.OrdinalIgnoreCase)
                && columns.TryGetValue(source[..^1] + "2", out string target))
                yield return (source, target);
    }

    public static void Mirror<T>(Dictionary<string, T> values, IEnumerable<string> columns)
    {
        foreach (var (source, target) in Pairs(columns))
            if (values.TryGetValue(source, out var value)) values[target] = value;
            else if (values.ContainsKey(target))
                throw new InvalidOperationException($"В режиме единой ЯС поле {target} изменяется через {source}.");
    }

    public static void SetOffline(OfflineProjectDataSource source, bool enabled)
    {
        var project = source.Project;
        if (enabled)
        {
            var changes = new List<(ProjectRecord Record, string Target, JsonNode Value)>();
            foreach (var group in project.LocalRecords.GroupBy(row => row.TableName, StringComparer.OrdinalIgnoreCase))
            {
                var schema = source.GetSchema(group.Key);
                var names = group.SelectMany(row => row.Values.Select(field => field.Key));
                if (schema != null) names = schema.Columns.Select(column => column.Name).Concat(names);
                var pairs = Pairs(names).ToArray();
                if (pairs.Length == 0) continue;
                if (!ProjectWriteGuard.IsDataTableWritable(group.Key)) continue;
                foreach (var (first, second) in pairs)
                {
                    if (schema?.KeyColumns.Contains(second, StringComparer.OrdinalIgnoreCase) == true ||
                        schema?.Columns.Any(c => c.Name.Equals(second, StringComparison.OrdinalIgnoreCase) &&
                            (c.IsIdentity || c.IsComputed || c.IsReadOnly)) == true)
                        throw new InvalidOperationException($"Поле {group.Key}.{second} защищено от записи.");
                    foreach (var row in group)
                    {
                        var value = row.Values.FirstOrDefault(f => f.Key.Equals(first, StringComparison.OrdinalIgnoreCase)).Value;
                        var target = row.Values.FirstOrDefault(f => f.Key.Equals(second, StringComparison.OrdinalIgnoreCase));
                        if (JsonNode.DeepEquals(value, target.Value)) continue;
                        if (schema?.Columns.Any(c => c.Name.Equals(second, StringComparison.OrdinalIgnoreCase)) == true)
                            OfflineProjectDataSource.ValidateValues(schema, new Dictionary<string, object> { [second] = value?.ToString() }, false, null);
                        changes.Add((row, target.Key ?? second, value?.DeepClone()));
                    }
                }
            }
            foreach (var change in changes) change.Record.Values[change.Target] = change.Value;
        }
        project.Settings.UnifiedLanguage = enabled;
        source.NotifyProjectChanged();
    }
}
