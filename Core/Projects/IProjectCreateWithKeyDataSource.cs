#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Configurator.Core.Projects;

/// <summary>Optional create contract that returns the authoritative key of the inserted row.</summary>
public interface IProjectCreateWithKeyDataSource
{
    Task<IReadOnlyDictionary<string, object?>> CreateRecordAndGetKeyAsync(string tableName,
        IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
}
