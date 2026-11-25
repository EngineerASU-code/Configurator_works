using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public sealed class EditRowWindowTests(WpfDialogHost host)
{
    [Fact]
    public Task SaveShortcutUsesCurrentInputAndClosesEditor() => host.RunAsync(() =>
    {
        var window = new EditRowWindow("AnaOUT",
            [new ColumnInfo { Name = "Address", DataType = "int", IsNullable = true }],
            new() { ["Address"] = "1" }, "");
        try
        {
            window.Show();
            var field = Assert.IsType<StackPanel>(Assert.Single(window.FieldsPanel.Children.Cast<UIElement>()));
            Assert.Equal(2, field.Children.Count);
            Assert.Equal("Address (int)", Assert.IsType<TextBlock>(field.Children[0]).Text);
            var input = Assert.IsType<TextBox>(field.Children[1]);
            input.Text = "2";
            Assert.Contains(ApplicationCommands.Save.InputGestures.OfType<KeyGesture>(),
                gesture => gesture.Key == Key.S && gesture.Modifiers == ModifierKeys.Control);
            ApplicationCommands.Save.Execute(null, input);
            Assert.True(window.Confirmed);
            Assert.False(window.IsVisible);
            Assert.Equal(2, window.Values["Address"]);
        }
        finally { if (window.IsVisible) window.Close(); }
        return Task.CompletedTask;
    });
}
