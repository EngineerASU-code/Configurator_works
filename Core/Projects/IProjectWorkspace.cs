#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;

namespace Configurator.Core.Projects;

/// <summary>Storage-neutral workspace contract consumed by application modes.</summary>
public interface IProjectWorkspace
{
    string DisplayName { get; }
    bool IsOffline { get; }
    bool IsReadOnly { get; }
    long Revision { get; }
    Task<IReadOnlyList<ProjectTableSchema>> GetTablesAsync(CancellationToken cancellationToken = default);
    Task<ProjectTableSchema?> GetSchemaAsync(string tableName, CancellationToken cancellationToken = default);
    Task<ProjectTableSchema?> RefreshSchemaAsync(string tableName, CancellationToken cancellationToken = default);
    Task<ProjectRecordPage> ReadPageAsync(ProjectReadQuery query, CancellationToken cancellationToken = default);
    Task<int> CountAsync(ProjectReadQuery query, CancellationToken cancellationToken = default);
    Task CreateRecordAsync(string tableName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
    Task CreateRecordsAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, CancellationToken cancellationToken = default);
    Task UpdateRecordAsync(string tableName, IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
    Task<int> DeleteRecordsAsync(string tableName, IReadOnlyList<string> keyColumns, IReadOnlyList<IReadOnlyDictionary<string, object?>> keyValues, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClassDefinition>> GetClassDefinitionsAsync(CancellationToken cancellationToken = default);
    Task SaveClassDefinitionsAsync(IReadOnlyList<ClassDefinition> definitions, CancellationToken cancellationToken = default);
    Task<JsonObject> GetGraphRelationsAsync(CancellationToken cancellationToken = default);
    Task SaveGraphRelationsAsync(JsonObject document, CancellationToken cancellationToken = default);
}

/// <summary>Common paging, filtering, sorting and search request.</summary>
public sealed class ProjectReadQuery
{
    public string TableName { get; init; } = string.Empty;
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 100;
    public string? SearchText { get; init; }
    public string? SortColumn { get; init; }
    public bool SortDescending { get; init; }
    public IReadOnlyDictionary<string, object?> Filters { get; init; } = new Dictionary<string, object?>();
}

public sealed class ProjectRecordPage
{
    public IReadOnlyList<ProjectRecord> Items { get; init; } = Array.Empty<ProjectRecord>();
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
