using System.Windows;
using System.Windows.Controls;

namespace Configurator;

public partial class WorkspaceWelcome : UserControl
{
    public WorkspaceWelcome() => InitializeComponent();
    public event EventHandler OnlineRequested;
    public event EventHandler OfflineRequested;

    private void Online_Click(object sender, RoutedEventArgs e) => OnlineRequested?.Invoke(this, EventArgs.Empty);
    private void Offline_Click(object sender, RoutedEventArgs e) => OfflineRequested?.Invoke(this, EventArgs.Empty);
}
