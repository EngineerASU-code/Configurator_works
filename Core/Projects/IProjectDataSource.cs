#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Configurator.Core.Projects;

/// <summary>Unified read interface for project metadata and records, independent of the active storage.</summary>
public interface IProjectDataSource
{
    string DisplayName { get; }
    bool IsReadOnly { get; }
    IReadOnlyList<ProjectTableSchema> GetTables();
    IReadOnlyList<ProjectRecord> GetRecords(string tableName);
    ProjectTableSchema? GetSchema(string tableName);
}
