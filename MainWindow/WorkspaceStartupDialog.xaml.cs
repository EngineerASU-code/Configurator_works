using System.Windows;

namespace Configurator;

public partial class WorkspaceStartupDialog : Window
{
    public event EventHandler StartRequested;

    public WorkspaceStartupDialog()
    {
        InitializeComponent();
        Width = Math.Min(1440, SystemParameters.WorkArea.Width * 0.96);
        Height = Math.Min(900, SystemParameters.WorkArea.Height * 0.94);
        if (AppSettings.Load().ShowStartupAnimation && SystemParameters.ClientAreaAnimation)
            ContentRendered += (_, _) => Intro.Play(ShowWelcome);
        else
            ShowWelcome();
    }

    private void ShowWelcome()
    {
        Intro.Visibility = Visibility.Collapsed;
        Welcome.Visibility = Visibility.Visible;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);
}
