using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class FreeSqlConfirmationTests
{
    [Fact]
    public async Task TimeoutRejectsEvenIfConfirmationCompletesLater()
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(await DatabaseService.ConfirmFreeSqlAsync(_ => response.Task, 1, CancellationToken.None, TimeSpan.FromMilliseconds(30)));
        response.SetResult(true);
    }

    [Fact]
    public async Task TimeoutDoesNotRequireTheCallingSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        Task<bool> pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new UnresponsiveContext());
            pending = DatabaseService.ConfirmFreeSqlAsync(_ => new TaskCompletionSource<bool>().Task,
                1, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class UnresponsiveContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object state) { }
    }

    [Fact]
    public async Task ClosingCancelsPendingConfirmation()
    {
        using var cancellation = new CancellationTokenSource();
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = DatabaseService.ConfirmFreeSqlAsync(_ => response.Task, 1, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
