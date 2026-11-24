using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Configurator
{
    public partial class AddRowWindow : Window
    {
        private readonly List<ColumnInfo> _columns;
        private readonly Dictionary<string, TextBox> _fieldInputs = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        private readonly string _recordColumn;
        private readonly int _autoRecordValue;
        private readonly string _tableName;
        private readonly string _connectionString;
        private readonly string _plcColumn;
        private CancellationTokenSource _recordCts;
        private int _recordGeneration;
        private bool _updatingAutoRecord;
        private readonly int? _forcedClassNumber;
        private readonly string _classNumberColumn;
        private readonly Dictionary<string, List<LookupOption>> _lookupOptions;
        private readonly Func<string, CancellationToken, Task<int>> _scopedRecordProvider;
        private readonly Func<string, string, CancellationToken, Task<bool>> _recordExistsProvider;
        private readonly bool _editMode;
        private readonly bool _preferFreeScopedRecord;
        private string _suggestedRecordText;
        private CancellationTokenSource _recordCheckCts;
        private readonly Dictionary<string, ComboBox> _lookupInputs = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object> Values { get; private set; }
        public bool Confirmed { get; private set; } = false;

        public AddRowWindow(
            string tableName,
            List<ColumnInfo> columns,
            int nextRecordValue,
            string recordColumn,
            string connectionString = null,
            string plcColumn = null,
            int? forcedClassNumber = null,
            string classNumberColumn = null,
            Dictionary<string, List<LookupOption>> lookupOptions = null,
            Dictionary<string, object> initialValues = null,
            bool editMode = false,
            Func<string, CancellationToken, Task<int>> scopedRecordProvider = null,
            Func<string, string, CancellationToken, Task<bool>> recordExistsProvider = null,
            bool preferFreeScopedRecord = false)
        {
            InitializeComponent();
            _forcedClassNumber = forcedClassNumber;
            _classNumberColumn = classNumberColumn ?? "";
            _lookupOptions = lookupOptions ?? new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
            _scopedRecordProvider = scopedRecordProvider;
            _recordExistsProvider = recordExistsProvider;
            _editMode = editMode;
            _preferFreeScopedRecord = preferFreeScopedRecord;
            _tableName = tableName ?? "";
            WindowTitle.Text = (editMode ? "Редактирование строки — " : "Новая строка — ") + _tableName;
            _columns = columns ?? new List<ColumnInfo>();
            _recordColumn = recordColumn ?? "";
            _autoRecordValue = nextRecordValue;
            _connectionString = connectionString;
            _plcColumn = string.IsNullOrWhiteSpace(plcColumn)
                ? _columns.FirstOrDefault(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase))?.Name ?? ""
                : plcColumn;
            if (_tableName.Equals("dbo.Prog", StringComparison.OrdinalIgnoreCase))
            {
                Width = 1080;
                Height = 680;
                MinWidth = 1000;
                FieldsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                FieldsScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            GenerateFields();
            if (initialValues != null)
            {
                foreach (var pair in initialValues)
                {
                    if (_fieldInputs.TryGetValue(pair.Key, out var textBox)) textBox.Text = pair.Value == null || pair.Value == DBNull.Value ? string.Empty : Convert.ToString(pair.Value, CultureInfo.CurrentCulture) ?? string.Empty;
                    if (_lookupInputs.TryGetValue(pair.Key, out var combo)) combo.SelectedValue = pair.Value == null || pair.Value == DBNull.Value ? null : Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
            }
            Loaded += AddRowWindow_Loaded;
            Closed += (_, _) => { _recordCts?.Cancel(); _recordCheckCts?.Cancel(); };
        }

        private void GenerateFields()
        {
            FieldsPanel.Children.Clear();
            var groups = _columns.Where(c => !c.IsReadOnly)
                .GroupBy(GetFieldGroup)
                .OrderBy(g => GroupOrder(g.Key))
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var group in groups)
            {
                var groupBorder = new Border
                {
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7)
                };
                groupBorder.SetResourceReference(Border.BackgroundProperty, "BrushPanel");
                groupBorder.SetResourceReference(Border.BorderBrushProperty, "BrushBorder");

                var groupPanel = new StackPanel();
                var header = new TextBlock
                {
                    Text = group.Key,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 7)
                };
                header.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                groupPanel.Children.Add(header);

                var fieldsGrid = new UniformGrid { Columns = 3 };
                foreach (var col in group)
                {
                    var fieldPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 8) };
                    bool isKey = col.Name.Equals(_recordColumn, StringComparison.OrdinalIgnoreCase);
                    bool isRecord = isKey && col.Name.Equals("Record", StringComparison.OrdinalIgnoreCase);
                    bool isClassNumber = _forcedClassNumber.HasValue &&
                        !string.IsNullOrWhiteSpace(_classNumberColumn) &&
                        col.Name.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase);
                    string titleText = isClassNumber ? "🔒 " : (isRecord ? "↻ " : (isKey ? "🔑 " : ""));
                    titleText += $"{col.Name} ({col.DataType})";
                    if (col.IsNullable) titleText += " — nullable";
                    if (col.HasDefault) titleText += " — default";

                    var label = new TextBlock
                    { Text = titleText, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
                    label.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");

                    bool hasLookupDefinition = _lookupOptions.TryGetValue(col.Name, out var options);
                    bool hasLookupOptions = hasLookupDefinition && options.Count > 0;
                    // The class number is assigned by the selected class, not by a lookup picker.
                    bool isLookup = !isClassNumber && (hasLookupDefinition || IsKnownLookupField(col.Name));
                    options ??= new List<LookupOption>();
                    Control input;
                    if (isLookup)
                    {
                        var combo = new ComboBox { Width = 290, Height = 30, FontSize = 12, ItemsSource = options, DisplayMemberPath = nameof(LookupOption.Display), SelectedValuePath = nameof(LookupOption.Key) };
                        _lookupInputs[col.Name] = combo;
                        if (!hasLookupOptions) { combo.IsEnabled = false; combo.ToolTip = "Для связанного поля не удалось загрузить справочник. Значение нельзя вводить вручную; проверьте подключение и наличие таблицы-справочника."; }
                        input = combo;
                    }
                    else
                    {
                        input = new TextBox { Width = 290, Height = 30, Padding = new Thickness(8, 5, 8, 5), FontSize = 12 };
                    }
                    var textBox = input as TextBox;
                    if (isClassNumber && textBox != null)
                    {
                        textBox.Text = FormatClassNumberDisplay(_forcedClassNumber.Value);
                        textBox.IsReadOnly = true;
                        textBox.ToolTip = "Номер класса назначается автоматически и не редактируется для системного класса.";
                    }
                    else if (isRecord && _autoRecordValue > 0 && textBox != null)
                    {
                        textBox.Text = _autoRecordValue.ToString(CultureInfo.InvariantCulture);
                    }
                    if (textBox != null)
                        textBox.ToolTip = isClassNumber
                            ? "Номер класса назначается автоматически."
                            : isRecord
                            ? (_preferFreeScopedRecord ? "Предлагается первый пропуск в диапазоне Record выбранного PLC. Если пропусков нет, предлагается следующий номер. Значение можно изменить вручную."
                                : !string.IsNullOrWhiteSpace(_plcColumn) ? "Предложение: последний Record для выбранного PLC + 1. Значение можно изменить вручную." : "Предложение: последний Record + 1. Значение можно изменить вручную.")
                            : (col.HasDefault ? "Пусто → значение по умолчанию" : col.IsNullable ? "Пусто → NULL" : "Обязательное поле");

                    fieldPanel.Children.Add(label);
                    fieldPanel.Children.Add(input);
                    if (textBox != null) _fieldInputs[col.Name] = textBox;
                    if (isRecord && textBox != null) textBox.TextChanged += (_, _) => CheckRecord();
                    if (isLookup && input is ComboBox lookupBox) lookupBox.SelectedIndex = options.FindIndex(x => x.Key == (isClassNumber ? _forcedClassNumber?.ToString(CultureInfo.InvariantCulture) : ""));
                    if (!string.IsNullOrWhiteSpace(_plcColumn) && col.Name.Equals(_plcColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        if (textBox != null) textBox.TextChanged += PlcTextBox_TextChanged;
                        if (input is ComboBox plcCombo) plcCombo.SelectionChanged += PlcCombo_SelectionChanged;
                    }
                    fieldsGrid.Children.Add(fieldPanel);
                }
                groupPanel.Children.Add(fieldsGrid);
                groupBorder.Child = groupPanel;
                FieldsPanel.Children.Add(groupBorder);
            }
        }

        private static string FormatClassNumberDisplay(int classNumber)
        {
            var className = Configurator.Core.Architecture.SystemClassCatalog.CreateDefaults()
                .FirstOrDefault(item => item.ClassNumber == classNumber)?.Name;
            return string.IsNullOrWhiteSpace(className)
                ? classNumber.ToString(CultureInfo.InvariantCulture)
                : $"{classNumber} · {className}";
        }

        private static bool IsKnownLookupField(string name)
        {
            var n = (name ?? "").Replace("_", "").Replace(" ", "");
            return new[] { "Type", "PLC", "Area", "PLCClassNumber", "PLCClasssNumber", "Class", "Unit" }
                .Contains(n, StringComparer.OrdinalIgnoreCase);
        }

        private static int GroupOrder(string group) => group switch
        {
            "Идентификация и контекст" => 0,
            "Наименование и описание" => 1,
            "PLC / идентификаторы" => 2,
            "Параметры" => 3,
            _ => 10
        };

        private static string GetFieldGroup(ColumnInfo col)
        {
            if (col == null) return "Основное";
            string n = col.Name.ToLowerInvariant();
            if (n is "plc" or "record" || n.Contains("class") || n == "area") return "Идентификация и контекст";
            if (n.StartsWith("name") || n.StartsWith("description") || n.StartsWith("text")) return "Наименование и описание";
            if (n.Contains("address") || n.Contains("number") || n.Contains("db_")) return "PLC / идентификаторы";
            return "Параметры";
        }

        private void AddRowWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CheckRecord();
            if (string.IsNullOrWhiteSpace(_plcColumn)) return;
            if (_fieldInputs.TryGetValue(_plcColumn, out var plcBox))
                _ = UpdateRecordFromPlcAsync(plcBox.Text, CancellationToken.None);
            else if (_lookupInputs.TryGetValue(_plcColumn, out var plcCombo) && plcCombo.SelectedValue != null)
                _ = UpdateRecordFromPlcAsync(Convert.ToString(plcCombo.SelectedValue, CultureInfo.InvariantCulture), CancellationToken.None);
        }

        private async void PlcTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            CheckRecord();
            if (_updatingAutoRecord || sender is not TextBox plcBox) return;
            await UpdateRecordFromPlcAsync(plcBox.Text, CancellationToken.None);
        }

        private async void PlcCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CheckRecord();
            if (_updatingAutoRecord || sender is not ComboBox plcCombo) return;
            await UpdateRecordFromPlcAsync((plcCombo.SelectedItem as LookupOption)?.Key, CancellationToken.None);
        }

        private async void CheckRecord()
        {
            if (_editMode || !IsLoaded) return;
            if (!_fieldInputs.TryGetValue(_recordColumn, out var recordBox)) return;
            _recordCheckCts?.Cancel();
            _recordCheckCts = new CancellationTokenSource();
            var token = _recordCheckCts.Token;
            recordBox.ClearValue(Control.BorderBrushProperty);
            string record = recordBox.Text.Trim();
            string plc = _fieldInputs.TryGetValue(_plcColumn, out var text) ? text.Text.Trim()
                : _lookupInputs.TryGetValue(_plcColumn, out var combo) ? (combo.SelectedItem as LookupOption)?.Key : null;
            if (string.IsNullOrWhiteSpace(record) || string.IsNullOrWhiteSpace(plc)) return;
            try
            {
                await Task.Delay(250, token);
                bool exists;
                if (_recordExistsProvider != null) exists = await _recordExistsProvider(plc, record, token);
                else if (!string.IsNullOrWhiteSpace(_connectionString))
                {
                    var recordColumn = _columns.First(c => c.Name.Equals(_recordColumn, StringComparison.OrdinalIgnoreCase));
                    var plcColumn = _columns.First(c => c.Name.Equals(_plcColumn, StringComparison.OrdinalIgnoreCase));
                    exists = await new DatabaseService(_connectionString).ScopedRecordExistsAsync(_tableName, _recordColumn, _plcColumn, ConvertValue(record, recordColumn), ConvertValue(plc, plcColumn), token);
                }
                else return;
                if (!token.IsCancellationRequested && exists) recordBox.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        private async Task UpdateRecordFromPlcAsync(string plcText, CancellationToken externalToken)
        {
            if (string.IsNullOrWhiteSpace(_tableName) || string.IsNullOrWhiteSpace(_recordColumn) || string.IsNullOrWhiteSpace(_plcColumn))
                return;
            if (string.IsNullOrWhiteSpace(_connectionString) && _scopedRecordProvider == null)
                return;

            if (!_fieldInputs.TryGetValue(_recordColumn, out var recordBox))
                return;

            int generation = ++_recordGeneration;
            var previous = _recordCts;
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            _recordCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var ct = _recordCts.Token;

            if (_preferFreeScopedRecord)
            {
                if (recordBox.Text == _suggestedRecordText)
                    recordBox.Clear();
                _suggestedRecordText = null;
                BtnConfirm.IsEnabled = true;
                if (!string.IsNullOrWhiteSpace(recordBox.Text)) return;
            }
            if (string.IsNullOrWhiteSpace(plcText))
                return;

            if (_preferFreeScopedRecord) BtnConfirm.IsEnabled = false;
            try
            {
                var plcColumn = _columns.FirstOrDefault(c => c.Name.Equals(_plcColumn, StringComparison.OrdinalIgnoreCase));
                if (plcColumn == null) return;

                int nextRecord;
                if (_scopedRecordProvider != null)
                {
                    nextRecord = await _scopedRecordProvider(plcText.Trim(), ct);
                }
                else
                {
                    object plcValue = ConvertValue(plcText.Trim(), plcColumn);
                    var db = new DatabaseService(_connectionString);
                    nextRecord = _preferFreeScopedRecord
                        ? await db.GetFreeScopedRecordValueAsync(_tableName, _recordColumn, _plcColumn, plcValue, ct)
                        : await db.GetNextScopedRecordValueAsync(_tableName, _recordColumn, _plcColumn, plcValue, ct);
                }
                if (generation != _recordGeneration || ct.IsCancellationRequested) return;
                if (_preferFreeScopedRecord && !string.IsNullOrWhiteSpace(recordBox.Text)) return;

                _updatingAutoRecord = true;
                recordBox.Text = nextRecord.ToString(CultureInfo.InvariantCulture);
                _suggestedRecordText = recordBox.Text;
                _updatingAutoRecord = false;
            }
            catch (OperationCanceledException)
            {
                _updatingAutoRecord = false;
            }
            catch
            {
                _updatingAutoRecord = false;
                // Некорректный PLC не блокирует ручной ввод Record.
            }
            finally
            {
                if (_preferFreeScopedRecord && generation == _recordGeneration && !ct.IsCancellationRequested)
                    BtnConfirm.IsEnabled = true;
            }
        }

        private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnConfirm.IsEnabled) return;
            if ((!string.IsNullOrWhiteSpace(_connectionString) || _scopedRecordProvider != null) &&
                !string.IsNullOrWhiteSpace(_plcColumn) && !string.IsNullOrWhiteSpace(_recordColumn) &&
                _fieldInputs.TryGetValue(_recordColumn, out var recordBox) && string.IsNullOrWhiteSpace(recordBox.Text))
            {
                string plcText = _fieldInputs.TryGetValue(_plcColumn, out var plcBox)
                    ? plcBox.Text
                    : _lookupInputs.TryGetValue(_plcColumn, out var plcCombo)
                        ? Convert.ToString(plcCombo.SelectedValue, CultureInfo.InvariantCulture) ?? ""
                        : "";
                if (!string.IsNullOrWhiteSpace(plcText))
                {
                    await UpdateRecordFromPlcAsync(plcText, CancellationToken.None);
                    if (_preferFreeScopedRecord && (!IsVisible || !BtnConfirm.IsEnabled)) return;
                    if (_preferFreeScopedRecord && string.IsNullOrWhiteSpace(recordBox.Text))
                    {
                        Configurator.Infrastructure.ModernDialog.Show("Не удалось подобрать свободный Record. Укажите значение вручную.");
                        return;
                    }
                }
            }

            Values = new Dictionary<string, object>();
            var errors = new List<string>();
            bool hasUserValue = false;

            foreach (var colInfo in _columns.Where(c => !c.IsReadOnly))
            {
                string fieldName = colInfo.Name;
                string text = _lookupInputs.TryGetValue(fieldName, out var lookupBox)
                    ? lookupBox.SelectedValue?.ToString() ?? ""
                    : _fieldInputs.TryGetValue(fieldName, out var textBoxValue) ? textBoxValue.Text.Trim() : "";
                if (colInfo == null || colInfo.IsReadOnly)
                    continue;

                if (_forcedClassNumber.HasValue && !string.IsNullOrWhiteSpace(_classNumberColumn) &&
                    fieldName.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool generatedField = fieldName.Equals(_recordColumn, StringComparison.OrdinalIgnoreCase);
                if (!generatedField && !string.IsNullOrEmpty(text))
                    hasUserValue = true;

                if (string.IsNullOrEmpty(text))
                {
                    if (colInfo.HasDefault)
                        continue;
                    if (colInfo.IsNullable)
                    {
                        Values[fieldName] = DBNull.Value;
                        continue;
                    }
                    if (!colInfo.IsIdentity)
                        errors.Add($"Поле \"{fieldName}\" обязательно для заполнения");
                    continue;
                }

                try
                {
                    // Проверяем ограничения SQL Server до INSERT, чтобы вместо общей
                    // ошибки truncation пользователь видел конкретное поле и лимит.
                    if (colInfo.MaxLength > 0 && IsStringColumn(colInfo.DataType))
                    {
                        int maxChars = colInfo.MaxLength;
                        if (colInfo.DataType.Equals("nchar", StringComparison.OrdinalIgnoreCase) ||
                            colInfo.DataType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase))
                            maxChars = colInfo.MaxLength / 2;
                        if (text.Length > maxChars)
                        {
                            errors.Add($"Поле \"{fieldName}\": введено {text.Length} символов, максимально допустимо {maxChars}.");
                            continue;
                        }
                    }
                    Values[fieldName] = ConvertValue(text, colInfo);
                }
                catch (Exception ex)
                {
                    errors.Add($"Поле \"{fieldName}\": {ex.Message}");
                }
            }

            if (_forcedClassNumber.HasValue && !string.IsNullOrWhiteSpace(_classNumberColumn))
                Values[_classNumberColumn] = _forcedClassNumber.Value;

            if (errors.Count > 0)
            {
                Configurator.Infrastructure.ModernDialog.Show(string.Join("\n", errors), "Ошибка ввода",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Values = null;
                return;
            }

            // Полностью пустую строку не создаём. Это особенно важно для
            // таблиц без ключа, где SQL Server может технически разрешить
            // INSERT всех NULL. Предзаполненный Record считается данными.
            bool hasAutoGeneratedValue = _fieldInputs.Values.Any(t => t.IsReadOnly && !string.IsNullOrWhiteSpace(t.Text));
            if (!hasUserValue && !hasAutoGeneratedValue)
            {
                Configurator.Infrastructure.ModernDialog.Show("Нельзя добавить полностью пустую строку. Заполните хотя бы одно поле.", "Нет данных",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Confirmed = true;
            Close();
        }

        private static bool IsStringColumn(string type) =>
            type != null && new[] { "char", "varchar", "nchar", "nvarchar" }.Contains(type, StringComparer.OrdinalIgnoreCase);

        private static object ConvertValue(string text, ColumnInfo col)
        {
            var dt = col.DataType.ToLower();

            if (dt == "int")
                return int.Parse(text);
            if (dt == "bigint")
                return long.Parse(text);
            if (dt == "smallint")
                return short.Parse(text);
            if (dt == "tinyint")
                return byte.Parse(text);

            if (dt == "decimal" || dt == "numeric" || dt == "money" || dt == "smallmoney")
                return decimal.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "float")
                return double.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "real")
                return float.Parse(text, CultureInfo.InvariantCulture);

            if (dt == "date" || dt == "datetime" || dt == "datetime2" || dt == "smalldatetime")
                return DateTime.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "datetimeoffset")
                return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
            if (dt == "time")
                return TimeSpan.Parse(text, CultureInfo.InvariantCulture);

            if (dt == "bit")
            {
                if (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("да", StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }

            if (dt == "uniqueidentifier")
                return Guid.Parse(text);

            return text;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                BtnConfirm_Click(sender, e);
            else if (e.Key == Key.Escape)
                Close();
        }
    }
}
