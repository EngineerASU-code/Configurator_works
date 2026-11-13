using System.Collections.Generic;

namespace Configurator.Core.Architecture;

/// <summary>
/// Project-specific structure of one table participating in a complex class
/// (Program, Matrix, etc.). It is metadata only: it never creates SQL relations.
/// </summary>
public sealed class ComplexTableDefinition
{
    public string TableName { get; set; } = "";
    public string Role { get; set; } = "Child";
    public string Description { get; set; } = "";
    public List<FieldDefinition> Fields { get; set; } = new();
}
