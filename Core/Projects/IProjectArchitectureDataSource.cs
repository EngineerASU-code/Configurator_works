#nullable enable
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;

namespace Configurator.Core.Projects;

/// <summary>
/// Project architecture persistence contract. Class definitions and graph relation
/// documents are stored by the active provider adapter, never by UI mode handlers.
/// </summary>
public interface IProjectArchitectureDataSource
{
    Task<IReadOnlyList<ClassDefinition>> GetClassDefinitionsAsync(CancellationToken cancellationToken = default);
    Task SaveClassDefinitionsAsync(IReadOnlyList<ClassDefinition> definitions, CancellationToken cancellationToken = default);
    Task<JsonObject> GetGraphRelationsAsync(CancellationToken cancellationToken = default);
    Task SaveGraphRelationsAsync(JsonObject document, CancellationToken cancellationToken = default);
}
