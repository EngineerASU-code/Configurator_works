using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Configurator;

internal partial class DuplicateDraftWindow : Window
{
    private readonly DataTable drafts;
    private readonly DataRow[] originals;
    private DataColumn field;
    private int errors;
    private readonly Func<Task> assignAddresses;
    private readonly string addressField;
    private string[] assignedPlcs;
    private readonly Func<string> validate;
    private readonly Dictionary<string,List<LookupOption>> lookups;

    internal static bool Edit(Window owner, string className, DataTable drafts, DataRow[] originals = null, Func<string> validate = null, Dictionary<string,List<LookupOption>> lookups = null, Func<Task> assignAddresses = null, string addressField = null, bool isInput = false)
    {
        var dialog = new DuplicateDraftWindow(className, drafts, originals, validate, lookups, assignAddresses, addressField, isInput) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private DuplicateDraftWindow(string className, DataTable drafts, DataRow[] originals, Func<string> validate, Dictionary<string,List<LookupOption>> lookups, Func<Task> assignAddresses, string addressField, bool isInput)
    {
        this.assignAddresses = assignAddresses;
        this.addressField = addressField;
        this.validate = validate;
        this.lookups = lookups ?? new(StringComparer.OrdinalIgnoreCase);
        this.drafts = drafts;
        this.originals = originals ?? drafts.Copy().Rows.Cast<DataRow>().ToArray();
        InitializeComponent();
        PickAddresses.Content = isInput ? "Подобрать свободные входы" : "Подобрать свободные выходы";
        PickAddresses.Visibility = assignAddresses == null ? Visibility.Collapsed : Visibility.Visible;
        Width = Math.Min(1560, SystemParameters.WorkArea.Width * 0.94);
        Height = Math.Min(840, SystemParameters.WorkArea.Height * 0.90);
        Title = $"Дублирование {className} · {drafts.Rows.Count}";
        Create.Content = "Проверить изменения";
        FilterFields(null,null);
    }

    private void FilterFields(object sender, TextChangedEventArgs e)
    {
        if (Fields == null || errors != 0) return;
        var names = drafts.Columns.Cast<DataColumn>().Select(c => c.ColumnName)
            .Where(n => n.Contains(Search.Text,StringComparison.OrdinalIgnoreCase)).ToList();
        var previous = field?.ColumnName ?? "Name";
        Fields.ItemsSource = names;
        Fields.SelectedItem = names.Contains(previous) ? previous : names.FirstOrDefault();
    }

    private void FieldChanged(object sender, SelectionChangedEventArgs e)
    {
        field = Fields.SelectedItem is string name ? drafts.Columns[name] : null;
        Render();
    }

    private void Render()
    {
        Rows.Children.Clear();
        Heading.Text = field == null ? "Поле не найдено" : field.ColumnName + (field.ReadOnly ? " · только чтение" : "");
        ReplaceBar.Visibility = field?.DataType == typeof(string) && !field.ReadOnly && !lookups.ContainsKey(field.ColumnName) ? Visibility.Visible : Visibility.Collapsed;
        if (field == null) { UpdateCount(null,null); return; }
        bool editingName = field.ColumnName.Equals("Name", StringComparison.OrdinalIgnoreCase);
        BeforeHeader.Visibility = editingName ? Visibility.Collapsed : Visibility.Visible;
        Headers.Columns = editingName ? 2 : 3;
        for (int i = 0; i < drafts.Rows.Count; i++)
        {
            var source = originals[i];
            var row = drafts.Rows[i];
            string title = source.Table.Columns.Contains("Name") ? Convert.ToString(source["Name"]) : "";
            var cells = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0,3,0,3) };
            void Label(string text) => cells.Children.Add(new TextBlock { Text = text, ToolTip = text,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) });
            var identity = new StackPanel { Margin = new Thickness(0,4,8,4) };
            identity.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? $"Объект {i+1}" : title, FontWeight = FontWeights.SemiBold });
            var context = new List<string>();
            if (source.Table.Columns.Contains("PLC")) context.Add($"PLC {source["PLC"]}");
            if (source.Table.Columns.Contains("Record")) context.Add($"Record {source["Record"]}");
            var detail = new TextBlock { Text = string.Join(" · ",context), FontSize = 12 };
            detail.SetResourceReference(TextBlock.ForegroundProperty,"BrushTextSecondary");
            identity.Children.Add(detail);
            cells.Children.Add(identity);
            if (!editingName) Label(source.IsNull(field.ColumnName) ? "—" : Convert.ToString(source[field.ColumnName]));
            if (!field.ReadOnly && lookups.TryGetValue(field.ColumnName,out var choices) && choices.Count > 0)
            {
                var column = field;
                var current = Convert.ToString(row[column]);
                var options = choices.ToList();
                if (!options.Any(o => o.Key == current)) options.Add(new LookupOption { Key = current, Display = current.Length == 0 ? "—" : current });
                var picker = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(LookupOption.Display),
                    SelectedValuePath = nameof(LookupOption.Key), SelectedValue = current };
                picker.SelectionChanged += (_,_) =>
                {
                    if (picker.SelectedValue is string key) row[column] = key.Length == 0 ? DBNull.Value : key;
                };
                cells.Children.Add(picker);
                Rows.Children.Add(cells);
                continue;
            }
            var input = new TextBox { Text = Convert.ToString(row[field]), IsReadOnly = field.ReadOnly || field.DataType == typeof(byte[]) };
            bool invalid = false;
            input.TextChanged += (_,_) =>
            {
                bool failed = false;
                try { row[field] = input.Text.Length == 0 && field.DataType != typeof(string) ? DBNull.Value : input.Text; }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                { failed = true; }
                if (failed != invalid) errors += failed ? 1 : -1;
                invalid = failed;
                Fields.IsEnabled = Search.IsEnabled = Create.IsEnabled = errors == 0;
                UpdateCount(null,null);
            };
            cells.Children.Add(input);
            Rows.Children.Add(cells);
        }
        UpdateCount(null,null);
    }

    private void UpdateCount(object sender, TextChangedEventArgs e)
    {
        if (Apply == null) return;
        int matches = field?.DataType == typeof(string) && FindText.Text.Length > 0
            ? drafts.Rows.Cast<DataRow>().Count(r => r[field] is string v && v.Contains(FindText.Text,StringComparison.Ordinal)) : 0;
        Apply.IsEnabled = matches > 0 && errors == 0;
        Status.Text = errors != 0 ? $"Проверьте значение поля {field.ColumnName} ({field.DataType.Name})" :
            ReplaceBar.IsVisible && FindText.Text.Length > 0 ? $"Совпадения в копиях: {matches} из {drafts.Rows.Count}" : "";
    }

    private void Replace(object sender, RoutedEventArgs e)
    {
        if (!Apply.IsEnabled || field.ReadOnly) return;
        foreach (DataRow row in drafts.Rows)
            if (row[field] is string value) row[field] = value.Replace(FindText.Text,NewText.Text,StringComparison.Ordinal);
        Render();
    }

    private async void AssignAddresses(object sender, RoutedEventArgs e)
    {
        if (assignAddresses == null || errors != 0) return;
        Editor.IsEnabled = Create.IsEnabled = PickAddresses.IsEnabled = false;
        Status.Text = "Проверка карты адресов PLC…";
        try
        {
            await assignAddresses();
            if (!IsVisible) return;
            assignedPlcs = drafts.Rows.Cast<DataRow>().Select(r => Convert.ToString(r["PLC"])).ToArray();
            Search.Clear();
            Fields.SelectedItem = addressField;
            Render();
            Status.Text = $"Подобрано адресов: {drafts.Rows.Count}";
        }
        catch (Exception ex) { Status.Text = Infrastructure.UserErrorMessage.Format(ex); }
        finally { Editor.IsEnabled = Create.IsEnabled = PickAddresses.IsEnabled = true; }
    }

    private void GoBack(object sender, RoutedEventArgs e)
    {
        Review.Visibility = Back.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        PickAddresses.Visibility = assignAddresses == null ? Visibility.Collapsed : Visibility.Visible;
        Create.Content = "Проверить изменения";
        UpdateCount(null,null);
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (errors != 0) return;
        if (Review.IsVisible) { DialogResult = true; return; }
        if (assignedPlcs != null && !assignedPlcs.SequenceEqual(drafts.Rows.Cast<DataRow>().Select(r => Convert.ToString(r["PLC"]))))
        {
            Status.Text = "PLC изменён. Подберите адреса для нового PLC.";
            return;
        }
        string issue = validate?.Invoke();
        if (!string.IsNullOrEmpty(issue)) { Status.Text = issue; return; }
        Summary.Children.Clear();
        Summary.RowDefinitions.Clear();
        for (int i = 0; i < drafts.Rows.Count; i++)
        {
            var source = originals[i];
            var draft = drafts.Rows[i];
            string name = source.Table.Columns.Contains("Name") ? Convert.ToString(source["Name"]) : "";
            var heading = $"Копия {i+1} · {name}";
            if (drafts.Columns.Contains("PLC")) heading += $" · PLC {draft["PLC"]}";
            if (drafts.Columns.Contains("Record")) heading += $" · Record {draft["Record"]}";
            if (i % 2 == 0) Summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var content = new StackPanel();
            var card = new Border { Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Margin = new Thickness(4), Padding = new Thickness(12,8,12,8) };
            card.SetResourceReference(Border.BorderBrushProperty,"BrushBorder");
            card.SetResourceReference(Border.BackgroundProperty,"BrushBase");
            Grid.SetColumn(card,i % 2);
            Grid.SetRow(card,i / 2);
            if (drafts.Rows.Count == 1) Grid.SetColumnSpan(card,2);
            Summary.Children.Add(card);
            content.Children.Add(new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,6) });
            int changes = 0;
            foreach (DataColumn column in drafts.Columns)
            {
                var before = source[column.ColumnName];
                var after = draft[column];
                if (!(assignedPlcs != null && column.ColumnName == addressField) &&
                    System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(before,after)) continue;
                string Show(object v) => v == DBNull.Value ? "NULL" : v is string text && text.Length == 0 ? "«»" : Convert.ToString(v);
                content.Children.Add(new TextBlock { FontSize = 17, Margin = new Thickness(4,2,0,3), Text = $"{column.ColumnName}    {Show(before)}  →  {Show(after)}", TextWrapping = TextWrapping.Wrap });
                changes++;
            }
            if (changes == 0) content.Children.Add(new TextBlock { FontSize = 17, Text = "Значения исходного объекта" });
        }
        Editor.Visibility = PickAddresses.Visibility = Visibility.Collapsed;
        Review.Visibility = Back.Visibility = Visibility.Visible;
        Create.Content = $"Создать копии ({drafts.Rows.Count})";
        Status.Text = "";
    }
}
