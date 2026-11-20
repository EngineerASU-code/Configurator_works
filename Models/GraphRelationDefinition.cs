#nullable enable
using System;
using System.Linq;
﻿using System.Collections.Generic;

namespace Configurator;

public sealed class GraphRelationDefinition
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string SourceColumn { get; set; } = "";
    public string TargetColumn { get; set; } = "";
    public string Rule { get; set; } = "";

    internal static string? ResolveTableName(IEnumerable<string> tables, string? name)
    {
        var exact = tables.FirstOrDefault(table => table.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null || string.IsNullOrWhiteSpace(name)) return exact;

        var leaf = Leaf(name);
        var matches = tables.Where(table => Leaf(table).Equals(leaf, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;

        static string Leaf(string value) => value.Split('.').Last().Trim('[', ']');
    }
}

public sealed class GraphRelationProjectDocument
{
    public List<GraphRelationDefinition> ManualRelations { get; set; } = new();
    public List<string> SuppressedAutomaticRelations { get; set; } = new();
}


public sealed class GraphRelationRestorePayload
{
    public GraphRelationDefinition Relation { get; set; } = new();
    public bool WasManual { get; set; }
    // For inferred logical relations, restoring removes this id from the suppression list.
    public string SuppressedRelationId { get; set; } = "";
}
