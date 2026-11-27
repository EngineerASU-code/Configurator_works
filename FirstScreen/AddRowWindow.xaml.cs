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
        private readonly Func<string, string, CancellationToken, Task<Dictionary<string, List<LookupOption>>>> _freeAddressProvider;
        private Dictionary<string, List<LookupOption>> _addresses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBlock> _hints = new(StringComparer.OrdinalIgnoreCase);
        private readonly int? _forcedClassNumber;
        private readonly string _recordScopeClassColumn;
        private readonly string _classNumberColumn;
        private readonly Func<string, Task<List<LookupOption>>> _msTypes;
        private readonly Dictionary<string, List<LookupOption>> _lookupOptions;
        private readonly Func<string, string, CancellationToken, Task<int>> _scopedRecordProvider;
        private readonly Func<string, string, string, CancellationToken, Task<bool>> _recordExistsProvider;
        private readonly bool _editMode;
        private readonly bool _preferFreeScopedRecord;
        private CancellationTokenSource _recordCheckCts;
        private readonly Dictionary<string, ComboBox> _lookupInputs = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object> Values { get; private set; }
        internal Func<bool> CanApply { get; set; }
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
            Func<string, string, CancellationToken, Task<int>> scopedRecordProvider = null,
            Func<string, string, string, CancellationToken, Task<bool>> recordExistsProvider = null,
            bool preferFreeScopedRecord = false,
            string recordScopeClassColumn = null,
            Func<string, string, CancellationToken, Task<Dictionary<string, List<LookupOption>>>> freeAddressProvider = null,
            Func<string, Task<List<LookupOption>>> msTypes = null)
        {
            InitializeComponent();
            _forcedClassNumber = forcedClassNumber;
            _recordScopeClassColumn = recordScopeClassColumn;
            _classNumberColumn = classNumberColumn ?? "";
            _msTypes = msTypes;
            _lookupOptions = lookupOptions ?? new Dictionary<string, List<LookupOption>>(StringComparer.OrdinalIgnoreCase);
            _scopedRecordProvider = scopedRecordProvider;
            _recordExistsProvider = recordExistsProvider;
            _editMode = editMode;
            _freeAddressProvider = freeAddressProvider;
            _preferFreeScopedRecord = preferFreeScopedRecord;
            _tableName = tableName ?? "";
            WindowTitle.Text = (editMode ? "Редактирование строки — " : "Новая строка — ") + _tableName;
            _columns = columns?.Where(c => !MainWindow.HideLanguageField(c.Name)).ToList() ?? new List<ColumnInfo>();
            _recordColumn = recordColumn ?? "";
            _autoRecordValue = nextRecordValue;
            _connectionString = connectionString;
            _plcColumn = string.IsNullOrWhiteSpace(plcColumn)
                ? _columns.FirstOrDefault(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase))?.Name ?? ""
                : plcColumn;
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
            GenerateFields();
            FieldArea.SizeChanged += (_, _) => ArrangeFields();
            if (initialValues != null)
            {
                foreach (var pair in initialValues)
                {
                    if (_fieldInputs.TryGetValue(pair.Key, out var textBox)) textBox.Text = pair.Value == null || pair.Value == DBNull.Value ? string.Empty : Convert.ToString(pair.Value, CultureInfo.CurrentCulture) ?? string.Empty;
                    if (_lookupInputs.TryGetValue(pair.Key, out var combo)) combo.SelectedValue = pair.Value == null || pair.Value == DBNull.Value ? null : Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
            }
            if (DerivesAreaName && _fieldInputs.TryGetValue("AreaName", out var area) &&
                _fieldInputs.TryGetValue("Record", out var record))
            {
                var hint = _hints.GetValueOrDefault("Record");
                void UpdateName()
                {
                    var value = string.IsNullOrWhiteSpace(record.Text) ? hint?.Text : record.Text;
                    area.Text = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                        ? Configurator.Core.Workspace.ScopedRecordSuggestion.AreaNameForRecord(number) : "";
                }
                record.TextChanged += (_, _) => UpdateName();
                if (hint != null)
                {
                    var property = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
                    EventHandler changed = (_, _) => UpdateName();
                    property.AddValueChanged(hint, changed);
                    Closed += (_, _) => property.RemoveValueChanged(hint, changed);
                }
                UpdateName();
            }
            Loaded += AddRowWindow_Loaded;
            Closed += (_, _) => { _recordCts?.Cancel(); _recordCheckCts?.Cancel(); };
        }

        private bool DerivesAreaName => !_editMode && _recordColumn.Equals("Record", StringComparison.OrdinalIgnoreCase)
            && Configurator.Core.Workspace.ScopedRecordSuggestion.IsAreasTable(_tableName)
            && _columns.Any(c => !c.IsReadOnly && c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));

        private int _page;
        private void PreviousPage_Click(object sender, RoutedEventArgs e) { _page--; ArrangeFields(); }
        private void NextPage_Click(object sender, RoutedEventArgs e) { _page++; ArrangeFields(); }

        private void ArrangeFields()
        {
            if (FieldArea.ActualHeight <= 0 || FieldArea.ActualWidth <= 0) return;
            int maxColumns = Math.Clamp((int)(FieldArea.ActualWidth / 195), 2, 6);
            int page = 0;
            double used = 0;
            var pages = new List<(Border Card, UIElement Field, int Page)>();
            foreach (Border card in FieldsPanel.Children)
            {
                var stack = (StackPanel)card.Child;
                var header = (TextBlock)stack.Children[0];
                var grid = (UniformGrid)stack.Children[1];
                grid.Columns = Math.Min(maxColumns, grid.Children.Count);
                header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double overhead = header.DesiredSize.Height + card.Padding.Top + card.Padding.Bottom +
                    card.BorderThickness.Top + card.BorderThickness.Bottom + card.Margin.Bottom;
                for (int i = 0; i < grid.Children.Count; i += grid.Columns)
                {
                    double rowHeight = 0;
                    for (int j = i; j < Math.Min(i + grid.Columns, grid.Children.Count); j++)
                    {
                        var field = (FrameworkElement)grid.Children[j];
                        field.Visibility = Visibility.Visible;
                        field.Measure(new Size((FieldArea.ActualWidth - 24) / grid.Columns, double.PositiveInfinity));
                        rowHeight = Math.Max(rowHeight, field.DesiredSize.Height);
                    }
                    double needed = rowHeight + (i == 0 ? overhead : 0);
                    if (used > 0 && used + needed > FieldArea.ActualHeight)
                    {
                        page++;
                        used = 0;
                        needed = rowHeight + overhead;
                    }
                    used += needed;
                    for (int j = i; j < Math.Min(i + grid.Columns, grid.Children.Count); j++)
                        pages.Add((card, grid.Children[j], page));
                }
            }
            _page = Math.Clamp(_page, 0, page);
            foreach (var group in pages.GroupBy(entry => entry.Card))
            {
                group.Key.Visibility = group.Any(entry => entry.Page == _page) ? Visibility.Visible : Visibility.Collapsed;
                foreach (var entry in group)
                    entry.Field.Visibility = entry.Page == _page ? Visibility.Visible : Visibility.Collapsed;
            }
            PageControls.Visibility = page > 0 ? Visibility.Visible : Visibility.Collapsed;
            PageNumber.Text = $"{_page + 1} / {page + 1}";
            PreviousPage.IsEnabled = _page > 0;
            NextPage.IsEnabled = _page < page;
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
                    Margin = new Thickness(0, 0, 0, 5),
                    Padding = new Thickness(10, 5, 10, 5),
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
                    Margin = new Thickness(0, 0, 0, 4)
                };
                header.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                groupPanel.Children.Add(header);

                var fieldsGrid = new UniformGrid { Columns = Math.Min(4, group.Count()) };
                foreach (var col in group.OrderBy(c => InputPlaceholder.FieldOrder(c.Name)))
                {
                    var fieldPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
                    bool isKey = col.Name.Equals(_recordColumn, StringComparison.OrdinalIgnoreCase);
                    bool isRecord = isKey && col.Name.Equals("Record", StringComparison.OrdinalIgnoreCase);
                    bool isClassNumber = _forcedClassNumber.HasValue &&
                        !string.IsNullOrWhiteSpace(_classNumberColumn) &&
                        col.Name.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase);
                    bool isAreaName = DerivesAreaName && col.Name.Equals("AreaName", StringComparison.OrdinalIgnoreCase);
                    string titleText = isClassNumber || isAreaName ? "🔒 " : (isRecord ? "↻ " : (isKey ? "🔑 " : ""));
                    titleText += $"{col.Name} ({col.DataType})";

                    if (col.HasDefault) titleText += " — default";

                    var label = new TextBlock
                    { Text = titleText, ToolTip = titleText, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
                    label.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");

                    bool hasLookupDefinition = _lookupOptions.TryGetValue(col.Name, out var options);
                    bool hasLookupOptions = hasLookupDefinition && options.Count > 0;
                    // The class number is assigned by the selected class, not by a lookup picker.
                    bool isLookup = !MsTypeCatalog.IsField(col.Name) && !isAreaName && !isClassNumber && (hasLookupDefinition || IsKnownLookupField(col.Name));
                    options ??= new List<LookupOption>();
                    Control input;
                    if (isLookup)
                    {
                        var combo = new ComboBox { Height = 30, FontSize = 13, ItemsSource = options, DisplayMemberPath = nameof(LookupOption.Display), SelectedValuePath = nameof(LookupOption.Key) };
                        _lookupInputs[col.Name] = combo;
                        if (!hasLookupOptions) { combo.IsEnabled = false; combo.ToolTip = "Для связанного поля не удалось загрузить справочник. Значение нельзя вводить вручную; проверьте подключение и наличие таблицы-справочника."; }
                        input = combo;
                    }
                    else
                    {
                        input = new TextBox { Height = 30, Padding = new Thickness(8, 5, 8, 5), FontSize = 13 };
                    }
                    var textBox = input as TextBox;
                    if (isClassNumber && textBox != null)
                    {
                        textBox.Text = FormatClassNumberDisplay(_forcedClassNumber.Value);
                        textBox.IsReadOnly = true;
                        textBox.ToolTip = "Номер класса назначается автоматически и не редактируется для системного класса.";
                    }
                    if (textBox != null)
                        textBox.ToolTip = isClassNumber
                            ? "Номер класса назначается автоматически."
                            : isRecord
                            ? (_preferFreeScopedRecord ? "Предлагается первый пропуск в диапазоне Record выбранного PLC и класса. Если пропусков нет, предлагается следующий номер. Значение можно изменить вручную."
                                : !string.IsNullOrWhiteSpace(_plcColumn) ? "Предложение: последний Record для выбранного PLC + 1. Значение можно изменить вручную." : "Предложение: последний Record + 1. Значение можно изменить вручную.")
                            : (col.HasDefault ? "Пусто → значение по умолчанию" : col.IsNullable ? "Пусто → NULL" : "Обязательное поле");

                    if (isAreaName)
                    {
                        textBox.IsReadOnly = true;
                        textBox.ToolTip = "Название формируется из Record, например 6 → 006.";
                    }
                    fieldPanel.Children.Add(label);
                    if (!_editMode && textBox != null && (isRecord || PlcAddressCatalog.IsSuggestedField(col.Name)))
                    {
                        var hint = new TextBlock { Text = "—" };
                        _hints[col.Name] = hint;
                        if (isRecord && _autoRecordValue > 0)
                            hint.Text = _autoRecordValue.ToString(CultureInfo.InvariantCulture);
                        var editor = InputPlaceholder.Wrap(textBox, hint);
                        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
                        fieldPanel.Children.Add(isRecord ? editor : InputPlaceholder.WithAddressList(editor, textBox,
                            () => Task.FromResult(_addresses.GetValueOrDefault(col.Name))));
                    }
                    else if (textBox != null && InputPlaceholder.IsFlag(col))
                    {
                        fieldPanel.Children.Add(InputPlaceholder.Flag(col, textBox));
                    }
                    else if (textBox != null)
                    {
                        var editor = InputPlaceholder.WrapField(textBox, col);
                        if (_msTypes != null && MsTypeCatalog.IsField(col.Name))
                        {
                            var scope = ProjectClassCatalog.FindClassColumn(_columns.Select(c => c.Name));
                            fieldPanel.Children.Add(InputPlaceholder.WithAddressList(editor, textBox, () =>
                                _msTypes(_forcedClassNumber?.ToString() ?? (scope == null ? null : ReadInput(scope))), true));
                        }
                        else fieldPanel.Children.Add(editor);
                    }
                    else if (input is ComboBox picker)
                    {
                        fieldPanel.Children.Add(InputPlaceholder.Lookup(picker));
                    }
                    else fieldPanel.Children.Add(input);
                    if (textBox != null) _fieldInputs[col.Name] = textBox;
                    if (isRecord && textBox != null) textBox.TextChanged += (_, _) => CheckRecord();
                    if (isLookup && input is ComboBox lookupBox) lookupBox.SelectedIndex = options.FindIndex(x => x.Key == (isClassNumber ? _forcedClassNumber?.ToString(CultureInfo.InvariantCulture) : ""));
                    if (col.Name.Equals(_plcColumn, StringComparison.OrdinalIgnoreCase) ||
                        !isClassNumber && (col.Name.Equals(_recordScopeClassColumn, StringComparison.OrdinalIgnoreCase) ||
                        col.Name.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (textBox != null) textBox.TextChanged += (_, _) => RecordScopeChanged();
                        if (input is ComboBox scopeCombo) scopeCombo.SelectionChanged += (_, _) => RecordScopeChanged();
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

        private void AddRowWindow_Loaded(object sender, RoutedEventArgs e) => RecordScopeChanged();

        private void RecordScopeChanged()
        {
            if (_editMode || !IsLoaded) return;
            CheckRecord();
            _ = UpdateRecordFromPlcAsync(ReadInput(_plcColumn), CancellationToken.None);
        }

        private string ReadInput(string column) =>
            _fieldInputs.TryGetValue(column, out var text) ? text.Text.Trim()
                : _lookupInputs.TryGetValue(column, out var combo) ? (combo.SelectedItem as LookupOption)?.Key : null;

        private string ReadRecordScopeClass()
        {
            if (_recordScopeClassColumn == null) return null;
            if (_forcedClassNumber.HasValue && _recordScopeClassColumn.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase))
                return _forcedClassNumber.Value.ToString(CultureInfo.InvariantCulture);
            return ReadInput(_recordScopeClassColumn);
        }

        private object ConvertRecordScopeClass(string value) => _recordScopeClassColumn == null ? null :
            ConvertValue(value, _columns.First(c => c.Name.Equals(_recordScopeClassColumn, StringComparison.OrdinalIgnoreCase)));

        private async void CheckRecord()
        {
            if (_editMode || !IsLoaded) return;
            if (!_fieldInputs.TryGetValue(_recordColumn, out var recordBox)) return;
            _recordCheckCts?.Cancel();
            _recordCheckCts = new CancellationTokenSource();
            var token = _recordCheckCts.Token;
            recordBox.ClearValue(Control.BorderBrushProperty);
            string record = recordBox.Text.Trim();
            string plc = ReadInput(_plcColumn);
            string classValue = ReadRecordScopeClass();
            if (string.IsNullOrWhiteSpace(record) || string.IsNullOrWhiteSpace(plc) ||
                _recordScopeClassColumn != null && string.IsNullOrWhiteSpace(classValue)) return;
            try
            {
                await Task.Delay(250, token);
                bool exists;
                if (_recordExistsProvider != null) exists = await _recordExistsProvider(plc, classValue, record, token);
                else if (!string.IsNullOrWhiteSpace(_connectionString))
                {
                    var recordColumn = _columns.First(c => c.Name.Equals(_recordColumn, StringComparison.OrdinalIgnoreCase));
                    var plcColumn = _columns.First(c => c.Name.Equals(_plcColumn, StringComparison.OrdinalIgnoreCase));
                    exists = await new DatabaseService(_connectionString).ScopedRecordExistsAsync(_tableName, _recordColumn, _plcColumn, ConvertValue(record, recordColumn), ConvertValue(plc, plcColumn), token, _recordScopeClassColumn, ConvertRecordScopeClass(classValue));
                }
                else return;
                if (!token.IsCancellationRequested && exists) recordBox.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        private async Task UpdateRecordFromPlcAsync(string plcText, CancellationToken externalToken)
        {
            if (string.IsNullOrWhiteSpace(_plcColumn)) return;
            int generation = ++_recordGeneration;
            _recordCts?.Cancel();
            _recordCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var ct = _recordCts.Token;
            _hints.TryGetValue(_recordColumn, out var recordHint);
            var addressHints = _hints.Where(pair => PlcAddressCatalog.IsSuggestedField(pair.Key)).ToList();
            _addresses.Clear();
            bool needsPlc = string.IsNullOrWhiteSpace(plcText);
            if (recordHint != null) recordHint.Text = needsPlc ? "PLC" : "—";
            foreach (var hint in addressHints) hint.Value.Text = needsPlc && _freeAddressProvider != null ? "PLC" : "—";
            BtnConfirm.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(plcText)) return;
            string classValue = ReadRecordScopeClass();
            BtnConfirm.IsEnabled = false;
            try
            {
                if (recordHint != null && (_recordScopeClassColumn == null || !string.IsNullOrWhiteSpace(classValue)))
                {
                    try
                    {
                        int? next = null;
                        if (_scopedRecordProvider != null)
                            next = await _scopedRecordProvider(plcText, classValue, ct);
                        else if (!string.IsNullOrWhiteSpace(_connectionString))
                        {
                            var plc = ConvertValue(plcText, _columns.First(c => c.Name.Equals(_plcColumn, StringComparison.OrdinalIgnoreCase)));
                            var db = new DatabaseService(_connectionString);
                            next = _preferFreeScopedRecord
                                ? await db.GetFreeScopedRecordValueAsync(_tableName, _recordColumn, _plcColumn, plc, ct, _recordScopeClassColumn, ConvertRecordScopeClass(classValue))
                                : await db.GetNextScopedRecordValueAsync(_tableName, _recordColumn, _plcColumn, plc, ct);
                        }
                        if (!ct.IsCancellationRequested && next.HasValue) recordHint.Text = next.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { System.Diagnostics.Debug.WriteLine(ex); }
                }
                if (addressHints.Count > 0 && _freeAddressProvider != null && !ct.IsCancellationRequested)
                {
                    var addressClass = _forcedClassNumber?.ToString(CultureInfo.InvariantCulture) ?? classValue ?? ReadInput(_classNumberColumn);
                    var suggestions = await _freeAddressProvider(plcText, addressClass, ct);
                    if (!ct.IsCancellationRequested)
                    {
                        _addresses = suggestions;
                        foreach (var hint in addressHints)
                            if (suggestions.TryGetValue(hint.Key, out var options) && options.Count > 0)
                                hint.Value.Text = options[0].Key;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            finally
            {
                if (generation == _recordGeneration && !ct.IsCancellationRequested) BtnConfirm.IsEnabled = true;
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnConfirm.IsEnabled) return;
            Values = new Dictionary<string, object>();
            var errors = new List<string>();
            bool hasUserValue = false;

            foreach (var colInfo in _columns.Where(c => !c.IsReadOnly))
            {
                string fieldName = colInfo.Name;
                string text = _lookupInputs.TryGetValue(fieldName, out var lookupBox)
                    ? lookupBox.SelectedValue?.ToString() ?? ""
                    : _fieldInputs.TryGetValue(fieldName, out var textBoxValue) ? InputPlaceholder.FieldValue(textBoxValue).Trim() : "";
                if (colInfo == null || colInfo.IsReadOnly)
                    continue;

                if (_forcedClassNumber.HasValue && !string.IsNullOrWhiteSpace(_classNumberColumn) &&
                    fieldName.Equals(_classNumberColumn, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool generatedField = fieldName.Equals(_recordColumn, StringComparison.OrdinalIgnoreCase);
                if (!generatedField && !string.IsNullOrEmpty(text))
                    hasUserValue = true;

                if (string.IsNullOrEmpty(text) && _hints.TryGetValue(fieldName, out var hint) &&
                    (int.TryParse(hint.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
                     PlcAddressCatalog.IsSuggestedField(fieldName) && Configurator.Core.Workspace.PlcAddressMap.IsSingleAddress(hint.Text)))
                    text = hint.Text;
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

            if (CanApply != null && !CanApply())
            {
                Infrastructure.ModernDialog.Show(this, "Исходная таблица или запись изменилась. Вернитесь к ней или откройте редактор заново.", "Сохранение строки", MessageBoxButton.OK, MessageBoxImage.Information);
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
