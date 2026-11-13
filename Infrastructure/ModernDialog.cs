using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Configurator.Infrastructure;

/// <summary>Project-styled replacement for legacy system MessageBox alerts.</summary>
public static class ModernDialog
{
    public static MessageBoxResult Show(string message) => Show(null, message, "Configurator", MessageBoxButton.OK, MessageBoxImage.Information);
    public static MessageBoxResult Show(string message, string caption) => Show(null, message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons) => Show(null, message, caption, buttons, MessageBoxImage.None);
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image) => Show(null, message, caption, buttons, image);
    public static MessageBoxResult Show(Window owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        var win = new Window
        {
            Title = string.IsNullOrWhiteSpace(caption) ? "Configurator" : caption,
            Width = 500, MinWidth = 360, MaxWidth = 680, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = owner == null,
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 250)),
            Foreground = new SolidColorBrush(Color.FromRgb(35, 45, 58)),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13
        };
        var root = new Border { Margin = new Thickness(12), Padding = new Thickness(22), CornerRadius = new CornerRadius(14), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(222, 228, 235)), BorderThickness = new Thickness(1) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var color = image == MessageBoxImage.Error ? Color.FromRgb(190, 55, 55) : image == MessageBoxImage.Warning ? Color.FromRgb(181, 112, 24) : Color.FromRgb(42, 132, 83);
        heading.Children.Add(new Border { Width = 8, Height = 28, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        heading.Children.Add(new TextBlock { Text = win.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetRow(heading, 0); layout.Children.Add(heading);
        var body = new TextBlock { Text = message ?? string.Empty, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19, MaxWidth = 620, Margin = new Thickness(0, 0, 0, 22) };
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        MessageBoxResult result = MessageBoxResult.None;
        void AddButton(string label, MessageBoxResult value, bool primary)
        {
            var button = new Button { Content = label, MinWidth = 94, Height = 36, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 4, 14, 4), FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand, Background = primary ? new SolidColorBrush(Color.FromRgb(37, 126, 78)) : new SolidColorBrush(Color.FromRgb(242, 245, 248)), Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(45, 57, 70)), BorderBrush = primary ? new SolidColorBrush(Color.FromRgb(37, 126, 78)) : new SolidColorBrush(Color.FromRgb(213, 220, 228)), BorderThickness = new Thickness(1) };
            button.Click += (_, _) => { result = value; if (value == MessageBoxResult.None) win.Close(); else win.DialogResult = true; };
            buttonsPanel.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.YesNo: AddButton("Нет", MessageBoxResult.No, false); AddButton("Да", MessageBoxResult.Yes, true); break;
            case MessageBoxButton.YesNoCancel: AddButton("Отмена", MessageBoxResult.Cancel, false); AddButton("Нет", MessageBoxResult.No, false); AddButton("Да", MessageBoxResult.Yes, true); break;
            case MessageBoxButton.OKCancel: AddButton("Отмена", MessageBoxResult.Cancel, false); AddButton("ОК", MessageBoxResult.OK, true); break;
            default: AddButton("Понятно", MessageBoxResult.OK, true); break;
        }
        Grid.SetRow(buttonsPanel, 2); layout.Children.Add(buttonsPanel); root.Child = layout; win.Content = root;
        win.Loaded += (_, _) => { win.SizeToContent = SizeToContent.Height; };
        win.ShowDialog();
        return result;
    }
}
