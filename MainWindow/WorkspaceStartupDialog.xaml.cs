using System.Windows;

namespace Configurator;

public partial class WorkspaceStartupDialog : Window
{
    public string SelectedAction { get; private set; } = "online";

    public WorkspaceStartupDialog() => InitializeComponent();

    private void Online_Click(object sender, RoutedEventArgs e) { SelectedAction = "online"; DialogResult = true; }
    private void OpenProject_Click(object sender, RoutedEventArgs e) { SelectedAction = "open"; DialogResult = true; }
}
