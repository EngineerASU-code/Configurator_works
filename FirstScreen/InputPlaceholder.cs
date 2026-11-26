using System.Windows;
using System.Windows.Controls;

namespace Configurator;

internal static class InputPlaceholder
{
    internal static int FieldOrder(string name) => name.ToUpperInvariant() switch { "RECORD" => 0, "PLC" => 1, _ => 2 };

    internal static Grid WithAddressList(FrameworkElement editor, TextBox input, Func<Task<List<LookupOption>>> load)
    {
        var host = new Grid { Width = editor.Width };
        editor.Width = double.NaN;
        input.Width = double.NaN;
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.ColumnDefinitions.Add(new ColumnDefinition());
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.Children.Add(editor);
        var button = new Button { Content = "▾", Width = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 14, ToolTip = "Свободные адреса выбранного PLC" };
        Grid.SetColumn(button, 1);
        host.Children.Add(button);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var menu = new ContextMenu { PlacementTarget = host, MaxHeight = 260, MinWidth = host.ActualWidth, VerticalOffset = 3, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.SetResourceReference(FrameworkElement.StyleProperty, "AddressChoiceMenu");
            try
            {
                var options = await load();
                if (!button.IsLoaded) return;
                foreach (var option in options ?? new List<LookupOption>())
                {
                    var item = new MenuItem { Header = option.Display };
                    item.Click += (_, _) => input.Text = option.Key;
                    menu.Items.Add(item);
                }
                if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Нет доступных адресов", IsEnabled = false });
                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (button.IsLoaded)
                {
                    menu.Items.Add(new MenuItem { Header = "Не удалось загрузить адреса", IsEnabled = false });
                    menu.IsOpen = true;
                }
            }
            finally { button.IsEnabled = true; }
        };
        return host;
    }

    public static Grid Wrap(TextBox input, TextBlock hint)
    {
        var host = new Grid { Width = input.Width, HorizontalAlignment = HorizontalAlignment.Center };
        input.VerticalContentAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        hint.FontSize = input.FontSize;
        hint.FontFamily = input.FontFamily;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(input.Padding.Left + input.BorderThickness.Left + 3, 0, 4, 0);
        hint.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
        void Refresh() => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => Refresh();
        Refresh();
        host.Children.Add(input);
        host.Children.Add(hint);
        return host;
    }
}
