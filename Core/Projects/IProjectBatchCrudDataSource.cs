#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Configurator.Core.Projects;

/// <summary>Optional atomic batch-create contract shared by storage adapters.</summary>
public interface IProjectBatchCrudDataSource
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CreateRecordsAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
        CancellationToken cancellationToken = default);
}
