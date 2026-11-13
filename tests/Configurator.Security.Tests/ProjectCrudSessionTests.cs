#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProjectCrudSessionTests
{
    [Fact]
    public async Task SwitchingProjectCancelsAnInFlightCrudOperation()
    {
        var source = new BlockingCrudSource();
        var session = new ProjectCrudSession(source);
        var operation = session.CreateRecordAsync("dbo.Items", new Dictionary<string, object?> { ["Id"] = 1 });
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // This is the same cancellation action used when ProjectModeView replaces
        // its active project-bound CRUD session during Online -> Offline switching.
        session.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(source.ObservedCancellation);
    }

    [Fact]
    public async Task DisposedSessionRejectsNewMutations()
    {
        var source = new BlockingCrudSource();
        var session = new ProjectCrudSession(source);
        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CreateRecordAsync(
            "dbo.Items", new Dictionary<string, object?> { ["Id"] = 1 }));
        Assert.False(source.Started.Task.IsCompleted);
    }

    private sealed class BlockingCrudSource : IProjectCrudDataSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }

        public async Task CreateRecordAsync(string tableName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { ObservedCancellation = true; throw; }
        }

        public Task UpdateRecordAsync(string tableName, IReadOnlyList<string> keyColumns,
            IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values,
            CancellationToken cancellationToken = default) => CreateRecordAsync(tableName, values, cancellationToken);

        public Task<int> DeleteRecordsAsync(string tableName, IReadOnlyList<string> keyColumns,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> keyValues, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<int>(new CancellationToken(true));
    }
}
