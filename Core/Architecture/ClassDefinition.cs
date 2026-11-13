using System;
using System.Collections.Generic;
using System.Linq;

namespace Configurator.Core.Architecture;

public sealed class ClassDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Object";
    public bool IsSystem { get; set; }
    public bool Available { get; set; } = true;
    public int ClassNumber { get; set; }
    public string BaseClassId { get; set; } = "";
    public string Description { get; set; } = "";
    public List<FieldDefinition> Fields { get; set; } = new();
    public List<ClassTypeDefinition> Types { get; set; } = new();
    public List<StorageMapping> StorageMappings { get; set; } = new();
    public string TypeStorageTable { get; set; } = "";
    public List<ComplexTableDefinition> ComplexTables { get; set; } = new();

    public StorageMapping PrimaryStorage => StorageMappings?.FirstOrDefault(x => x != null && string.Equals(x.Role, "primary", StringComparison.OrdinalIgnoreCase));

    [System.Text.Json.Serialization.JsonIgnore]
    public string PrimaryStorageTableName => PrimaryStorage?.TableName ?? string.Empty;
}
