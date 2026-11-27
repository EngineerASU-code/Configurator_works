using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly Func<string, Task<List<LookupOption>>> _msTypes;
        private readonly Dictionary<string, List<LookupOption>> _lookupOptions;
        private readonly Dictionary<string, ComboBox> _lookupInputs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, CancellationToken, Task<int>> _suggestScopedRecord;
        private CancellationTokenSource _recordSuggestion;

        public Dictionary<string, object> Values { get; private set; }
        internal Func<Task<bool>> SaveChangesAsync { get; set; }
        private bool _saving;
        internal void MarkConflict(string field)
        {
            Control input = _fieldInputs.GetValueOrDefault(field);
            input ??= _lookupInputs.GetValueOrDefault(field);
            if (input == null) return;
            input.Background = System.Windows.Media.Brushes.MistyRose;
            input.Focus();
            input.BringIntoView();
        }

        internal Func<bool> CanApply { get; set; }
        public bool Confirmed { get; private set; } = false;

        public EditRowWindow(string tableName, List<ColumnInfo> columns,
            Dictionary<string, string> currentValues, string keyColumn,
            string classNumberColumn = null, int? forcedClassNumber = null, Dictionary<string, List<LookupOption>> lookupOptions = null,
            Func<string, CancellationToken, Task<int>> suggestScopedRecord = null,
            Func<string, Task<Dictionary<string, List<LookupOption>>>> addresses = null,
            Func<string, Task<List<LookupOption>>> msTypes = null)
        {
            InitializeComponent();
            _classNumberColumn = classNumberColumn ?? "";
            _forcedClassNumber = forcedClassNumber;
            _msTypes = msTypes;
            _lookupOptions = lookupOptions ?? new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
            _suggestScopedRecord = suggestScopedRecord;
            WindowTitle.Text = $"Изменить строку — {tableName}";
            _columns = columns.Where(c => !MainWindow.HideLanguageField(c.Name)).ToList();
            _currentValues = currentValues;
            _keyColumn = keyColumn;
            var screen = SystemParameters.WorkArea;
            MaxHeight = screen.Height;
            Width = Math.Min(screen.Width, columns.Count(c => !c.IsReadOnly) > 12 ? 1080 : 700);
            FieldsPanel.Columns = Width >= 900 ? 4 : 3;
            SizeChanged += (_, e) => FieldsPanel.Columns = e.NewSize.Width >= 900 ? 4 : 3;
            GenerateFields(addresses);
            ValidateFields();
            Closing += (_, e) => e.Cancel = _saving;
            Closed += (_, _) => _recordSuggestion?.Cancel();
        }

        private void GenerateFields(Func<string, Task<Dictionary<string, List<LookupOption>>>> addresses)
        {
            foreach (var col in _columns.OrderBy(c => InputPlaceholder.FieldOrder(c.Name)))
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
                var label = new TextBlock
                {
                    Text = $"{col.Name} ({col.DataType})",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3)
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");

                bool hasLookupDefinition = _lookupOptions.TryGetValue(col.Name, out var options);
                bool hasLookupOptions = hasLookupDefinition && options.Count > 0;
                bool isLookup = !MsTypeCatalog.IsField(col.Name) && !isClassNumber && (hasLookupDefinition || IsKnownLookupField(col.Name));
                options ??= new List<LookupOption>();
                if (isLookup && _currentValues != null && _currentValues.TryGetValue(col.Name, out string existingLookupValue) && !string.IsNullOrWhiteSpace(existingLookupValue)
                    && !options.Any(option => string.Equals(option.Key, existingLookupValue, StringComparison.OrdinalIgnoreCase)))
                {
                    // Keep the existing key selectable even when it is missing from a
                    // populated lookup; otherwise saving another field would erase it.
                    options = options.ToList();
                    options.Insert(0, new LookupOption { Key = existingLookupValue, Display = hasLookupOptions ? $"{existingLookupValue} · отсутствует в справочнике" : existingLookupValue });
                }
                Control input;
                TextBox textBox = null;
                if (isLookup)
                {
                    var combo = new ComboBox { Height = 32, FontSize = 13, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = options, DisplayMemberPath = nameof(LookupOption.Display), SelectedValuePath = nameof(LookupOption.Key) };
                    _lookupInputs[col.Name] = combo;
                    if (!hasLookupOptions)
                    {
                        combo.IsEnabled = false;
                        combo.ToolTip = "Нет вариантов из справочника. При сохранении строки текущее значение сохранится.";
                        ToolTipService.SetShowOnDisabled(combo, true);
                    }
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
                if (textBox != null && InputPlaceholder.IsFlag(col))
                    fieldPanel.Children.Add(InputPlaceholder.Flag(col, textBox));
                else if (input is ComboBox picker)
                    fieldPanel.Children.Add(InputPlaceholder.Lookup(picker));
                else if (textBox != null)
                {
                    var editor = InputPlaceholder.WrapField(textBox, col);
                    if (_msTypes != null && MsTypeCatalog.IsField(col.Name))
                    {
                        var scope = ProjectClassCatalog.FindClassColumn(_columns.Select(c => c.Name));
                        fieldPanel.Children.Add(InputPlaceholder.WithAddressList(editor, textBox, () =>
                            _msTypes(_forcedClassNumber?.ToString() ?? (scope == null ? null : _lookupInputs.GetValueOrDefault(scope)?.SelectedValue?.ToString() ?? _fieldInputs.GetValueOrDefault(scope)?.Text ?? _currentValues?.GetValueOrDefault(scope))), true));
                    }
                    else if (addresses != null && PlcAddressCatalog.IsSuggestedField(col.Name))
                    fieldPanel.Children.Add(InputPlaceholder.WithAddressList(editor, textBox, async () =>
                    {
                        string plc = _lookupInputs.GetValueOrDefault("PLC")?.SelectedValue?.ToString();
                        if (string.IsNullOrWhiteSpace(plc)) return null;
                        var choices = await addresses(plc);
                        return plc == _lookupInputs.GetValueOrDefault("PLC")?.SelectedValue?.ToString() ? choices.GetValueOrDefault(col.Name) : null;
                    }));
                    else fieldPanel.Children.Add(editor);
                }
                if (textBox != null) _fieldInputs[col.Name] = textBox;

                if (textBox != null) textBox.TextChanged += (s, e) => { textBox.ClearValue(Control.BackgroundProperty); ValidateFields(); };
                if (input is ComboBox comboInput) comboInput.SelectionChanged += (s, e) => { comboInput.ClearValue(Control.BackgroundProperty); ValidateFields(); };

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
                    : _fieldInputs.TryGetValue(fieldName, out var textBox) ? InputPlaceholder.FieldValue(textBox) : "";

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

            BtnSave.IsEnabled = errors.Count == 0 && _recordSuggestion == null;
            if (errors.Count > 0)
            {
                ValidationHint.Text = string.Join("\n", errors);
                ValidationHint.Visibility = Visibility.Visible;
            }
            else ValidationHint.Visibility = Visibility.Collapsed;
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_saving || !BtnSave.IsEnabled) return;
            Values = new Dictionary<string, object>();
            var errors = new List<string>();

            foreach (var colInfo in _columns)
            {
                if (colInfo.IsReadOnly) continue;
                string fieldName = colInfo.Name;
                if (_forcedClassNumber.HasValue && !string.IsNullOrWhiteSpace(_classNumberColumn) &&
                    fieldName.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase))
                    continue;

                string input = _lookupInputs.TryGetValue(fieldName, out var lookupBox)
                    ? lookupBox.SelectedValue?.ToString() ?? ""
                    : _fieldInputs.TryGetValue(fieldName, out var textBox) ? InputPlaceholder.FieldValue(textBox) : "";
                if (_currentValues != null && _currentValues.TryGetValue(fieldName, out var original) &&
                    string.Equals(input, original ?? "", StringComparison.Ordinal))
                    continue;
                string text = input.Trim();

                if (string.IsNullOrEmpty(text))
                {
                    if (colInfo.IsNullable) { Values[fieldName] = DBNull.Value; continue; }
                    if (colInfo.HasDefault) continue;
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

            if (_suggestScopedRecord != null && !await ApplyScopedRecordSuggestionAsync()) return;
            if (CanApply != null && !CanApply())
            {
                Infrastructure.ModernDialog.Show(this, "Исходная таблица или запись изменилась. Вернитесь к ней или откройте редактор заново.", "Сохранение строки", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (SaveChangesAsync != null && Values.Count > 0)
            {
                _saving = true;
                IsEnabled = false;
                try
                {
                    if (!await SaveChangesAsync()) return;
                }
                finally
                {
                    IsEnabled = true;
                    _saving = false;
                }
            }
            Confirmed = true;
            Close();
        }

        private async Task<bool> ApplyScopedRecordSuggestionAsync()
        {
            var plc = _columns.First(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase));
            var record = _columns.First(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));
            if (!Values.TryGetValue(plc.Name, out var newPlc) ||
                !_lookupInputs.TryGetValue(plc.Name, out var plcInput) ||
                !_fieldInputs.TryGetValue(record.Name, out var recordInput)) return true;

            _currentValues.TryGetValue(plc.Name, out var oldPlc);
            _currentValues.TryGetValue(record.Name, out var oldRecord);
            var selectedPlc = plcInput.SelectedValue?.ToString();
            if (string.Equals(selectedPlc ?? "", oldPlc ?? "", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(recordInput.Text.Trim(), oldRecord?.Trim() ?? "", StringComparison.Ordinal)) return true;

            using var request = new CancellationTokenSource();
            _recordSuggestion = request;
            BtnSave.IsEnabled = false;
            FieldsPanel.IsEnabled = false;
            try
            {
                int suggestion = await _suggestScopedRecord(newPlc == DBNull.Value ? null : selectedPlc, request.Token);
                request.Token.ThrowIfCancellationRequested();
                string text = suggestion.ToString(CultureInfo.InvariantCulture);
                Values[record.Name] = ConvertValue(text, record);
                recordInput.Text = text;
                return true;
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (!request.IsCancellationRequested)
                    Configurator.Infrastructure.ModernDialog.Show("Не удалось подобрать свободный Record для выбранного PLC. Укажите значение вручную.");
                return false;
            }
            finally
            {
                _recordSuggestion = null;
                if (!request.IsCancellationRequested)
                {
                    FieldsPanel.IsEnabled = true;
                    ValidateFields();
                }
            }
        }

        internal static object ConvertValue(string text, ColumnInfo col)
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
