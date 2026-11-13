using System;
using System.Collections.Generic;
using System.Linq;

namespace Configurator.Core.Architecture;

/// <summary>Project-owned dictionary generation and binding rules.</summary>
public sealed class LookupGenerationSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool AutomaticDiscoveryEnabled { get; set; } = true;
    public List<LookupGenerationRule> Rules { get; set; } = new();
}

public sealed class LookupGenerationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string ClassName { get; set; } = string.Empty;
    public string SourceTable { get; set; } = string.Empty;
    public string SourceField { get; set; } = "Type";
    public string LookupTable { get; set; } = string.Empty;
    public string KeyField { get; set; } = "Record";
    public string DisplayField { get; set; } = "NameL2";
    public bool IsUserDefined { get; set; }
}

public static class LookupGenerationDefaults
{
    public static LookupGenerationSettings Create()
    {
        var settings = new LookupGenerationSettings();
        settings.Rules.AddRange(new[]
        {
            Rule("PLC", "dbo.PLC", "dbo.PLCType", "Record", "Name"),
            Rule("Valve", "dbo.Valves", "dbo.Valves_Type", "Record", "NameL2"),
            Rule("DSV", "dbo.Valve2S", "dbo.Valves2S_Type", "Record", "NameL2"),
            Rule("Motor", "dbo.Motors", "dbo.Motors_Type", "Record", "NameL2"),
            Rule("DI", "dbo.DiscretIN", "dbo.DiscretIN_Types", "Record", "NameL2"),
            Rule("DO", "dbo.DiscretOUT", "dbo.DiscretOUT_Types", "Record", "NameL2"),
            Rule("AI", "dbo.AnaIn", "dbo.AnaIn_Type", "Record", "NameL2"),
            Rule("AO", "dbo.AnaOUT", "dbo.AnaOUT_Type", "Record", "NameL2"),
            Rule("PID", "dbo.Reglers", "dbo.Reglers_Types", "Record", "NameL2")
        });
        return settings;
    }

    private static LookupGenerationRule Rule(string cls, string source, string lookup, string key, string display) => new()
    {
        ClassName = cls, SourceTable = source, SourceField = "Type", LookupTable = lookup,
        KeyField = key, DisplayField = display
    };
}

/// <summary>Single place that decides which project rule applies, shared by Online and Offline resolution.</summary>
public static class LookupGenerationRuleSelector
{
    /// <summary>
    /// Returns the best matching rule for class + source table + field. A rule that names the source table
    /// is more specific than a table-agnostic one; on equal specificity an enabled rule wins over a disabled one.
    /// The caller treats a disabled result as "do not generate a lookup for this field".
    /// </summary>
    public static LookupGenerationRule Find(LookupGenerationSettings settings, string className, string sourceTable, string fieldName)
    {
        if (settings?.Rules == null) return null;
        string cls = className ?? string.Empty;
        string field = fieldName ?? string.Empty;
        return settings.Rules
            .Where(r => r != null
                && string.Equals(r.ClassName ?? string.Empty, cls, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.SourceField ?? string.Empty, field, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(r.SourceTable) || string.IsNullOrWhiteSpace(sourceTable)
                    || string.Equals(r.SourceTable, sourceTable, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => string.IsNullOrWhiteSpace(r.SourceTable) ? 0 : 1)
            .ThenByDescending(r => r.Enabled ? 1 : 0)
            .FirstOrDefault();
    }
}
