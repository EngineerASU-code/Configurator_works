using System.Windows;
using System.Windows.Controls;

namespace Configurator;

public partial class TextInputWindow : Window
{
    private readonly string _placeholder;
    public string Result => string.IsNullOrWhiteSpace(InputText.Text) ? _placeholder ?? "" : InputText.Text.Trim();

    public TextInputWindow(string title, string prompt, string initial = "", string placeholder = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        _placeholder = placeholder;
        InputText.Text = initial;
        if (placeholder != null)
        {
            InputHost.Children.Clear();
            InputHost.Children.Add(InputPlaceholder.Wrap(InputText, new System.Windows.Controls.TextBlock { Text = placeholder }));
        }
        Loaded += (_, _) => { InputText.Focus(); if (InputText.Text.Length > 0) InputText.SelectAll(); };
    }

    private readonly Dictionary<string, (TextBox Input, string Hint)> _addresses = new();
    public IEnumerable<KeyValuePair<string, string>> AddressResults => _addresses.Select(pair =>
        KeyValuePair.Create(pair.Key, string.IsNullOrWhiteSpace(pair.Value.Input.Text) ? pair.Value.Hint : pair.Value.Input.Text.Trim()));

    public void OfferAddress(string field, List<LookupOption> options)
    {
        var input = new TextBox { Width = InputText.Width, MinHeight = InputText.MinHeight, Padding = InputText.Padding };
        string hint = options[0].Key;
        _addresses.Add(field, (input, hint));
        AddressPanel.Children.Add(new TextBlock { Text = field, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        AddressPanel.Children.Add(InputPlaceholder.WithAddressList(InputPlaceholder.Wrap(input, new TextBlock { Text = hint }), input, () => Task.FromResult(options)));
        AddressPanel.Visibility = Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Result)) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
