using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public sealed class EditRowWindowTests(WpfDialogHost host)
{
    [Fact]
    public Task FailedSaveKeepsDraftUntilCorrected() => host.RunAsync(() =>
    {
        var window = new EditRowWindow("dbo.AnaOUT", [new() { Name = "Record", DataType = "int" }],
            new() { ["Record"] = "1" }, "Record");
        try
        {
            window.Show();
            var field = (StackPanel)window.FieldsPanel.Children[0];
            var input = (TextBox)field.Children[1];
            window.SaveChangesAsync = () =>
            {
                if (Equals(window.Values["Record"], 3)) return Task.FromResult(true);
                window.MarkConflict("Record");
                return Task.FromResult(false);
            };
            input.Text = "2";
            ApplicationCommands.Save.Execute(null, input);
            Assert.True(window.IsVisible);
            Assert.False(window.Confirmed);
            Assert.Equal("2", input.Text);
            Assert.Equal(System.Windows.Media.Brushes.MistyRose, input.Background);
            input.Text = "3";
            Assert.NotEqual(System.Windows.Media.Brushes.MistyRose, input.Background);
            ApplicationCommands.Save.Execute(null, input);
            Assert.True(window.Confirmed);
            Assert.False(window.IsVisible);
        }
        finally { if (window.IsVisible) window.Close(); }
        return Task.CompletedTask;
    });
    [Fact]
    public Task ReglerShowsIdentityFirstAndRetainsTypeWhenDictionaryIsEmpty() => host.RunAsync(() =>
    {
        var window = new EditRowWindow("dbo.Reglers",
            [new() { Name = "PLC", DataType = "int" }, new() { Name = "Type", DataType = "int" }, new() { Name = "Record", DataType = "int" }],
            new() { ["PLC"] = "1", ["Record"] = "10", ["Type"] = "1" }, "Record");
        try
        {
            window.Show();
            var fields = window.FieldsPanel.Children.Cast<StackPanel>().ToList();
            Assert.Equal(new[] { "Record (int)", "PLC (int)", "Type (int)" }, fields.Select(f => ((TextBlock)f.Children[0]).Text));
            var type = (ComboBox)fields[2].Children[1];
            Assert.Equal("1", ((LookupOption)type.SelectedItem).Display);
            Assert.False(type.IsEnabled);
            ApplicationCommands.Save.Execute(null, window);
            Assert.True(window.Confirmed);
            Assert.False(window.Values.ContainsKey("Type"));
        }
        finally { if (window.IsVisible) window.Close(); }
        return Task.CompletedTask;
    });

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
