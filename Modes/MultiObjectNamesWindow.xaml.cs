using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Configurator;

public partial class MultiObjectNamesWindow : Window
{
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();
    public string SelectedPlc { get; private set; } = "1";

    public MultiObjectNamesWindow(string className, IEnumerable<(string Key, string Title)> plcOptions = null, string initial = "")
    {
        InitializeComponent();
        Title = $"Несколько новых объектов — {className}";
        NamesText.Text = initial;
        var options = (plcOptions ?? new[] { ("1", "PLC 1") }).ToList();
        PlcCombo.ItemsSource = options.Select(x => new PlcChoice { Key = x.Key, Title = x.Title }).ToList();
        PlcCombo.SelectedIndex = 0;
        Loaded += (_, _) => { NamesText.Focus(); NamesText.CaretIndex = NamesText.Text.Length; };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var names = NamesText.Text
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            Configurator.Infrastructure.ModernDialog.Show("Введите хотя бы одно имя объекта.", "Несколько новых объектов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Names = names;
        SelectedPlc = Convert.ToString(PlcCombo.SelectedValue) ?? "1";
        DialogResult = true;
    }

    private sealed class PlcChoice { public string Key { get; set; } = ""; public string Title { get; set; } = ""; }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
