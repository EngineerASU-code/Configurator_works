using System.Windows;
using System.Windows.Controls;
using Configurator.Themes;

namespace Configurator;

public partial class ThemePickerWindow : Window
{
    public ThemePickerWindow()
    {
        InitializeComponent();
        Schemes.ItemsSource = ThemeManager.BuiltIn;
        Schemes.SelectedItem = ThemeManager.BuiltIn.FirstOrDefault(theme => theme.Name == ThemeManager.CurrentPreset.Name);
    }

    private void Scheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Schemes.SelectedItem is ThemePreset preset && preset.Name != ThemeManager.CurrentPreset.Name)
            ThemeManager.ApplyTheme(preset);
    }
}
