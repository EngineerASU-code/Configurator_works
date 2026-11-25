#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Configurator;

internal static class Mode3DeleteShortcut
{
    public static void Handle(KeyEventArgs e, Button? action)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None ||
            action?.IsEnabled != true || IsEditing(e.OriginalSource as DependencyObject)) return;

        e.Handled = true;
        if (!e.IsRepeat) action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static bool IsEditing(DependencyObject? source)
    {
        for (var element = source; element is Visual; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is TextBoxBase or ComboBox or ComboBoxItem) return true;
            if (element is DataGridCell cell) return cell.IsEditing;
        }
        return false;
    }
}
