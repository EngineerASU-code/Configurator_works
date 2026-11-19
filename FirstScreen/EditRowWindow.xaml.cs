using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Configurator
{
    public partial class EditRowWindow : Window
    {
        private List<ColumnInfo> _columns;
        private Dictionary<string, string> _currentValues;
        private string _keyColumn = "";
        private Dictionary<string, TextBox> _fieldInputs = new Dictionary<string, TextBox>();
        private readonly string _classNumberColumn;
        private readonly int? _forcedClassNumber;
        private readonly Dictionary<string, List<LookupOption>> _lookupOptions;
        private readonly Dictionary<string, ComboBox> _lookupInputs = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object> Values { get; private set; }
        public bool Confirmed { get; private set; } = false;

        public EditRowWindow(string tableName, List<ColumnInfo> columns,
            Dictionary<string, string> currentValues, string keyColumn,
            string classNumberColumn = null, int? forcedClassNumber = null, Dictionary<string, List<LookupOption>> lookupOptions = null)
        {
            InitializeComponent();
            _classNumberColumn = classNumberColumn ?? "";
            _forcedClassNumber = forcedClassNumber;
            _lookupOptions = lookupOptions ?? new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
            WindowTitle.Text = $"Изменить строку — {tableName}";
            _columns = columns;
            _currentValues = currentValues;
            _keyColumn = keyColumn;
            GenerateFields();
            ValidateFields();
        }

        private void GenerateFields()
        {
            foreach (var col in _columns)
            {
                if (col.IsReadOnly)
                    continue;

                var fieldPanel = new StackPanel
                {
                    Margin = new Thickness(0, 0, 12, 8)
                };

                bool isKey = col.Name.Equals(_keyColumn, StringComparison.OrdinalIgnoreCase);
                string normalizedColumnName = col.Name.Replace("_", "").Replace(" ", "").ToLowerInvariant();
                bool isClassNumber = (_forcedClassNumber.HasValue &&
                    !string.IsNullOrWhiteSpace(_classNumberColumn) &&
                    col.Name.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase)) ||
                    normalizedColumnName is "plcclassnumber" or "plcclasssnumber" or "classnumber" or "plcclassno";
                string titleText = isClassNumber ? "🔒 " : (isKey ? "🔑 " : "");
                titleText += $"{col.Name} ({col.DataType})";
                if (col.IsNullable) titleText += " — nullable";
                if (col.HasDefault) titleText += " — default";

                var label = new TextBlock
                {
                    Text = titleText,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3)
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");

                bool hasLookupDefinition = _lookupOptions.TryGetValue(col.Name, out var options);
                bool hasLookupOptions = hasLookupDefinition && options.Count > 0;
                bool isLookup = !isClassNumber && (hasLookupDefinition || IsKnownLookupField(col.Name));
                options ??= new List<LookupOption>();
                if (isLookup && _currentValues != null && _currentValues.TryGetValue(col.Name, out string existingLookupValue) && !string.IsNullOrWhiteSpace(existingLookupValue)
                    && !options.Any(option => string.Equals(option.Key, existingLookupValue, StringComparison.OrdinalIgnoreCase)))
                {
                    // Keep the existing key selectable even when it is missing from a
                    // populated lookup; otherwise saving another field would erase it.
                    var description = hasLookupOptions ? "значение отсутствует в справочнике" : "справочник недоступен";
                    options.Insert(0, new LookupOption { Key = existingLookupValue, Display = $"{existingLookupValue} · текущее значение, {description}" });
                }
                Control input;
                TextBox textBox = null;
                if (isLookup)
                {
                    var combo = new ComboBox { Height = 32, FontSize = 13, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = options, DisplayMemberPath = nameof(LookupOption.Display), SelectedValuePath = nameof(LookupOption.Key) };
                    _lookupInputs[col.Name] = combo;
                    if (!hasLookupOptions) { combo.IsEnabled = false; combo.ToolTip = "Для связанного поля не удалось загрузить справочник. Значение нельзя вводить вручную; проверьте подключение и наличие таблицы-справочника."; }
                    input = combo;
                    if (_currentValues != null && _currentValues.TryGetValue(col.Name, out string currentLookup)) combo.SelectedValue = currentLookup ?? "";
                }
                else
                {
                    textBox = new TextBox { Padding = new Thickness(8, 6, 8, 6), FontSize = 13, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch, IsReadOnly = isClassNumber };
                    if (_currentValues != null && _currentValues.TryGetValue(col.Name, out string currentVal))
                        textBox.Text = isClassNumber ? FormatClassNumberDisplay(currentVal, _forcedClassNumber) : currentVal ?? "";
                    input = textBox;
                }
                if (isClassNumber && textBox != null) textBox.ToolTip = "PLC Class Number определяет класс строки и не может быть изменён вручную.";
                if (isKey && textBox != null && !isClassNumber) textBox.ToolTip = "Ключевое поле можно изменить. Уникальность проверяется при сохранении.";

                fieldPanel.Children.Add(label);
                fieldPanel.Children.Add(input);
                if (textBox != null) _fieldInputs[col.Name] = textBox;

                if (!isKey || (isKey && !isClassNumber))
                {
                    string hint = col.HasDefault
                        ? "Пусто → поле не изменяется"
                        : col.IsNullable
                            ? "Пусто → NULL"
                            : "Обязательное поле";
                    var hintText = new TextBlock
                    {
                        Text = hint,
                        FontSize = 10,
                        Margin = new Thickness(0, 4, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    };
                    hintText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                    fieldPanel.Children.Add(hintText);
                    if (textBox != null) textBox.ToolTip = hint;
                }

                if (textBox != null) textBox.TextChanged += (s, e) => ValidateFields();
                if (input is ComboBox comboInput) comboInput.SelectionChanged += (s, e) => ValidateFields();

                FieldsPanel.Children.Add(fieldPanel);
            }
        }

        private static bool IsKnownLookupField(string name)
        {
            var n = (name ?? "").Replace("_", "").Replace(" ", "");
            return new[] { "Type", "PLC", "Area", "PLCClassNumber", "PLCClasssNumber", "Class", "Unit" }
                .Contains(n, StringComparer.OrdinalIgnoreCase);
        }

        private static string FormatClassNumberDisplay(string value, int? classNumber)
        {
            if (!classNumber.HasValue) return value ?? "";
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return value ?? "";

            var className = Configurator.Core.Architecture.SystemClassCatalog.CreateDefaults()
                .FirstOrDefault(item => item.ClassNumber == classNumber.Value)?.Name;
            return string.IsNullOrWhiteSpace(className) ? value : $"{value} · {className}";
        }

        private void ValidateFields()
        {
            var errors = new List<string>();
            foreach (var colInfo in _columns)
            {
                if (colInfo.IsReadOnly) continue;
                string fieldName = colInfo.Name;
                string valueText = _lookupInputs.TryGetValue(fieldName, out var lookupBox)
                    ? lookupBox.SelectedValue?.ToString() ?? ""
                    : _fieldInputs.TryGetValue(fieldName, out var textBox) ? textBox.Text : "";

                if (_forcedClassNumber.HasValue && !string.IsNullOrWhiteSpace(_classNumberColumn) &&
                    fieldName.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (fieldName.Equals(_keyColumn, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(valueText)) errors.Add($"Ключ \"{fieldName}\" не может быть пустым");
                    continue;
                }

                if (!colInfo.IsNullable && !colInfo.HasDefault && string.IsNullOrWhiteSpace(valueText))
                    errors.Add($"\"{fieldName}\" обязательно");
            }

            BtnSave.IsEnabled = errors.Count == 0;
            if (errors.Count > 0)
            {
                ValidationHint.Text = string.Join("\n", errors);
                ValidationHint.Visibility = Visibility.Visible;
            }
            else ValidationHint.Visibility = Visibility.Collapsed;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnSave.IsEnabled) return;
            Values = new Dictionary<string, object>();
            var errors = new List<string>();

            foreach (var colInfo in _columns)
            {
                if (colInfo.IsReadOnly) continue;
                string fieldName = colInfo.Name;
                if (_forcedClassNumber.HasValue && !string.IsNullOrWhiteSpace(_classNumberColumn) &&
                    fieldName.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = _lookupInputs.TryGetValue(fieldName, out var lookupBox)
                    ? lookupBox.SelectedValue?.ToString() ?? ""
                    : _fieldInputs.TryGetValue(fieldName, out var textBox) ? textBox.Text.Trim() : "";

                if (string.IsNullOrEmpty(text))
                {
                    if (colInfo.HasDefault) continue;
                    if (colInfo.IsNullable) { Values[fieldName] = DBNull.Value; continue; }
                    if (!fieldName.Equals(_keyColumn, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Поле \"{fieldName}\" обязательно");
                    continue;
                }

                try { Values[fieldName] = ConvertValue(text, colInfo); }
                catch (Exception ex) { errors.Add($"Поле \"{fieldName}\": {ex.Message}"); }
            }

            if (errors.Count > 0)
            {
                Configurator.Infrastructure.ModernDialog.Show(string.Join("\n", errors), "Ошибка ввода", MessageBoxButton.OK, MessageBoxImage.Warning);
                Values = null;
                return;
            }
            Confirmed = true;
            Close();
        }

        private static object ConvertValue(string text, ColumnInfo col)
        {
            if (col == null) return text;

            var dt = (col.DataType ?? "").ToLowerInvariant();

            if (dt == "int") return int.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "bigint") return long.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "smallint") return short.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "tinyint") return byte.Parse(text, CultureInfo.InvariantCulture);

            if (dt == "decimal" || dt == "numeric" || dt == "money" || dt == "smallmoney")
                return decimal.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "float") return double.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "real") return float.Parse(text, CultureInfo.InvariantCulture);

            if (dt == "date" || dt == "datetime" || dt == "datetime2" || dt == "smalldatetime")
                return DateTime.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "datetimeoffset") return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "time") return TimeSpan.Parse(text, CultureInfo.InvariantCulture);

            if (dt == "bit")
            {
                if (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("да", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("нет", StringComparison.OrdinalIgnoreCase))
                    return false;
                throw new FormatException("Ожидалось значение 0/1, true/false или да/нет.");
            }

            if (dt == "uniqueidentifier") return Guid.Parse(text);

            return text;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                BtnSave_Click(sender, e);
            else if (e.Key == Key.Escape)
                Close();
        }
    }
}
