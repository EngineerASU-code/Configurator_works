#nullable enable
using Configurator.Core.Projects;

namespace Configurator.Core.Synchronization;

/// <summary>
/// Boundary for remote data access. Implementations must use parameterized SQL,
/// allowlisted mappings and least-privilege credentials. This patch intentionally
/// defines no write implementation; Online writes remain disabled by default.
/// </summary>
public interface IOnlineDataAdapter
{
    string RemoteIdentity { get; }
    Task<IReadOnlyList<ProjectRecord>> ReadRecordsAsync(
        IReadOnlyCollection<ProjectMapping> mappings,
        CancellationToken cancellationToken = default);
    Task<string?> GetRecordVersionAsync(
        string tableName,
        string keyColumn,
        string key,
        CancellationToken cancellationToken = default);
}
