#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Configurator;

internal static class Mode3DeleteShortcut
{
    public static void Handle(KeyEventArgs e, Button? action) => Handle(e, action, Keyboard.Modifiers);

    internal static void Handle(KeyEventArgs e, Button? action, ModifierKeys modifiers)
        => Handle(e, action?.IsEnabled == true, () => action!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), modifiers);

    internal static void Handle(KeyEventArgs e, bool enabled, System.Action execute, ModifierKeys? modifiers = null)
    {
        if (e.Key != Key.Delete || (modifiers ?? Keyboard.Modifiers) != ModifierKeys.None ||
            !enabled || IsEditing(e.OriginalSource as DependencyObject)) return;

        e.Handled = true;
        if (!e.IsRepeat) execute();
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
