using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Configurator;

public partial class ProjectModeView
{
    private readonly Dictionary<string, IReadOnlyList<LookupOption>> _offlineLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Core.Projects.ProjectTableSchema Schema, IReadOnlyList<Core.Projects.ProjectRecord> Records)> _offlineLookupTableCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<Core.Projects.ProjectTableSchema> _offlineTablesCache;
    private async void RenderLookupObjectDetailsAsync()
    {
        if (_selectedRow == null || _selectedClass == null) return;
        bool offline = _offlineProject != null;
        if (!offline && (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))) return;

        // One continuous editor column: every field remains visible and there are
        // no empty alternating group columns on wide screens.
        ObjectEditor.Children.Clear();
        ObjectEditor.RowDefinitions.Clear();
        ObjectEditor.ColumnDefinitions.Clear();
        ObjectEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 500 });

        TxtObjectName.Text = PickName(_selectedRow, _selectedClass);
        // PLC / Area / Record are displayed once in TxtObjectContext below.
        // Keep this line for the object's class only to avoid duplicated metadata.
        TxtObjectMeta.Text = $"Класс: {_selectedClass.Name}";
        TxtPreviewGlyph.Text = _selectedClass.Name.Length > 3 ? _selectedClass.Name[..3].ToUpperInvariant() : _selectedClass.Name.ToUpperInvariant();
        var classDescription = _selectedClass.Description?.Trim() ?? string.Empty;
        var objectDescription = new[] { "DescriptionL1", "Description", "Description_L1", "Comment", "Notes" }
            .Where(name => _selectedRow.Table.Columns.Contains(name))
            .Select(name => Convert.ToString(_selectedRow[name])?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var previewDescription = !string.IsNullOrWhiteSpace(objectDescription) ? objectDescription : classDescription;
        TxtPreviewClass.Text = string.IsNullOrWhiteSpace(previewDescription)
            ? _selectedClass.Name
            : $"{_selectedClass.Name} · {previewDescription}";
        TxtPreviewClass.ToolTip = string.IsNullOrWhiteSpace(objectDescription)
            ? classDescription
            : $"Объект: {objectDescription}\nКласс: {classDescription}";

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
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int index = 0;
            foreach (var item in group)
            {
                int row = index / 2;
                int col = index % 2;
                while (content.RowDefinitions.Count <= row) content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var field = item.Field;
                var rowPanel = new Grid { Margin = new Thickness(col == 0 ? 0 : 7, 0, col == 0 ? 7 : 0, 7) };
                rowPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star), MinWidth = 110 });
                rowPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 180 });

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
                if (field.Name.Equals("Type", StringComparison.OrdinalIgnoreCase))
                {
                    string currentValueForLog = GetText(field.Name);
                    WriteTypeLookupDiagnostic($"EDITOR_COMBO class={_selectedClass?.Name} sourceTable={_selectedClass?.PrimaryStorage?.TableName} offline={offline} currentKey='{currentValueForLog}' optionsCount={options.Count}");
                }
                bool knownLookupField = new[] { "Type", "PLC", "Area", "AreaName", "PLCClassNumber", "PLCClasssNumber", "Class", "Unit" }
                    .Contains(field.Name, StringComparer.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(field.LookupTable) && !string.IsNullOrWhiteSpace(field.LookupKeyField));
                var comboOptions = options.ToList();
                string currentLookupValue = GetText(field.Name);
                bool isGeneratedArea = field.Name.Equals("Area", StringComparison.OrdinalIgnoreCase);
                bool isReadOnlyClassNumber = new[] { "PLC_Class_Number", "PLCClassNumber", "PLCClasssNumber" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase);
                if (field.Name.Equals("Type", StringComparison.OrdinalIgnoreCase))
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
                else if (options.Count > 0 || knownLookupField)
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
                Grid.SetColumn(editor, 1);
                rowPanel.Children.Add(editor);
                Grid.SetColumn(rowPanel, col);
                Grid.SetRow(rowPanel, row);
                content.Children.Add(rowPanel);
                index++;
            }

            var expander = new Expander
            {
                Header = $"{groupEntry.Key}  ·  {group.Count} полей",
                Content = content,
                IsExpanded = true,
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            expander.SetResourceReference(Control.ForegroundProperty, "BrushText");
            card.Child = expander;
            Grid.SetColumn(card, 0);
            Grid.SetRow(card, groupRow++);
            ObjectEditor.Children.Add(card);
        }

        TxtStatus.Text = $"{(offline ? "Offline · " : string.Empty)}{_selectedClass.Name} · показаны все {actualFields.Count} полей · {(offline ? "значения взяты из локального снимка" : "связанные поля — выпадающие списки")}";
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

    private static int ObjectGroupOrder(string group) => group switch
    {
        "Object" => 0, "Visual" => 1, "Config" => 2, "Conditions" => 3, "Parameters" => 4, "Прочее" => 100, _ => 50
    };

    private IReadOnlyList<LookupOption> GetOfflineLookupOptions(Core.Architecture.FieldDefinition field, string className, string sourceTable)
    {
        if (_offlineProject == null || field == null) return Array.Empty<LookupOption>();

        string lookupTable = field.LookupTable?.Trim() ?? string.Empty;
        string keyField = field.LookupKeyField?.Trim() ?? string.Empty;
        string displayField = field.LookupDisplayField?.Trim() ?? string.Empty;
        string fieldName = field.Name?.Trim() ?? string.Empty;
        var generationSettings = Configurator.Infrastructure.Projects.LookupGenerationSettingsStore.Load(_offlineProject);
        var generationRule = Configurator.Core.Architecture.LookupGenerationRuleSelector.Find(generationSettings, className, sourceTable, fieldName);
        if (generationRule != null)
        {
            if (!generationRule.Enabled) return Array.Empty<LookupOption>();
            // Project rule takes precedence over inferred metadata and legacy naming rules.
            lookupTable = generationRule.LookupTable;
            keyField = generationRule.KeyField;
            displayField = generationRule.DisplayField;
        }
        string cacheKey = string.Join("|", _offlineProject.Manifest.ProjectId, sourceTable ?? string.Empty, className ?? string.Empty, fieldName, lookupTable, keyField, displayField);
        if (_offlineLookupCache.TryGetValue(cacheKey, out var cached))
        {
            if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
                WriteTypeLookupDiagnostic($"CACHE_HIT class={className} field={fieldName} cachedCount={cached.Count} cacheKey='{cacheKey}'");
            return cached;
        }

        // Explicit class-to-dictionary mappings for Type. Do not probe guessed table names:
        // the snapshot contains the same dbo tables as Online, and NameL2 is the display label.
        string exactTypeTableName = string.Empty;
        if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase) && generationRule == null)
        {
            exactTypeTableName = className?.Trim().ToUpperInvariant() switch
            {
                // PLC.Type is a foreign key into PLCType; show PLCType.Name, keep its Record key.
                "PLC" => "PLCType",
                // Use the real table names observed in the imported Offline snapshot.
                // Keep the SQL schema's exact spelling, including underscores and plurals.
                "VALVE" => "Valves_Type",
                "DSV" => "Valves2S_Type",
                "MOTOR" => "Motors_Type",
                "DI" => "DiscretIN_Types",
                "DO" => "DiscretOUT_Types",
                "AI" => "AnaIn_Type",
                // Some project versions call this AnaIOut_Type; this snapshot contains AnaOUT_Type.
                "AO" => "AnaOUT_Type",
                "PID" => "Reglers_Types",
                _ => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(exactTypeTableName))
            {
                lookupTable = FindOfflineTable("dbo." + exactTypeTableName, exactTypeTableName);
                if (string.IsNullOrWhiteSpace(lookupTable) && className?.Trim().Equals("AO", StringComparison.OrdinalIgnoreCase) == true)
                    lookupTable = FindOfflineTable("AnaIOut_Type", "AnaIOut_Types", "AnaOUT_Type", "AnaOUT_Types");
                keyField = "Record";
                displayField = className?.Trim().Equals("PLC", StringComparison.OrdinalIgnoreCase) == true ? "Name" : "NameL2";
                WriteTypeLookupDiagnostic($"MAPPING class='{className}' sourceTable='{sourceTable}' exactTable='dbo.{exactTypeTableName}' resolvedTable='{lookupTable}' keyField='{keyField}' displayField='{displayField}' projectId='{_offlineProject.Manifest.ProjectId}'");
            }
        }

        // If a project uses a new spelling for a type dictionary, discover it from
        // the snapshot's table names. Match the class stem + Type, allowing only
        // an optional plural "s"; this deliberately prevents Valve from matching
        // Valve2S_Type (the latter is the separate DSV class).
        if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(lookupTable) && generationSettings.AutomaticDiscoveryEnabled)
        {
            lookupTable = FindTypeTableByClassName(className);
            if (!string.IsNullOrWhiteSpace(lookupTable))
            {
                keyField = "Record";
                displayField = "NameL2";
                WriteTypeLookupDiagnostic($"AUTO_DISCOVERY class='{className}' resolvedTable='{lookupTable}' keyField='{keyField}' displayField='{displayField}'");
            }
        }

        if (string.IsNullOrWhiteSpace(lookupTable) && !(fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(exactTypeTableName)))
        {
            switch (fieldName.ToUpperInvariant())
            {
                case "AREA":
                case "AREANAME": lookupTable = FindOfflineTable("Areas", "Area", "AreaNames"); keyField = "Record"; displayField = "AreaNameL2"; break;
                case "PLC": lookupTable = FindOfflineTable("PLC", "PLCs", "PlcList"); keyField = "Record"; displayField = "NameL2"; break;
                case "UNIT": lookupTable = FindOfflineTable("Units", "Unit"); keyField = "Unit_ID"; displayField = "Name"; break;
                case "CLASS": lookupTable = FindOfflineTable("Classes", "Class"); keyField = "Record"; displayField = "Name"; break;
                case "PLCCLASSNUMBER":
                case "PLCCLASS SNUMBER": lookupTable = FindOfflineTable("PLCClasssNumbers", "PLCClasssNumber", "PLCClassNumbers", "PLCClassNumber", "PLCClasses"); keyField = fieldName; displayField = "Name"; break;
                case "TYPE":
                    var definition = Core.Architecture.SystemLookupCatalog.Find(sourceTable, fieldName) ?? Core.Architecture.SystemLookupCatalog.ForClass(className);
                    lookupTable = FindOfflineTable(GetTypeTableName(className), GetPluralTypeTableName(className), definition?.LookupTable, className + "Type", className + "Types");
                    keyField = "Record"; displayField = "NameL2";
                    break;
            }
        }
        else if (!string.IsNullOrWhiteSpace(lookupTable) && !(fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(lookupTable)))
        {
            var configuredLookupTable = lookupTable;
            var resolvedLookupTable = FindOfflineTable(configuredLookupTable);
            if (!string.IsNullOrWhiteSpace(resolvedLookupTable))
                lookupTable = resolvedLookupTable;
            else if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                // Class libraries from older versions may point at singular table names
                // (ValveType), while a snapshot may contain the actual plural table (ValvesType).
                lookupTable = FindOfflineTable(GetTypeTableName(className), GetPluralTypeTableName(className),
                    configuredLookupTable, className + "Type", className + "Types");
                if (string.IsNullOrWhiteSpace(keyField)) keyField = "Record";
                if (string.IsNullOrWhiteSpace(displayField)) displayField = "NameL2";
            }
            else lookupTable = string.Empty;
        }

        var source = _offlineSource ?? new Core.Projects.OfflineProjectDataSource(_offlineProject);
        if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
        {
            var allTables = _offlineTablesCache ?? source.GetTables();
            WriteTypeLookupDiagnostic($"TABLES projectId='{_offlineProject.Manifest.ProjectId}' requested='{lookupTable}' count={allTables.Count} names=[{string.Join("; ", allTables.Select(t => t.TableName))}]");
        }
        Core.Projects.ProjectTableSchema schema = null;
        IReadOnlyList<Core.Projects.ProjectRecord> records = Array.Empty<Core.Projects.ProjectRecord>();
        if (!string.IsNullOrWhiteSpace(lookupTable))
        {
            if (_offlineLookupTableCache.TryGetValue(lookupTable, out var cachedTable))
            {
                schema = cachedTable.Schema;
                records = cachedTable.Records;
            }
            else
            {
                try
                {
                    schema = source.GetSchema(lookupTable);
                    records = source.GetRecords(schema?.TableName ?? lookupTable);
                    if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
                        WriteTypeLookupDiagnostic($"READ_TABLE requested='{lookupTable}' actual='{schema?.TableName}' schemaColumns=[{string.Join(",", schema?.Columns?.Select(c => c.Name) ?? Enumerable.Empty<string>())}] records={records.Count}");
                }
                catch (Exception ex)
                {
                    if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase)) WriteTypeLookupDiagnostic($"READ_TABLE_ERROR requested='{lookupTable}' error={ex}");
                    throw;
                }
                if (schema == null && records.Count > 0)
                {
                    // Older snapshots can contain the captured dictionary rows without a
                    // matching schema entry. Infer the columns from those rows so the real
                    // NameL2 dictionary still populates the combo instead of a current-value stub.
                    schema = new Core.Projects.ProjectTableSchema
                    {
                        TableName = records[0].TableName,
                        Columns = records.SelectMany(r => r.Values.Select(v => v.Key))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(name => new Core.Projects.ProjectColumnSchema { Name = name })
                            .ToList()
                    };
                }
                if (schema != null)
                {
                    if (records.Count == 0) records = source.GetRecords(schema.TableName);
                    _offlineLookupTableCache[lookupTable] = (schema, records);
                    _offlineLookupTableCache[schema.TableName] = (schema, records);
                }
            }
        }
        if (schema != null && records.Count > 0)
        {
            string[] keyCandidates = { keyField, "Record", "Type", "ID", "Id", "PLCClassNumber", "PLCClasssNumber", "AreaName", "Unit_ID" };
            string[] displayCandidates = { displayField, "NameL2", "NameL1", "Name_L2", "Name_L1", "Name", "Title", "DescriptionL2", "DescriptionL1", "Description", "Caption", "Label", "AreaName", "PLCName" };
            string actualKey = keyCandidates.Where(x => !string.IsNullOrWhiteSpace(x)).FirstOrDefault(x => schema.Columns.Any(c => c.Name.Equals(x, StringComparison.OrdinalIgnoreCase)) || records.Any(r => r.Values.ContainsKey(x)));
            string actualDisplay = displayCandidates.Where(x => !string.IsNullOrWhiteSpace(x)).FirstOrDefault(x => schema.Columns.Any(c => c.Name.Equals(x, StringComparison.OrdinalIgnoreCase)) || records.Any(r => r.Values.ContainsKey(x)));
            if (!string.IsNullOrWhiteSpace(actualKey))
            {
                var result = records.Select(r =>
                    {
                        string key = GetRecordValue(r, actualKey);
                        string label = !string.IsNullOrWhiteSpace(actualDisplay) ? GetRecordValue(r, actualDisplay) : string.Empty;
                        bool isAreaField = fieldName.Equals("Area", StringComparison.OrdinalIgnoreCase) || fieldName.Equals("AreaName", StringComparison.OrdinalIgnoreCase);
                        return new LookupOption { Key = key, Display = string.IsNullOrWhiteSpace(label) || label.Equals(key, StringComparison.OrdinalIgnoreCase) ? key : isAreaField ? $"{key} - {label}" : $"{key} · {label}" };
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                    .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .OrderBy(x => x.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
                    WriteTypeLookupDiagnostic($"OPTIONS_BUILT table='{lookupTable}' keyColumn='{actualKey}' displayColumn='{actualDisplay}' options={result.Count} values=[{string.Join("; ", result.Select(o => $"{o.Key}={o.Display}"))}]");
                if (result.Count > 0) { _offlineLookupCache[cacheKey] = result; return result; }
            }
        }

        // Fallback to a copied text list packaged in the .cfgproj. This is the
        // project-owned mask: it stays available without any live SQL dictionary.
        var lookupLeaf = string.IsNullOrWhiteSpace(lookupTable) ? string.Empty : lookupTable.Split('.').Last();
        var textList = _offlineProject.TextLists.FirstOrDefault(x =>
            (!string.IsNullOrWhiteSpace(lookupTable) &&
                ((x.SourceTable ?? string.Empty).Equals(lookupTable, StringComparison.OrdinalIgnoreCase) ||
                 (x.Name ?? string.Empty).Equals(lookupTable, StringComparison.OrdinalIgnoreCase))) ||
            (x.Name ?? string.Empty).Equals(fieldName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(x.SourceTable) && !string.IsNullOrWhiteSpace(lookupLeaf) &&
                x.SourceTable.Split('.').Last().Equals(lookupLeaf, StringComparison.OrdinalIgnoreCase)));
        if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
            WriteTypeLookupDiagnostic($"TEXTLIST_FALLBACK table='{lookupTable}' matchedList='{textList?.Name}' sourceTable='{textList?.SourceTable}' items={textList?.Items?.Count ?? 0}");
        if (textList?.Items?.Count > 0)
        {
            var copiedOptions = textList.Items.Select(x => new LookupOption { Key = x.Key ?? string.Empty, Display = string.IsNullOrWhiteSpace(x.Text) ? x.Key ?? string.Empty : $"{x.Key} · {x.Text}" })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key)).OrderBy(x => x.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
            _offlineLookupCache[cacheKey] = copiedOptions;
            return copiedOptions;
        }
        var emptyOptions = Array.Empty<LookupOption>();
        if (fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase))
            WriteTypeLookupDiagnostic($"NO_OPTIONS class='{className}' field='{fieldName}' requestedTable='{lookupTable}' keyField='{keyField}' displayField='{displayField}' current='{GetText(fieldName)}'");
        _offlineLookupCache[cacheKey] = emptyOptions;
        return emptyOptions;
    }

    private string FindTypeTableByClassName(string className)
    {
        if (_offlineProject == null || string.IsNullOrWhiteSpace(className)) return string.Empty;
        if (_offlineTablesCache == null)
            _offlineTablesCache = (_offlineSource ?? new Core.Projects.OfflineProjectDataSource(_offlineProject)).GetTables();

        string classKey = className.Trim().ToUpperInvariant();
        string stem = classKey switch
        {
            "VALVE" => "valve",
            "DSV" => "valve2s",
            "MOTOR" => "motor",
            "DI" => "discretin",
            "DO" => "discretout",
            "AI" => "anain",
            "AO" => "anaout",
            "PID" => "regler",
            _ => new string(className.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()
        };
        if (string.IsNullOrWhiteSpace(stem)) return string.Empty;

        var matches = _offlineTablesCache
            .Select(t => new { t.TableName, Name = new string((t.TableName ?? string.Empty).Split('.').Last().Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() })
            .Where(t =>
            {
                int stemIndex = t.Name.IndexOf(stem, StringComparison.Ordinal);
                if (stemIndex < 0) return false;
                string remainder = t.Name[(stemIndex + stem.Length)..];
                // Accepted shapes: ValveType / ValvesType / Valve_Type / Valves_Type.
                // No arbitrary suffixes between class stem and Type (e.g. Valve2SType).
                if (remainder.StartsWith("type", StringComparison.Ordinal)) return true;
                return remainder.StartsWith("stype", StringComparison.Ordinal);
            })
            .OrderBy(t => t.Name.Length)
            .ToList();
        return matches.FirstOrDefault()?.TableName ?? string.Empty;
    }

    private string FindOfflineTable(params string[] candidates)
    {
        if (_offlineProject == null) return string.Empty;
        if (_offlineTablesCache == null)
            _offlineTablesCache = (_offlineSource ?? new Core.Projects.OfflineProjectDataSource(_offlineProject)).GetTables();
        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var table = _offlineTablesCache.FirstOrDefault(x => x.TableName.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                x.TableName.Split('.').Last().Equals(candidate.Split('.').Last(), StringComparison.OrdinalIgnoreCase));
            if (table != null) return table.TableName;
        }
        return string.Empty;
    }

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

    private static string GetTypeTableName(string className) => className?.Trim().ToUpperInvariant() switch
    {
        "DI" => "DiscretIN_Types", "DO" => "DiscretOUT_Types", "AI" => "AnaIn_Type", "AO" => "AnaOUT_Type",
        "PID" => "Reglers_Types", "DSV" => "Valves2S_Type", "VALVE" => "Valves_Type", "MOTOR" => "Motors_Type",
        "STATUS" => "StatusesType", "PROGRAM" => "ProTypes", "STEP" => "Prog_StepType", _ => "Type"
    };

    private static string GetPluralTypeTableName(string className) => className?.Trim().ToUpperInvariant() switch
    {
        "DI" => "DiscretIN_Types", "DO" => "DiscretOUT_Types", "AI" => "AnaIn_Type", "AO" => "AnaOUT_Type",
        "PID" => "Reglers_Types", "DSV" => "Valves2S_Type", "VALVE" => "Valves_Type", "MOTOR" => "Motors_Type",
        "STATUS" => "StatusType", "PROGRAM" => "ProgramType", "STEP" => "StepType", _ => className + "Types"
    };

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
                await RequireCrudSource().UpdateRecordAsync(_selectedClass.PrimaryStorage.TableName, offlineKeyColumns, offlineIdentity, offlineValues);
                foreach (var pair in offlineValues)
                    if (_objects.Columns.Contains(pair.Key)) _selectedRow[pair.Key] = pair.Value ?? DBNull.Value;
                await RenderLookupObjectDetailsAsyncTask();
                TxtStatus.Text = "Объект сохранён в локальном проекте";
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
            await RequireCrudSource().UpdateRecordAsync(_selectedClass.PrimaryStorage.TableName, keyColumns, identity, values);
            foreach (var pair in values)
                if (_objects.Columns.Contains(pair.Key)) _selectedRow[pair.Key] = pair.Value ?? DBNull.Value;
            await RenderLookupObjectDetailsAsyncTask();
            TxtStatus.Text = "Объект сохранён · связанные поля проверены по справочникам";
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show(ex.Message, "Сохранение объекта", MessageBoxButton.OK, MessageBoxImage.Error); }
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
