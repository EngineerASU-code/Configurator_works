#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Configurator.Core.Projects;

/// <summary>
/// Binds mutations to one selected project session. Switching or closing the project
/// cancels all in-flight mutations; Online adapters receive that token all the way to
/// SqlClient commands, while Offline adapters stop before changing the local snapshot.
/// </summary>
public sealed class ProjectCrudSession : IProjectCrudDataSource, IProjectBatchCrudDataSource, IProjectCreateWithKeyDataSource, IDisposable
{
    private readonly IProjectCrudDataSource _inner;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public ProjectCrudSession(IProjectCrudDataSource inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task CreateRecordAsync(string tableName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        ThrowIfDisposed();
        await _inner.CreateRecordAsync(tableName, values, linked.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, object?>> CreateRecordAndGetKeyAsync(string tableName,
        IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        ThrowIfDisposed();
        if (_inner is not IProjectCreateWithKeyDataSource keyed)
            throw new NotSupportedException("Источник данных не возвращает ключ созданной записи.");
        return await keyed.CreateRecordAndGetKeyAsync(tableName, values, linked.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CreateRecordsAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        ThrowIfDisposed();
        if (records == null || records.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();
        if (_inner is IProjectBatchCrudDataSource batch)
            return await batch.CreateRecordsAsync(tableName, records, linked.Token).ConfigureAwait(false);
        else if (records.Count == 1)
        {
            await _inner.CreateRecordAsync(tableName, records[0], linked.Token).ConfigureAwait(false);
            return records;
        }
        else
            throw new NotSupportedException("Источник данных не поддерживает атомарное пакетное создание записей.");
    }

    public async Task UpdateRecordAsync(string tableName, IReadOnlyList<string> keyColumns,
        IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        ThrowIfDisposed();
        await _inner.UpdateRecordAsync(tableName, keyColumns, keyValues, values, linked.Token).ConfigureAwait(false);
    }

    public async Task<int> DeleteRecordsAsync(string tableName, IReadOnlyList<string> keyColumns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> keyValues, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        ThrowIfDisposed();
        return await _inner.DeleteRecordsAsync(tableName, keyColumns, keyValues, linked.Token).ConfigureAwait(false);
    }

    public void Cancel() { if (Volatile.Read(ref _disposed) == 0) _lifetime.Cancel(); }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ProjectCrudSession));
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
    }
}
