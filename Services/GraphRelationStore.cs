#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;

namespace Configurator;

/// <summary>
/// Reads graph rules either in the portable .cfgproj package (Offline) or in the legacy
/// per-database JSON store (Online). An Offline instance never reads or writes AppData.
/// </summary>
public sealed class GraphRelationStore
{
    private readonly string? _path;
    private readonly ConfiguratorProject? _project;

    /// <summary>Legacy Online store scoped to a SQL Server/database pair.</summary>
    public GraphRelationStore(string server, string database)
    {
        var safeServer = MakeSafe(ConnectionSettings.ProjectServerName(server));
        var safeDatabase = MakeSafe(database);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Configurator", "GraphRelations");
        _path = Path.Combine(root, $"{safeServer}__{safeDatabase}.json");
    }

    /// <summary>Portable Offline store scoped to the opened local project.</summary>
    public GraphRelationStore(ConfiguratorProject project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    public GraphRelationProjectDocument Load(bool strict = false)
    {
        try
        {
            if (_project != null)
            {
                var node = _project.GraphRelations;
                return node.Count == 0
                    ? new GraphRelationProjectDocument()
                    : JsonSerializer.Deserialize<GraphRelationProjectDocument>(node.ToJsonString()) ?? new GraphRelationProjectDocument();
            }
            if (_path is null) return new GraphRelationProjectDocument();
            return JsonSerializer.Deserialize<GraphRelationProjectDocument>(File.ReadAllText(_path))
                ?? throw new JsonException("Файл логических связей содержит NULL вместо документа.");
        }
        catch (Exception ex) when (!strict || ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new GraphRelationProjectDocument();
        }
    }

    public void Save(GraphRelationProjectDocument document) =>
        throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

    private static string MakeSafe(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
        return text;
    }
}
