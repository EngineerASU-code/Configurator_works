#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Configurator.Core.Projects;

/// <summary>
/// Optional server-side paging contract. Online implementations must execute
/// filtering, search, sorting and counting in the storage engine, not materialize
/// the whole table in the UI process.
/// </summary>
public interface IProjectPagedDataSource
{
    Task<ProjectRecordPage> ReadPageAsync(ProjectReadQuery query, CancellationToken cancellationToken = default);
    Task<int> CountAsync(ProjectReadQuery query, CancellationToken cancellationToken = default);
}
