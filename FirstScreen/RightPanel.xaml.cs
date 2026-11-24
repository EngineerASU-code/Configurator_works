using System.Windows.Controls;

using System.Windows;

namespace Configurator;

public partial class RightPanel : UserControl
{
    public static readonly DependencyProperty HasActiveColumnViewProperty =
        DependencyProperty.Register(nameof(HasActiveColumnView), typeof(bool), typeof(RightPanel), new PropertyMetadata(false));

    public bool HasActiveColumnView
    {
        get => (bool)GetValue(HasActiveColumnViewProperty);
        set => SetValue(HasActiveColumnViewProperty, value);
    }

    public RightPanel() => InitializeComponent();
}
