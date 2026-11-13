#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Architecture;

namespace Configurator.Infrastructure.Projects;

/// <summary>
/// Adapter for the existing architecture stores. Online preserves the current per-user
/// class library and per-database graph store; Offline stores both documents inside cfgproj.
/// </summary>
public sealed class ProjectArchitectureDataSource : IProjectArchitectureDataSource
{
    private readonly TemplateLibraryStore _classes;
    private readonly Configurator.GraphRelationStore _relations;

    public ProjectArchitectureDataSource(ConfiguratorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _classes = new TemplateLibraryStore(project);
        _relations = new Configurator.GraphRelationStore(project);
    }

    public ProjectArchitectureDataSource(string server, string database)
    {
        _classes = new TemplateLibraryStore();
        _relations = new Configurator.GraphRelationStore(server, database);
    }

    public ProjectArchitectureDataSource(TemplateLibraryStore classes, Configurator.GraphRelationStore relations)
    {
        _classes = classes ?? throw new ArgumentNullException(nameof(classes));
        _relations = relations ?? throw new ArgumentNullException(nameof(relations));
    }

    public Task<IReadOnlyList<ClassDefinition>> GetClassDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ClassDefinition>>(_classes.Load());
    }

    public Task SaveClassDefinitionsAsync(IReadOnlyList<ClassDefinition> definitions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _classes.Save(definitions ?? Array.Empty<ClassDefinition>());
        return Task.CompletedTask;
    }

    public Task<JsonObject> GetGraphRelationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = _relations.Load();
        var json = JsonSerializer.SerializeToNode(document) as JsonObject ?? new JsonObject();
        return Task.FromResult(json);
    }

    public Task SaveGraphRelationsAsync(JsonObject document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var typed = document == null
            ? new Configurator.GraphRelationProjectDocument()
            : document.Deserialize<Configurator.GraphRelationProjectDocument>() ?? new Configurator.GraphRelationProjectDocument();
        _relations.Save(typed);
        return Task.CompletedTask;
    }
}
