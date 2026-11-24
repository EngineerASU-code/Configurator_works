#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;

namespace Configurator.Core.Projects;

/// <summary>
/// Storage-neutral facade joining the selected read and write adapters.
/// It deliberately contains no SQL or UI-mode decisions.
/// </summary>
public sealed class ProjectWorkspace : IProjectWorkspace, IDisposable
{
    private readonly IProjectDataSource _reader;
    private readonly IProjectCrudDataSource? _writer;
    private readonly IProjectArchitectureDataSource? _architecture;
    private long _revision;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public ProjectWorkspace(IProjectDataSource reader, IProjectCrudDataSource? writer = null,
        IProjectArchitectureDataSource? architecture = null, bool allowWrite = true)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = allowWrite ? writer ?? reader as IProjectCrudDataSource : null;
        _architecture = architecture;
        if (reader is OfflineProjectDataSource offline) offline.ProjectChanged += OnProjectChanged;
    }

    public string DisplayName => _reader.DisplayName;
    public bool IsOffline => _reader is OfflineProjectDataSource;
    public bool IsReadOnly => _reader.IsReadOnly || _writer == null;
    public long Revision => Interlocked.Read(ref _revision);

    public async Task<IReadOnlyList<ProjectTableSchema>> GetTablesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        if (_reader is IProjectAsyncDataSource asyncSource)
            return await asyncSource.GetTablesAsync(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return _reader.GetTables();
    }

    public async Task<ProjectTableSchema?> GetSchemaAsync(string tableName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        if (_reader is IProjectAsyncDataSource asyncSource)
            return await asyncSource.GetSchemaAsync(tableName, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return _reader.GetSchema(tableName);
    }

    public async Task<ProjectTableSchema?> RefreshSchemaAsync(string tableName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_reader is IProjectSchemaCache cache) cache.InvalidateSchemaCache();
        return await GetSchemaAsync(tableName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectRecordPage> ReadPageAsync(ProjectReadQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(query.TableName)) throw new ArgumentException("Не задана таблица.", nameof(query));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        if (_reader is IProjectPagedDataSource paged)
            return await paged.ReadPageAsync(query, linked.Token).ConfigureAwait(false);
        var page = Math.Max(1, query.PageNumber);
        var size = Math.Clamp(query.PageSize, 1, 1000);
        IEnumerable<ProjectRecord> rows = _reader.GetRecords(query.TableName);
        foreach (var filter in query.Filters ?? new Dictionary<string, object?>())
        {
            rows = rows.Where(row => TryGetValueIgnoreCase(row.Values, filter.Key, out var node)
                && string.Equals(Normalize(node), Normalize(filter.Value), StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            rows = rows.Where(row => row.Values.Any(pair => pair.Value != null
                && pair.Value.ToString()!.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(query.SortColumn))
        {
            Func<ProjectRecord, string> key = row => TryGetValueIgnoreCase(row.Values, query.SortColumn, out var node) ? node?.ToString() ?? string.Empty : string.Empty;
            rows = query.SortDescending
                ? rows.OrderByDescending(key, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(key, StringComparer.CurrentCultureIgnoreCase);
        }
        var materialized = rows as IReadOnlyList<ProjectRecord> ?? rows.ToList();
        return new ProjectRecordPage
        {
            Items = materialized.Skip((int)Math.Min(int.MaxValue, (long)(page - 1) * size)).Take(size).ToList(),
            PageNumber = page,
            PageSize = size,
            TotalCount = materialized.Count
        };
    }

    public async Task<int> CountAsync(ProjectReadQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        if (_reader is IProjectPagedDataSource paged)
            return await paged.CountAsync(query, linked.Token).ConfigureAwait(false);
        var result = await ReadPageAsync(new ProjectReadQuery
        {
            TableName = query.TableName, PageNumber = 1, PageSize = 1,
            SearchText = query.SearchText, Filters = query.Filters
        }, cancellationToken).ConfigureAwait(false);
        return result.TotalCount;
    }

    public async Task CreateRecordAsync(string tableName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    { EnsureWritable(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken); await _writer!.CreateRecordAsync(tableName, values, linked.Token).ConfigureAwait(false); if (!IsOffline) Interlocked.Increment(ref _revision); }

    public async Task CreateRecordsAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (records == null || records.Count == 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        if (_writer is IProjectBatchCrudDataSource batch)
            await batch.CreateRecordsAsync(tableName, records, linked.Token).ConfigureAwait(false);
        else if (records.Count == 1)
            await _writer!.CreateRecordAsync(tableName, records[0], linked.Token).ConfigureAwait(false);
        else
            throw new NotSupportedException("Источник данных не поддерживает атомарное пакетное создание записей.");
        if (!IsOffline) Interlocked.Increment(ref _revision);
    }

    public async Task UpdateRecordAsync(string tableName, IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    { EnsureWritable(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken); await _writer!.UpdateRecordAsync(tableName, keyColumns, keyValues, values, linked.Token).ConfigureAwait(false); if (!IsOffline) Interlocked.Increment(ref _revision); }

    public async Task UpdateCompareRecordAsync(ProjectTableSchema schema, ProjectRecord? expectedRecord,
        IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (IsOffline || _writer is not OnlineProjectCrudDataSource online)
            throw new NotSupportedException("Условное сравнение перед записью поддерживается только для Online-источника.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await online.UpdateCompareRecordAsync(schema, expectedRecord, keyValues, values, linked.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _revision);
    }

    internal async Task ReassignPlcAddressAsync(Configurator.Core.Workspace.AddressOwner owner, string newAddress,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (IsOffline || _writer is not OnlineProjectCrudDataSource online || _architecture == null)
            throw new NotSupportedException("Переназначение адреса доступно только для Online-проекта с разрешённой записью.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await Configurator.PlcAddressReassignment.ExecuteAsync(online.ConnectionString, _architecture, owner, newAddress, linked.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _revision);
    }

    public async Task<int> DeleteRecordsAsync(string tableName, IReadOnlyList<string> keyColumns, IReadOnlyList<IReadOnlyDictionary<string, object?>> keyValues, CancellationToken cancellationToken = default)
    { EnsureWritable(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken); var count = await _writer!.DeleteRecordsAsync(tableName, keyColumns, keyValues, linked.Token).ConfigureAwait(false); if (count > 0 && !IsOffline) Interlocked.Increment(ref _revision); return count; }

    public async Task<IReadOnlyList<ClassDefinition>> GetClassDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_architecture == null) throw new NotSupportedException("Для текущего рабочего пространства не подключено хранилище архитектуры классов.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        return await _architecture.GetClassDefinitionsAsync(linked.Token).ConfigureAwait(false);
    }

    public Task SaveClassDefinitionsAsync(IReadOnlyList<ClassDefinition> definitions, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

    public async Task<JsonObject> GetGraphRelationsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_architecture == null) throw new NotSupportedException("Для текущего рабочего пространства не подключено хранилище связей графа.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        return await _architecture.GetGraphRelationsAsync(linked.Token).ConfigureAwait(false);
    }

    public Task SaveGraphRelationsAsync(JsonObject document, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

    private void EnsureWritable()
    {
        ThrowIfDisposed();
        if (IsReadOnly) throw new InvalidOperationException("Для текущего рабочего пространства запись недоступна.");
    }
    private void OnProjectChanged(object? sender, EventArgs e) => Interlocked.Increment(ref _revision);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ProjectWorkspace));
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        if (_reader is OfflineProjectDataSource offline) offline.ProjectChanged -= OnProjectChanged;
        _lifetime.Dispose();
    }
    private static bool TryGetValueIgnoreCase(JsonObject values, string key, out JsonNode? value)
    {
        foreach (var pair in values)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string Normalize(object? value) => value switch
    {
        null => string.Empty,
        JsonNode node => node.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };
}
