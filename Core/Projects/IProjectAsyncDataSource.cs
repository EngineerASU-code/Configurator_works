#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Configurator.Core.Projects;

/// <summary>Asynchronous metadata reads for storage adapters that perform I/O.</summary>
public interface IProjectAsyncDataSource
{
    Task<IReadOnlyList<ProjectTableSchema>> GetTablesAsync(CancellationToken cancellationToken = default);
    Task<ProjectTableSchema?> GetSchemaAsync(string tableName, CancellationToken cancellationToken = default);
}
