using System;
using System.Collections.Generic;
using System.Linq;
using Configurator.Core.Architecture;

namespace Configurator;

/// <summary>Shared key and duplicate handling for Online and Offline lookup options.</summary>
internal static class LookupOptionRules
{
    public static string ResolveKeyField(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKeyColumns,
        string preferred,
        params string[] additionalPreferred)
    {
        if (columns == null || columns.Count == 0) return null;

        var candidates = new[] { preferred }
            .Concat(additionalPreferred ?? Array.Empty<string>())
            .Concat(new[] { "Record", "Type", "ID" })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate));
        foreach (var candidate in candidates)
        {
            var match = columns.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        if (primaryKeyColumns?.Count == 1)
            return columns.FirstOrDefault(name => name.Equals(primaryKeyColumns[0], StringComparison.OrdinalIgnoreCase));
        var identities = columns.Where(name => name.EndsWith("ID", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return identities.Length == 1 ? identities[0] : null;
    }

    public static void ResolveConventionalLookup(string sourceTable, string className, string fieldName,
        out string table, out string key, out string display, out bool useAreaNameLookup)
    {
        table = null;
        key = null;
        display = "Name";
        useAreaNameLookup = false;
        switch (fieldName.Trim().ToUpperInvariant())
        {
            case "PLC": table = "dbo.PLC"; key = "Record"; break;
            case "CLASS": table = "dbo.Classes"; key = "Record"; break;
            case "AREA": table = "dbo.Areas"; key = "AreaName"; display = "AreaNameL2"; useAreaNameLookup = true; break;
            case "UNIT": table = "dbo.Units"; key = "Unit_ID"; break;
            case "PLCCLASSNUMBER":
            case "PLCCLASSSNUMBER": table = "dbo.PLCClasssNumbers"; key = "PLCClassNumber"; break;
            case "TYPE":
                var definition = SystemLookupCatalog.Find(sourceTable, "Type") ?? SystemLookupCatalog.ForClass(className);
                table = definition?.LookupTable;
                key = definition?.LookupKeyField;
                break;
        }
    }

    public static string ResolveTable(IEnumerable<string> tableNames, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        requested = requested.Replace("[", "").Replace("]", "").Trim();
        var exact = tableNames.Where(name => name.Equals(requested, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (exact.Length != 0) return exact.Length == 1 ? exact[0] : null;
        if (requested.Contains('.')) return null;
        return UniqueTable(tableNames, name => Leaf(name).Equals(requested, StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveLegacyTable(IReadOnlyList<string> tableNames, string sourceTable, string className,
        string field, string requested, bool automaticDiscovery, bool explicitTable)
    {
        var exact = ResolveTable(tableNames, requested);
        if (exact != null) return exact;
        if (field.Equals("Type", StringComparison.OrdinalIgnoreCase) && className?.Equals("PLC", StringComparison.OrdinalIgnoreCase) == true
            && (string.IsNullOrWhiteSpace(requested) || Stem(Leaf(requested)) == "PLC"))
            return ResolveAliases(tableNames, "PLCType", "PLC_Types", "PLCTypes");

        if (explicitTable) return null;
        if (field.Equals("PLC", StringComparison.OrdinalIgnoreCase))
            return ResolveAliases(tableNames, "PLC", "PLCs");
        if (field.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase) || field.Equals("PLCClasssNumber", StringComparison.OrdinalIgnoreCase))
            return ResolveAliases(tableNames, "PLCClasssNumbers", "PLCClasssNumber", "PLCClassNumbers",
                "PLCClassNumber", "PLCClasses", "Classes");
        if (!automaticDiscovery || !field.Equals("Type", StringComparison.OrdinalIgnoreCase)) return null;

        var stems = new[] { Stem(Leaf(requested)), Stem(Leaf(sourceTable)), Stem(className) }
            .SelectMany(value => new[] { value, Singularize(value) })
            .Where(value => value.Length != 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = className?.Trim().ToUpperInvariant() switch
        {
            "DI" => new[] { "DiscretInType", "DiscreteInType", "DigitalInputType", "DIType" },
            "DO" => new[] { "DiscretOutType", "DiscreteOutType", "DigitalOutputType", "DOType" },
            "AI" => new[] { "AnaInType", "AnalogInType", "AnalogInputType", "AIType" },
            "AO" => new[] { "AnaOutType", "AnalogOutType", "AnalogOutputType", "AOType" },
            "PID" => new[] { "ReglerType", "ReglerTypes", "PIDType" },
            "DSV" => new[] { "Valve2SType", "DSVType" },
            _ => Array.Empty<string>()
        };
        return UniqueTable(tableNames, name =>
        {
            var leaf = Leaf(name);
            return (leaf.EndsWith("Type", StringComparison.OrdinalIgnoreCase) ||
                    leaf.EndsWith("Types", StringComparison.OrdinalIgnoreCase)) &&
                (stems.Contains(Stem(leaf)) || aliases.Contains(leaf, StringComparer.OrdinalIgnoreCase));
        });
    }

    private static string ResolveAliases(IEnumerable<string> tables, params string[] aliases)
        => UniqueTable(tables, table => aliases.Contains(Leaf(table), StringComparer.OrdinalIgnoreCase));

    private static string UniqueTable(IEnumerable<string> tables, Func<string, bool> matches)
    {
        var candidates = tables.Where(matches).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string Leaf(string table) => (table ?? "").Replace("[", "").Replace("]", "").Split('.').Last().Trim();

    private static string Stem(string name)
    {
        var normalized = new string((name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (normalized.EndsWith("TYPES", StringComparison.Ordinal)) return normalized[..^5];
        if (normalized.EndsWith("TYPE", StringComparison.Ordinal)) return normalized[..^4];
        return normalized;
    }

    private static string Singularize(string name) => name.EndsWith("IES", StringComparison.Ordinal) ? name[..^3] + "Y"
        : name.EndsWith("SES", StringComparison.Ordinal) && name.Length > 3 ? name[..^2]
        : name.EndsWith("S", StringComparison.Ordinal) ? name[..^1] : name;

    public static List<LookupOption> KeepUnambiguousKeys(IEnumerable<LookupOption> options)
        => (options ?? Enumerable.Empty<LookupOption>())
            .Where(option => option != null && !string.IsNullOrWhiteSpace(option.Key))
            .GroupBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(option => option.Key).Distinct(StringComparer.Ordinal).Count() == 1
                && group.Select(option => option.Display ?? string.Empty).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => group.First())
            .ToList();
}
