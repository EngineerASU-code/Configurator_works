using System.Data;
using System.Windows;

namespace Configurator;

internal sealed class PasteConflict
{
    public int Number { get; init; }
    public Dictionary<string, object> Key { get; set; }
    public Dictionary<string, object> Values { get; init; }
    public DataRow Local { get; init; }
    public Dictionary<string, object> Online { get; init; }
    public bool UniqueTarget { get; init; }
    public bool CanReplace { get; set; }
    public string Reason { get; set; }
    public bool Selected { get; set; }
    public string NewRecord { get; set; } = "";
    public string KeyText => string.Join(", ", Key.Select(field => $"{field.Key}={field.Value}"));
}

public partial class PasteConflictsWindow : Window
{
    private readonly List<PasteConflict> remaining;
    private readonly Func<List<PasteConflict>, bool, Task> resolve;
    public bool CanChangeRecord { get; }
    private bool busy;

    internal PasteConflictsWindow(List<PasteConflict> remaining, bool canChange, Func<List<PasteConflict>, bool, Task> resolve)
    {
        CanChangeRecord = canChange;
        InitializeComponent();
        this.remaining = remaining;
        this.resolve = resolve;

        Rows.ItemsSource = remaining;

        Closing += (_, e) => e.Cancel = busy;
        UpdateSelection();
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in remaining) row.Selected = All.IsChecked == true;
        Rows.Items.Refresh();
        UpdateSelection();
    }

    private void Row_Click(object sender, RoutedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var selected = remaining.Where(row => row.Selected).ToList();
        All.IsChecked = selected.Count == 0 ? false : selected.Count == remaining.Count ? true : null;
        Replace.IsEnabled = selected.Count > 0 && selected.All(row => row.CanReplace);
        Change.IsEnabled = selected.Count > 0 && CanChangeRecord;
        ErrorText.Visibility = Visibility.Collapsed;
        Status.Text = $"Осталось конфликтов: {remaining.Count}. Отмечено: {selected.Count}.";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {

        var selected = remaining.Where(row => row.Selected).ToList();
        busy = true;
        IsEnabled = false;
        try
        {
            await resolve(selected, sender == Replace);
            remaining.RemoveAll(row => row.Selected);
            Rows.Items.Refresh();
            UpdateSelection();
        }
        catch (Exception ex)
        {
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = ex is FormatException or InvalidOperationException ? ex.Message : "Не удалось проверить ключи. Повторите операцию.";
        }
        finally
        {
            busy = false;
            IsEnabled = true;
        }
        if (remaining.Count == 0) DialogResult = true;
    }
}
