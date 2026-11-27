using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Configurator;

internal static class InputPlaceholder
{
    internal static int FieldOrder(string name) => name.ToUpperInvariant() switch { "RECORD" => 0, "PLC" => 1, _ => 2 };

    internal static bool IsFlag(ColumnInfo column)
    {
        var name = column.Name.Replace("_", "");
        return (column.DataType.ToLowerInvariant() is "bit" or "boolean") ||
            (column.DataType.ToLowerInvariant() is "int" or "bigint" or "tinyint" or "smallint" or "int32" or "int64" or "byte" or "int16") &&
            ((name.StartsWith("Use", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("User", StringComparison.Ordinal)) ||
             name.Equals("Archiving", StringComparison.OrdinalIgnoreCase) || name.Equals("Archieving", StringComparison.OrdinalIgnoreCase));
    }

    internal static string EmptyHint(ColumnInfo col)
    {
        if (col.HasDefault) return "DEFAULT";
        return col.DataType.ToLowerInvariant() switch
        {
            "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or
            "money" or "smallmoney" or "real" or "float" => "0",
            _ => "—"
        };
    }

    internal static Grid WrapField(TextBox input, ColumnInfo column)
    {
        string hint = EmptyHint(column);
        if (hint == "0") input.Tag = hint;
        var editor = Wrap(input, new TextBlock { Text = hint });
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        return editor;
    }

    internal static string FieldValue(TextBox input) =>
        input.Text.Length == 0 && input.Tag is string fallback ? fallback : input.Text;

    internal static Grid Lookup(ComboBox picker)
    {
        var host = new Grid();
        var hint = new TextBlock { Text = "—",
            Margin = new Thickness(10, 0, 28, 0), VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
        hint.Visibility = picker.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
        picker.SelectionChanged += (_, _) => hint.Visibility = picker.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
        host.Children.Add(picker);
        host.Children.Add(hint);
        return host;
    }

    internal static Border Flag(ColumnInfo col, TextBox textBox)
    {
        var halves = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };
        var frame = new Border { Height = 30, BorderThickness = new Thickness(1), Child = halves };
        frame.SetResourceReference(Border.BorderBrushProperty, "BrushBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "BrushPanel");
        bool refreshing = false;
        foreach (var value in new[] { "0", "1" })
        {
            var button = new RadioButton { Tag = value, Content = value == "1" ? "Да" : "Нет",
                BorderThickness = new Thickness(value == "1" ? 1 : 0, 0, 0, 0) };
            button.SetResourceReference(FrameworkElement.StyleProperty, "RowFlagSegment");
            System.Windows.Automation.AutomationProperties.SetName(button, col.Name + " — " + button.Content);
            button.Checked += (_, _) => { if (!refreshing) textBox.Text = value; };
            halves.Children.Add(button);
        }
        void Refresh()
        {
            if (refreshing) return;
            refreshing = true;
            string raw = textBox.Text.Trim();
            bool on = raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                (long.TryParse(raw, out long number) && number != 0);
            textBox.Text = on ? "1" : "0";
            ((RadioButton)halves.Children[0]).IsChecked = !on;
            ((RadioButton)halves.Children[1]).IsChecked = on;
            refreshing = false;
        }
        textBox.TextChanged += (_, _) => Refresh();
        Refresh();
        return frame;
    }
    internal static Grid WithAddressList(FrameworkElement editor, TextBox input, Func<Task<List<LookupOption>>> load, bool msType = false)
    {
        var host = new Grid { Width = editor.Width };
        editor.Width = double.NaN;
        input.Width = double.NaN;
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.ColumnDefinitions.Add(new ColumnDefinition());
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.Children.Add(editor);
        var button = new Button { Content = "▾", Width = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 14, ToolTip = msType ? "Значения MS_Type этого класса" : "Свободные адреса выбранного PLC" };
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
                if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = msType ? "У класса пока нет значений MS_Type" : "Нет доступных адресов", IsEnabled = false });
                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (button.IsLoaded)
                {
                    menu.Items.Add(new MenuItem { Header = msType ? "Не удалось загрузить значения MS_Type" : "Не удалось загрузить адреса", IsEnabled = false });
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
