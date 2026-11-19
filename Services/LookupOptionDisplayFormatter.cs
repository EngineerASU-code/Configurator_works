using System;
using System.Collections.Generic;
using System.Linq;

namespace Configurator;

/// <summary>Uses the same visible label rules for lookup rows from Online and Offline sources.</summary>
internal static class LookupOptionDisplayFormatter
{
    public static string Format(
        IReadOnlyCollection<string> names,
        Func<string, string> getValue,
        string key,
        string hint,
        bool combineAreaNames,
        bool useAreaNameLookup)
    {
        string Name(string candidate) => names.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        string Value(string candidate)
        {
            var actual = Name(candidate);
            return actual == null ? null : getValue(actual);
        }

        string display = key;
        if (combineAreaNames && Name("AreaName") is { } areaNameColumn)
        {
            var l1 = getValue(areaNameColumn) ?? string.Empty;
            var l2 = Value("AreaNameL2") ?? string.Empty;
            display = !string.IsNullOrWhiteSpace(l2) && !l2.Equals(l1, StringComparison.OrdinalIgnoreCase)
                ? $"{l1} · {l2}"
                : l1;
        }
        else
        {
            foreach (var candidate in new[] { hint, "Name", "NameL1", "Name_L1", "NameL2", "Name_L2", "Description", "DescriptionL1", "Title", "Caption", "Label", "TypeName", "Type_Name" })
            {
                if (string.IsNullOrWhiteSpace(candidate) || Name(candidate) is not { } valueColumn) continue;
                var value = getValue(valueColumn) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    display = $"{value}  [{key}]";
                    break;
                }
            }
        }

        string recordColumn = Name("Record");
        if (useAreaNameLookup)
        {
            var areaLabelColumn = new[] { "AreaNameL2", "AreaNameL1", "NameL2", "Name_L2", "NameL1", "Name_L1", "AreaName" }
                .Select(Name).FirstOrDefault(column => column != null);
            string areaLabel = areaLabelColumn == null ? string.Empty : getValue(areaLabelColumn) ?? string.Empty;
            string record = recordColumn == null ? string.Empty : getValue(recordColumn) ?? string.Empty;
            display = string.IsNullOrWhiteSpace(areaLabel) ? (string.IsNullOrWhiteSpace(record) ? key : record)
                : string.IsNullOrWhiteSpace(record) ? areaLabel : $"{record}. {areaLabel}";
        }
        else if (!combineAreaNames && recordColumn != null)
        {
            var nameColumn = new[] { "Name", "NameL1", "Title" }.Select(Name).FirstOrDefault(column => column != null);
            if (nameColumn != null)
            {
                string record = getValue(recordColumn) ?? key;
                string name = getValue(nameColumn) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name)) display = $"{record} · {name}";
            }
        }

        return display;
    }
}
