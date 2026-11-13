#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Configurator.Core.Projects;

/// <summary>
/// Explicit mutation contract shared by Online and Offline project adapters.
/// The mode/UI layer should issue mutations through this boundary instead of
/// deciding how a particular storage engine persists them.
/// </summary>
public interface IProjectCrudDataSource
{
    Task CreateRecordAsync(string tableName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
    Task UpdateRecordAsync(string tableName, IReadOnlyList<string> keyColumns,
        IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);
    Task<int> DeleteRecordsAsync(string tableName, IReadOnlyList<string> keyColumns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> keyValues, CancellationToken cancellationToken = default);
}
