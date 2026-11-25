#nullable enable
using System.Globalization;
using System.Windows;
using Configurator.Core.Compare;

namespace Configurator;

public partial class CompareWriteWindow : Window
{
    private readonly List<FieldEdit> _fields = new();
    internal CompareTransferPreview Preview { get; private set; }

    internal CompareWriteWindow(CompareTransferPreview preview)
    {
        InitializeComponent();
        Preview = preview;
        SummaryText.Text = preview.Summary;
        foreach (var (name, value) in preview.Values)
        {
            string current = "Отсутствует";
            if (preview.ExpectedRecord?.Values.TryGetPropertyValue(name, out var previous) == true)
                current = previous?.ToString() ?? "NULL";
            var column = preview.TargetSchema!.Columns.First(item => item.Name == name);
            _fields.Add(new FieldEdit(name, current, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                value is null, preview.ExpectedRecord is null && !name.Equals("Record", StringComparison.OrdinalIgnoreCase), column.IsNullable));
        }
        Fields.ItemsSource = _fields;
    }

    private void Write_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Preview = Preview.WithEdits(_fields.Where(field => field.Changed)
                .ToDictionary(field => field.Name, field => field.IsNull ? null : field.Text));
            DialogResult = true;
        }
        catch (InvalidOperationException ex)
        {
            ValidationText.Text = ex.Message;
        }
    }

    private sealed class FieldEdit(string name, string current, string original, bool wasNull, bool readOnly, bool nullable)
    {
        private readonly string _original = original;
        private readonly bool _wasNull = wasNull;
        public string Name => name;
        public string Current => current;
        public string Text { get; set; } = original;
        public bool IsNull { get; set; } = wasNull;
        public bool ReadOnly => readOnly;
        public bool CanSetNull => nullable && !readOnly;
        public bool Changed => !readOnly && (IsNull != _wasNull || (!IsNull && Text != _original));
    }
}
