using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public sealed class ObjectInputHintTests(WpfDialogHost host)
{
    [Fact]
    public Task PasteRecordStartsEmptyAndManualEntryOverridesHint() => host.RunAsync(() =>
    {
        var window = new TextInputWindow("Вставка", "Record", placeholder: "6");
        try
        {
            window.Show();
            Assert.Empty(window.InputText.Text);
            Assert.Empty(window.InputText.SelectedText);
            Assert.Equal("6", window.Result);
            var hint = ((Grid)window.InputText.Parent).Children.OfType<TextBlock>().Single();
            Assert.True(hint.IsVisible);
            window.InputText.Text = "12";
            Assert.False(hint.IsVisible);
            Assert.Equal("12", window.Result);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ScopeChangesRefreshBothHintsAndPreserveTypedAddress() => host.RunAsync(() =>
    {
        var window = new AddRowWindow("AnaOUT", new()
        {
            new() { Name = "Record", DataType = "int" },
            new() { Name = "Adress", DataType = "int" },
            new() { Name = "Address_output", DataType = "int" },
            new() { Name = "address_input", DataType = "nvarchar" },
            new() { Name = "PLC", DataType = "int" }
        }, 0, "Record", lookupOptions: new() { ["PLC"] = new()
        {
            new() { Key = "1", Display = "1" }, new() { Key = "2", Display = "2" }
        } }, scopedRecordProvider: (plc, _, _) => Task.FromResult(int.Parse(plc) + 4),
            freeAddressProvider: (plc, _, _) => Task.FromResult(new Dictionary<string, List<LookupOption>> { ["Adress"] = [new() { Key = (int.Parse(plc) * 100).ToString() }], ["Address_output"] = [new() { Key = "8" }], ["address_input"] = [new() { Key = "I1.2" }] }));
        try
        {
            window.Show();
            var plc = Descendants(window.FieldsPanel).OfType<ComboBox>().Single();
            Assert.Equal(4, Descendants(window.FieldsPanel).OfType<TextBlock>().Count(t => t.Text == "Выберите PLC" && t.IsVisible));
            plc.SelectedIndex = 0;
            var record = Descendants(window.FieldsPanel).OfType<TextBlock>().Single(t => t.Text == "5");
            var address = Descendants(window.FieldsPanel).OfType<TextBlock>().Single(t => t.Text == "100");
            var addressBox = ((Grid)address.Parent).Children.OfType<TextBox>().Single();
            Assert.Empty(addressBox.Text);
            addressBox.Text = "77";
            plc.SelectedIndex = 1;
            Assert.Equal("6", record.Text);
            Assert.Equal("200", address.Text);
            Assert.False(address.IsVisible);
            window.BtnConfirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.Confirmed);
            Assert.Equal(6, window.Values["Record"]);
            Assert.Equal(77, window.Values["Adress"]);
            Assert.Equal(8, window.Values["Address_output"]);
            Assert.Equal("I1.2", window.Values["address_input"]);
        }
        finally { if (window.IsVisible) window.Close(); }
        return Task.CompletedTask;
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
