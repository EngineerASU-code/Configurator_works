using System.Threading.Tasks;
using System.Windows;

namespace Configurator;

internal static class RowEditorWindowExtensions
{
    internal static Task ShowRowEditorAsync(this Window window)
    {
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        return closed.Task;
    }
}
