using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Configurator;

public partial class ProjectModeView
{
    private readonly List<string> _lookupWarnings = new();
    private readonly HashSet<string> _lookupWarningFields = new(StringComparer.OrdinalIgnoreCase);

    private async Task RenderConfigurationObjectDetailsAsync(DataRow row, int generation)
    {
        ObjectEditor.Children.Clear();
        ObjectEditor.RowDefinitions.Clear();
        ObjectEditor.ColumnDefinitions.Clear();
        ObjectEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (row == null) return;
        _lookupWarnings.Clear();
        _lookupWarningFields.Clear();

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        IReadOnlyList<LookupOption> plcTypeOptions = Array.Empty<LookupOption>();
        if (_configurationKind == ConfigurationKind.Plc && row.Table.Columns.Contains("Type"))
        {
            try { plcTypeOptions = await LoadConfigurationPlcTypeOptionsAsync(); }
            catch { plcTypeOptions = Array.Empty<LookupOption>(); }
            if (generation != _objectDetailsGeneration || !ReferenceEquals(_selectedRow, row)) return;
        }
        int index = 0;
        foreach (DataColumn column in row.Table.Columns)
        {
            int rowIndex = index / 2;
            int columnIndex = index % 2;
            while (content.RowDefinitions.Count <= rowIndex)
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var field = new Grid { Margin = new Thickness(columnIndex == 0 ? 0 : 7, 0, columnIndex == 0 ? 7 : 0, 7) };
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = column.ColumnName,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                ToolTip = $"{column.ColumnName} · тип: {column.DataType.Name}"
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            field.Children.Add(label);

            FrameworkElement value;
            var currentValue = row.IsNull(column) ? "" : Convert.ToString(row[column]) ?? "";
            if (_configurationKind == ConfigurationKind.Plc && column.ColumnName.Equals("Type", StringComparison.OrdinalIgnoreCase)
                && CanUseTypeLookup(plcTypeOptions, currentValue))
            {
                var options = plcTypeOptions.ToList();
                if (column.AllowDBNull && !options.Any(option => string.IsNullOrEmpty(option.Key)))
                    options.Insert(0, new LookupOption { Key = "", Display = "(не задано)" });
                var combo = new ComboBox
                {
                    ItemsSource = options,
                    DisplayMemberPath = nameof(LookupOption.Display),
                    SelectedValuePath = nameof(LookupOption.Key),
                    SelectedValue = currentValue,
                    Height = 30,
                    Padding = new Thickness(6, 2, 6, 2),
                    IsEnabled = UserAuthorizationService.Has(Permission.ManageObjects),
                    Tag = column.ColumnName,
                    ToolTip = $"Type · справочник PLC ({plcTypeOptions.Count} знач.)"
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (!ReferenceEquals(_selectedRow, row)) return;
                    BtnSave.IsEnabled = UserAuthorizationService.Has(Permission.ManageObjects);
                    UpdateConfigurationTypePreview(row);
                };
                value = combo;
            }
            else
            {
                if (_configurationKind == ConfigurationKind.Plc && column.ColumnName.Equals("Type", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(currentValue))
                    AddLookupWarning(column.ColumnName, plcTypeOptions.Count == 0
                        ? $"Type={currentValue}: справочник PLC Type отсутствует или не содержит значений."
                        : $"Type={currentValue}: этого значения нет в справочнике PLC Type.");
                var box = new TextBox
                {
                    Text = currentValue,
                    Height = 30,
                    Padding = new Thickness(7, 3, 7, 3),
                    IsReadOnly = IsConfigurationIdentityField(column.ColumnName)
                        || !UserAuthorizationService.Has(Permission.ManageObjects),
                    IsTabStop = !IsConfigurationIdentityField(column.ColumnName)
                        && UserAuthorizationService.Has(Permission.ManageObjects),
                    Tag = column.ColumnName,
                    ToolTip = IsConfigurationIdentityField(column.ColumnName)
                        ? $"{column.ColumnName} · ключевое поле, изменение отключено"
                        : column.ColumnName.Equals("Type", StringComparison.OrdinalIgnoreCase)
                            ? "Справочник PLC Type не найден. Введите ключ вручную или проверьте источник справочника."
                            : $"{column.ColumnName} · тип: {column.DataType.Name}"
                };
                box.TextChanged += (_, _) =>
                {
                    if ((_configurationKind is ConfigurationKind.Plc or ConfigurationKind.Area)
                        && ReferenceEquals(_selectedRow, row))
                        BtnSave.IsEnabled = UserAuthorizationService.Has(Permission.ManageObjects);
                    if (_configurationKind == ConfigurationKind.Plc && ReferenceEquals(_selectedRow, row))
                        UpdateConfigurationTypePreview(row);
                };
                value = box;
            }
            value.SetResourceReference(Control.BackgroundProperty, "BrushPanel");
            value.SetResourceReference(Control.ForegroundProperty, "BrushText");
            value.SetResourceReference(Control.BorderBrushProperty, "BrushBorder");
            Grid.SetColumn(value, 1);
            field.Children.Add(value);
            Grid.SetColumn(field, columnIndex);
            Grid.SetRow(field, rowIndex);
            content.Children.Add(field);
            index++;
        }

        var section = new StackPanel();
        var heading = new TextBlock
        {
            Text = $"Основные данные · {index} полей",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 9)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");
        section.Children.Add(heading);
        section.Children.Add(content);
        var card = new Border
        {
            Margin = new Thickness(0, 0, 0, 9),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = section
        };
        card.SetResourceReference(Border.BackgroundProperty, "BrushBase");
        card.SetResourceReference(Border.BorderBrushProperty, "BrushBorder");
        ObjectEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(card, 0);
        ObjectEditor.Children.Add(card);
        if (_configurationKind == ConfigurationKind.Plc)
            UpdateConfigurationTypePreview(row);
        UpdateLookupWarningsButton();
    }

    private bool IsConfigurationIdentityField(string fieldName) =>
        fieldName.Equals(_configurationKeyColumn, StringComparison.OrdinalIgnoreCase)
        || fieldName.Equals("Record", StringComparison.OrdinalIgnoreCase)
        || (_configurationKind == ConfigurationKind.Area && fieldName.Equals("AreaName", StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<LookupOption>> LoadConfigurationPlcTypeOptionsAsync()
    {
        if (_offlineProject != null)
        {
            var lookupField = new Core.Architecture.FieldDefinition
            {
                Name = "Type",
                LookupTable = "dbo.PLCType",
                LookupKeyField = "Record",
                LookupDisplayField = "Name"
            };
            return GetOfflineLookupOptions(lookupField, "PLC", _configurationTableName);
        }

        if (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase)) return Array.Empty<LookupOption>();
        var databaseKey = _connection.ToConnectionString(_selectedDatabase);
        if (_onlineLookupOptionsService == null || !_onlineLookupDatabaseKey.Equals(databaseKey, StringComparison.Ordinal))
        {
            _onlineLookupOptionsService = new LookupOptionsService(new DatabaseService(databaseKey));
            _onlineLookupDatabaseKey = databaseKey;
        }
        return await _onlineLookupOptionsService.TryGetAsync("PLC", _configurationTableName, "Type", default,
            "dbo.PLCType", "Record", "Name");
    }

    private async void RenderLookupObjectDetailsAsync()
    {
        int generation = ++_objectDetailsGeneration;
        var selectedRow = _selectedRow;
        var selectedClass = _selectedClass;
        if (selectedRow == null) return;
        _lookupWarnings.Clear();
        _lookupWarningFields.Clear();
        UpdateLookupWarningsButton();
        if (_configurationKind is ConfigurationKind.Plc or ConfigurationKind.Area)
        {
            await RenderConfigurationObjectDetailsAsync(selectedRow, generation);
            return;
        }
        if (selectedClass == null) return;
        bool IsCurrentRender() => generation == _objectDetailsGeneration
            && ReferenceEquals(_selectedRow, selectedRow)
            && ReferenceEquals(_selectedClass, selectedClass)
            && _configurationKind == ConfigurationKind.Class;
        bool offline = _offlineProject != null;
        if (!offline && (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))) return;

        // One continuous editor column: every field remains visible and there are
        // no empty alternating group columns on wide screens.
        ObjectEditor.Children.Clear();
        ObjectEditor.RowDefinitions.Clear();
        ObjectEditor.ColumnDefinitions.Clear();
        ObjectEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        TxtObjectName.Text = PickName(_selectedRow, _selectedClass);
        // PLC / Area / Record are displayed once in TxtObjectContext below.
        // Keep this line for the object's class only to avoid duplicated metadata.
        TxtObjectMeta.Text = $"Класс: {_selectedClass.Name}";
        TxtPreviewGlyph.Text = _selectedClass.Name.Length > 3 ? _selectedClass.Name[..3].ToUpperInvariant() : _selectedClass.Name.ToUpperInvariant();
        TxtPreviewGlyph.Visibility = Visibility.Visible;
        ImgObjectTypePreview.Source = null;
        ImgObjectTypePreview.Visibility = Visibility.Collapsed;
        var actualFields = _selectedRow.Table.Columns.Cast<DataColumn>()
            .Select(c => _selectedClass.Fields.FirstOrDefault(f => f.Name.Equals(c.ColumnName, StringComparison.OrdinalIgnoreCase))
                         ?? new Core.Architecture.FieldDefinition { Name = c.ColumnName, DataType = c.DataType.Name, Group = "Прочее" })
            .ToList();

        var grouped = actualFields
            .Select(f => new LookupFieldItem(f, ResolveDisplayGroupForObject(f)))
            .GroupBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Groups with no fields are omitted from the preview card.
        var orderedGroups = grouped
            .Where(g => g.Value.Count > 0)
            .Select(g => new { Key = g.Key, Items = g.Value })
            .OrderBy(g => ObjectGroupOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LookupOptionsService lookups = null;
        if (!offline)
        {
            var databaseKey = (_connection?.ToConnectionString(_selectedDatabase) ?? string.Empty);
            if (_onlineLookupOptionsService == null || !_onlineLookupDatabaseKey.Equals(databaseKey, StringComparison.Ordinal))
            {
                _onlineLookupOptionsService = new LookupOptionsService(new DatabaseService(databaseKey));
                _onlineLookupDatabaseKey = databaseKey;
            }
            lookups = _onlineLookupOptionsService;
        }
        int groupRow = 0;
        foreach (var groupEntry in orderedGroups)
        {
            ObjectEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var group = groupEntry.Items;
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 9),
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            card.SetResourceReference(Border.BackgroundProperty, "BrushBase");
            card.SetResourceReference(Border.BorderBrushProperty, "BrushBorder");
            card.BorderThickness = new Thickness(1);

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var conditionSlots = _selectedClass.Name.Equals("Step", StringComparison.OrdinalIgnoreCase) &&
                groupEntry.Key.Equals("Conditions", StringComparison.OrdinalIgnoreCase)
                ? GetStepConditionSlots(group.Select(item => item.Field.Name)) : new Dictionary<string, int>();
            int index = conditionSlots.Count == 0 ? 0 : ((conditionSlots.Values.Max() / 2) + 1) * 2;
            foreach (var item in group)
            {
                int slot = conditionSlots.TryGetValue(item.Field.Name, out int pairedSlot) ? pairedSlot : index++;
                int row = slot / 2;
                int col = slot % 2;
                while (content.RowDefinitions.Count <= row) content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var field = item.Field;
                var rowPanel = new Grid { Margin = new Thickness(col == 0 ? 0 : 7, 0, col == 0 ? 7 : 0, 7) };
                rowPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
                rowPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });

                var label = new TextBlock
                {
                    Text = field.Name,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = $"{field.Name} · тип: {field.DataType}"
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                Grid.SetColumn(label, 0);
                rowPanel.Children.Add(label);

                FrameworkElement editor;
                IReadOnlyList<LookupOption> options;
                try
                {
                    // Online reads the live related table. Offline first reads that same
                    // relation from the project snapshot, then falls back to its imported
                    // text-list copy. No SQL connection is used in Offline mode.
                    options = offline
                        ? GetOfflineLookupOptions(field, _selectedClass.Name, _selectedClass.PrimaryStorage?.TableName)
                        : lookups == null
                            ? Array.Empty<LookupOption>()
                             : await lookups.TryGetAsync(
                                _selectedClass.Name,
                                _selectedClass.PrimaryStorage?.TableName,
                                field.Name,
                                default,
                                field.Name.Equals("Type", StringComparison.OrdinalIgnoreCase) && _selectedClass.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase) ? "dbo.PLCType" : field.LookupTable,
                                field.Name.Equals("Type", StringComparison.OrdinalIgnoreCase) && _selectedClass.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase) ? "Record" : field.LookupKeyField,
                                field.Name.Equals("Type", StringComparison.OrdinalIgnoreCase) && _selectedClass.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase) ? "Name" : field.LookupDisplayField);
                }
                catch (Exception ex)
                {
                    options = Array.Empty<LookupOption>();
                    WriteTypeLookupDiagnostic($"EDITOR_OPTIONS_ERROR class={_selectedClass?.Name} field={field.Name} offline={offline} error={ex}");
                }
                if (!IsCurrentRender()) return;
                if (field.Name.Contains("Type", StringComparison.OrdinalIgnoreCase))
                {
                    string currentValueForLog = GetText(field.Name);
                    WriteTypeLookupDiagnostic($"EDITOR_COMBO class={_selectedClass?.Name} sourceTable={_selectedClass?.PrimaryStorage?.TableName} offline={offline} currentKey='{currentValueForLog}' optionsCount={options.Count}");
                }
                bool knownLookupField = new[] { "Type", "PLC", "Area", "AreaName", "PLC_Class_Number", "PLCClassNumber", "PLCClasssNumber", "Class", "Unit" }
                    .Contains(field.Name, StringComparer.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(field.LookupTable) && !string.IsNullOrWhiteSpace(field.LookupKeyField));
                var comboOptions = options.ToList();
                string currentLookupValue = GetText(field.Name);
                bool isGeneratedArea = field.Name.Equals("Area", StringComparison.OrdinalIgnoreCase);
                bool isReadOnlyClassNumber = new[] { "PLC_Class_Number", "PLCClassNumber", "PLCClasssNumber" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase);
                if (knownLookupField && !isReadOnlyClassNumber && !string.IsNullOrWhiteSpace(currentLookupValue)
                    && !options.Any(option => option.Key.Equals(currentLookupValue, StringComparison.OrdinalIgnoreCase)))
                {
                    var lookupName = field.Name.Equals("Type", StringComparison.OrdinalIgnoreCase) && _selectedClass.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase)
                        ? "PLC Type"
                        : string.IsNullOrWhiteSpace(field.LookupTable) ? $"для поля {field.Name}" : field.LookupTable;
                    AddLookupWarning(field.Name, options.Count == 0
                        ? $"{field.Name}={currentLookupValue}: список «{lookupName}» отсутствует, пуст или недоступен."
                        : $"{field.Name}={currentLookupValue}: значения нет в списке «{lookupName}».");
                }
                if (field.Name.Contains("Type", StringComparison.OrdinalIgnoreCase))
                    WriteTypeLookupDiagnostic($"COMBO_PREPARED class='{_selectedClass?.Name}' currentKey='{currentLookupValue}' sourceOptions={options.Count} finalOptionsBeforeCurrent={comboOptions.Count}");
                // Area is generated/derived by the database. Show its current value as
                // read-only text; do not offer a lookup combo that could attempt an invalid write.
                // For other lookups, only add a fallback when the key is genuinely absent.
                if (!isGeneratedArea && comboOptions.Count > 0 && !comboOptions.Any(o => o.Key.Equals(currentLookupValue, StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(currentLookupValue))
                    comboOptions.Add(new LookupOption { Key = currentLookupValue, Display = $"{currentLookupValue} · текущее значение" });
                if (!isGeneratedArea && comboOptions.Count == 0 && knownLookupField && !string.IsNullOrWhiteSpace(currentLookupValue))
                    comboOptions.Add(new LookupOption { Key = currentLookupValue, Display = currentLookupValue });
                if (isGeneratedArea || isReadOnlyClassNumber)
                {
                    string readOnlyValue = currentLookupValue;
                    if (isGeneratedArea)
                    {
                        var areaOption = options.FirstOrDefault(o => o.Key.Equals(currentLookupValue, StringComparison.OrdinalIgnoreCase));
                        if (areaOption != null && !string.IsNullOrWhiteSpace(areaOption.Display) && !areaOption.Display.Equals(currentLookupValue, StringComparison.OrdinalIgnoreCase))
                            readOnlyValue = AreaDisplayFormatter.Format(currentLookupValue, AreaDisplayFormatter.ExtractName(currentLookupValue, areaOption.Display));
                    }
                    var readOnlyArea = new TextBox
                    {
                        Text = readOnlyValue,
                        Height = 30,
                        Padding = new Thickness(7, 3, 7, 3),
                        IsReadOnly = true,
                        IsTabStop = false,
                        Tag = field.Name,
                        ToolTip = isGeneratedArea
                            ? "Area формируется автоматически. Изменение отключено, чтобы избежать ошибки записи."
                            : "PLC_Class_Number задаётся конфигурацией и доступен только для чтения."
                    };
                    readOnlyArea.SetResourceReference(Control.BackgroundProperty, "BrushPanel");
                    readOnlyArea.SetResourceReference(Control.ForegroundProperty, "BrushTextSecondary");
                    readOnlyArea.SetResourceReference(Control.BorderBrushProperty, "BrushBorder");
                    editor = readOnlyArea;
                }
                else if (field.Name.Equals("Type", StringComparison.OrdinalIgnoreCase)
                    ? CanUseTypeLookup(options, currentLookupValue)
                    : options.Count > 0 || knownLookupField)
                {
                    var combo = new ComboBox
                    {
                        Height = 30,
                        Padding = new Thickness(6, 2, 6, 2),
                        Tag = field.Name,
                        ItemsSource = comboOptions,
                        DisplayMemberPath = nameof(LookupOption.Display),
                        SelectedValuePath = nameof(LookupOption.Key),
                        SelectedValue = currentLookupValue,
                        ToolTip = options.Count > 0
                            ? $"{field.Name}: значения из локального/связанного справочника ({options.Count} знач.)"
                            : $"{field.Name}: в снимке нет копии справочника; доступно текущее значение"
                    };
                    combo.SetResourceReference(Control.BackgroundProperty, "BrushPanel");
                    combo.SetResourceReference(Control.ForegroundProperty, "BrushText");
                    combo.SetResourceReference(Control.BorderBrushProperty, "BrushBorder");
                    if (field.Name.Contains("Type", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(field.LookupTable)
                        || !string.IsNullOrWhiteSpace(field.LookupKeyField))
                    {
                        combo.SelectionChanged += (_, _) =>
                        {
                            if (IsCurrentRender()) UpdateObjectTypePreviewFromEditors();
                        };
                    }
                    editor = combo;
                }
                else
                {
                    var box = new TextBox
                    {
                        Text = GetText(field.Name),
                        Height = 30,
                        Padding = new Thickness(7, 3, 7, 3),
                        Tag = field.Name,
                        TextWrapping = TextWrapping.NoWrap,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        ToolTip = $"{field.Name} · тип: {field.DataType}"
                    };
                    box.SetResourceReference(Control.BackgroundProperty, "BrushPanel");
                    box.SetResourceReference(Control.ForegroundProperty, "BrushText");
                    box.SetResourceReference(Control.BorderBrushProperty, "BrushBorder");
                    editor = box;
                }
                if (editor is TextBox typeBox
                    && (field.Name.Contains("Type", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(field.LookupTable)
                        || !string.IsNullOrWhiteSpace(field.LookupKeyField)))
                {
                    typeBox.TextChanged += (_, _) =>
                    {
                        if (IsCurrentRender()) UpdateObjectTypePreviewFromEditors();
                    };
                }
                Grid.SetColumn(editor, 1);
                rowPanel.Children.Add(editor);
                Grid.SetColumn(rowPanel, col);
                Grid.SetRow(rowPanel, row);
                content.Children.Add(rowPanel);
            }

            var section = new StackPanel();
            var heading = new TextBlock
            {
                Text = $"{groupEntry.Key} · {group.Count} полей",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 9)
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");
            section.Children.Add(heading);
            section.Children.Add(content);
            card.Child = section;
            Grid.SetColumn(card, 0);
            Grid.SetRow(card, groupRow++);
            ObjectEditor.Children.Add(card);
        }

        if (!IsCurrentRender()) return;
        UpdateObjectTypePreviewFromEditors();
        UpdateLookupWarningsButton();
        TxtStatus.Text = $"{(offline ? "Offline · " : string.Empty)}{_selectedClass.Name} · показаны все {actualFields.Count} полей · {(offline ? "значения взяты из локального снимка" : "связанные поля — выпадающие списки")}";
    }

    private static bool CanUseTypeLookup(IReadOnlyList<LookupOption> options, string currentValue) =>
        options.Count > 0 && (string.IsNullOrWhiteSpace(currentValue) ||
            options.Any(option => string.Equals(option.Key, currentValue, StringComparison.OrdinalIgnoreCase)));

    private void UpdateLookupWarningsButton()
    {
        if (BtnLookupWarnings == null) return;
        BtnLookupWarnings.Visibility = _lookupWarnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BtnLookupWarnings.Content = $"⚠  Предупреждения: {_lookupWarnings.Count}";
        BtnLookupWarnings.ToolTip = _lookupWarnings.Count == 0
            ? null
            : string.Join(Environment.NewLine, _lookupWarnings);
    }

    private void AddLookupWarning(string fieldName, string message)
    {
        _lookupWarnings.Add(message);
        if (!string.IsNullOrWhiteSpace(fieldName)) _lookupWarningFields.Add(fieldName);
    }

    private void LookupWarnings_Click(object sender, RoutedEventArgs e)
    {
        if (_lookupWarnings.Count == 0) return;
        var panel = new DockPanel { Margin = new Thickness(18) };
        var close = new Button { Content = "Закрыть", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(new ScrollViewer
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 239, 180)),
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine + Environment.NewLine, _lookupWarnings),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), LineHeight = 20, Foreground = Brushes.Black
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var dialog = new Window
        {
            Owner = Window.GetWindow(this), Title = "Предупреждения о связанных значениях",
            Width = Math.Min(820, SystemParameters.WorkArea.Width),
            Height = Math.Min(440, SystemParameters.WorkArea.Height),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        dialog.SetResourceReference(Control.BackgroundProperty, "BrushBase");
        dialog.ShowDialog();
        var fieldName = _lookupWarningFields.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(fieldName)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var editor = FindEditorByFieldName(ObjectEditor, fieldName);
            if (editor == null) return;
            editor.BringIntoView();
            if (editor is Control control && control.IsEnabled && control.Focusable) control.Focus();
        }));
    }

    private static FrameworkElement FindEditorByFieldName(DependencyObject root, string fieldName)
    {
        if (root is FrameworkElement element && element.Tag is string tag && tag.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            return element;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindEditorByFieldName(System.Windows.Media.VisualTreeHelper.GetChild(root, index), fieldName);
            if (match != null) return match;
        }
        return null;
    }

    private string ResolveDisplayGroupForObject(Core.Architecture.FieldDefinition field)
    {
        string inferred = Core.Architecture.FieldGroupCatalog.Resolve(_selectedClass.Name, field.Name);
        string configured = field.Group?.Trim();

        // Legacy schema readers often mark every SQL column as Config. Prefer the
        // engineering catalog for fields that have a known Object/Visual role.
        if (string.IsNullOrWhiteSpace(configured) ||
            (configured.Equals("Config", StringComparison.OrdinalIgnoreCase) &&
             !inferred.Equals("Config", StringComparison.OrdinalIgnoreCase)))
            return inferred;

        return configured;
    }

    private static Dictionary<string, int> GetStepConditionSlots(IEnumerable<string> fieldNames)
    {
        var pairs = fieldNames.Select(name => (Name: name, Match: Regex.Match(name,
                @"^Condition_([0-9]{2})_(Name|Description)_?L([12])$", RegexOptions.IgnoreCase)))
            .Where(field => field.Match.Success)
            .GroupBy(field => (Kind: field.Match.Groups[2].Value.ToUpperInvariant(), Number: field.Match.Groups[1].Value))
            .OrderBy(pair => pair.Key.Kind == "NAME" ? 0 : 1)
            .ThenBy(pair => pair.Key.Number, StringComparer.Ordinal);
        var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int row = 0;
        foreach (var pair in pairs)
        {
            foreach (var language in pair.GroupBy(field => field.Match.Groups[3].Value))
                slots.Add(language.First().Name, row * 2 + (language.Key == "1" ? 0 : 1));
            row++;
        }
        return slots;
    }

    private static int ObjectGroupOrder(string group) => group switch
    {
        "Object" => 0, "Visual" => 1, "Config" => 2, "Conditions" => 3, "Parameters" => 4, "Прочее" => 100, _ => 50
    };

    private IReadOnlyList<LookupOption> GetOfflineLookupOptions(Core.Architecture.FieldDefinition field, string className, string sourceTable)
        => OfflineLookupOptionsService.Get(_offlineProject, className, sourceTable, field);

    private static readonly object _typeLookupLogLock = new();

    private static void WriteTypeLookupDiagnostic(string message)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Configurator", "Logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "TypeLookup.log");
            lock (_typeLookupLogLock)
            {
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never break object editing.
        }
    }

    private static string GetRecordValue(Core.Projects.ProjectRecord record, string propertyName)
    {
        if (record?.Values == null || string.IsNullOrWhiteSpace(propertyName)) return string.Empty;
        foreach (var pair in record.Values)
            if (pair.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)) return pair.Value?.ToString()?.Trim('"') ?? string.Empty;
        return string.Empty;
    }

    private async void SaveObjectWithLookupControlsAsync()
    {
        if (_selectedRow == null || _selectedClass?.PrimaryStorage == null) return;
        try
        {
            if (_offlineProject != null)
            {
                var offlineValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var control in FindEditorControls(ObjectEditor))
                {
                    if (control.Tag is not string field || !_objects.Columns.Contains(field)) continue;
                    string text = control switch { TextBox box => box.Text, ComboBox combo => Convert.ToString(combo.SelectedValue) ?? "", _ => null };
                    offlineValues[field] = ConvertEditorValue(field, text);
                }
                var offlineIdentity = await BuildIdentityAsync();
                var offlineKeyColumns = offlineIdentity.Keys.ToList();
                if (offlineKeyColumns.Count == 0) throw new InvalidOperationException("Не удалось определить ключ изменяемой записи.");
                await UpdateAndJournalAsync(_selectedClass.PrimaryStorage.TableName, offlineKeyColumns, offlineIdentity, offlineValues, _selectedRow, $"Изменён объект «{PickName(_selectedRow, _selectedClass)}»");
                foreach (var pair in offlineValues)
                    if (_objects.Columns.Contains(pair.Key)) _selectedRow[pair.Key] = pair.Value ?? DBNull.Value;
                await RenderLookupObjectDetailsAsyncTask();
                TxtStatus.Text = "Объект сохранён в локальном проекте";
                ShowActionFeedback($"Изменения объекта «{PickName(_selectedRow, _selectedClass)}» сохранены");
                return;
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var control in FindEditorControls(ObjectEditor))
            {
                if (control.Tag is not string field || !_objects.Columns.Contains(field)) continue;
                string text = control switch { TextBox box => box.Text, ComboBox combo => Convert.ToString(combo.SelectedValue) ?? "", _ => null };
                if (control is ComboBox lookupCombo)
                {
                    var definition = _selectedClass.Fields.FirstOrDefault(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                    if (lookupCombo.Items.Count == 0 && definition != null && !definition.Nullable)
                        throw new InvalidOperationException($"Поле «{field}» связано со справочником, но список значений пуст. Сохранение заблокировано.");
                    if (!string.IsNullOrWhiteSpace(text) && !lookupCombo.Items.Cast<object>().OfType<LookupOption>().Any(o => o.Key.Equals(text, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"Поле «{field}»: выберите существующее значение из справочника.");
                }
                values[field] = ConvertEditorValue(field, text);
            }
            var identity = await BuildIdentityAsync();
            var keyColumns = identity.Keys.ToList();
            if (keyColumns.Count == 0) throw new InvalidOperationException("Не удалось определить ключ изменяемой записи.");
            await UpdateAndJournalAsync(_selectedClass.PrimaryStorage.TableName, keyColumns, identity, values, _selectedRow, $"Изменён объект «{PickName(_selectedRow, _selectedClass)}»");
            foreach (var pair in values)
                if (_objects.Columns.Contains(pair.Key)) _selectedRow[pair.Key] = pair.Value ?? DBNull.Value;
            await RenderLookupObjectDetailsAsyncTask();
            TxtStatus.Text = "Объект сохранён · связанные поля проверены по справочникам";
            ShowActionFeedback($"Изменения объекта «{PickName(_selectedRow, _selectedClass)}» сохранены");
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Ошибка: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}", isError: true);
            Configurator.Infrastructure.ModernDialog.Show(Configurator.Infrastructure.UserErrorMessage.Format(ex), "Сохранение объекта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RenderLookupObjectDetailsAsyncTask()
    {
        await Task.Yield();
        RenderLookupObjectDetailsAsync();
    }

    private object ConvertEditorValue(string field, string text)
    {
        if (!_objects.Columns.Contains(field)) return text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return DBNull.Value;
        var type = Nullable.GetUnderlyingType(_objects.Columns[field].DataType) ?? _objects.Columns[field].DataType;
        try
        {
            if (type == typeof(string)) return text;
            if (type == typeof(int)) return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (type == typeof(short)) return short.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (type == typeof(byte)) return byte.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (type == typeof(decimal)) return decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return bool.Parse(text);
            if (type == typeof(Guid)) return Guid.Parse(text);
            return Convert.ChangeType(text, type, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { throw new InvalidOperationException($"Поле «{field}» требует значение типа {type.Name}, но «{text}» нельзя преобразовать."); }
    }

    private static IEnumerable<FrameworkElement> FindEditorControls(DependencyObject root)
    {
        if (root == null) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBox || child is ComboBox) yield return (FrameworkElement)child;
            foreach (var nested in FindEditorControls(child)) yield return nested;
        }
    }
    private sealed record LookupFieldItem(Core.Architecture.FieldDefinition Field, string Group);
}
