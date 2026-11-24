#nullable enable
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace Configurator.Security.Tests;

[CollectionDefinition("WPF dialogs")]
public sealed class WpfDialogsCollection : ICollectionFixture<WpfDialogHost> { }

public sealed class WpfDialogHost : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    public WpfDialogHost()
    {
        _thread = new Thread(() =>
        {
            try
            {
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                _ready.SetResult(app.Dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { _ready.TrySetException(ex); }
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task RunAsync(Func<Task> test)
    {
        var dispatcher = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await dispatcher.InvokeAsync(test).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        if (_ready.Task.IsCompletedSuccessfully)
            _ready.Task.Result.BeginInvokeShutdown(DispatcherPriority.Normal);
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
