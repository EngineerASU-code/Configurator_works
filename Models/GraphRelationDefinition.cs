using System.Collections.Generic;

namespace Configurator;

public sealed class GraphRelationDefinition
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string SourceColumn { get; set; } = "";
    public string TargetColumn { get; set; } = "";
    public string Rule { get; set; } = "";
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
