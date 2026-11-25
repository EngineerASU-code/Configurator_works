#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;

namespace Configurator.Core.Compare;

/// <summary>A proposed change to one Mode 3 element, generated without writing to either source.</summary>
public sealed class CompareTransferPreview
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private CompareTransferPreview(CompareSource source, CompareSource target, string description, string? blockReason)
    {
        Source = source;
        Target = target;
        Description = description;
        BlockReason = blockReason;
    }

    internal bool DeletesRecord => IsAllowed && TargetSchema is not null && SourceRecord is null && ExpectedRecord is not null;
    public bool IsAllowed => BlockReason is null;
    public string? BlockReason { get; }
    public string Description { get; }
    internal string Summary { get; private init; } = string.Empty;

    internal CompareSource Source { get; }
    internal ChangeJournalEntry? RecordedEntry { get; set; }
    internal (ConfiguratorProject Project, string Fingerprint)? SavedPackage { get; set; }
    internal CompareSource Target { get; }
    internal ProjectTableSchema? TargetSchema { get; private init; }
    internal ProjectTableSchema? SourceSchema { get; private init; }
    internal ProjectRecord? SourceRecord { get; private init; }
    internal IReadOnlyDictionary<string, object?> SourceKeyValues { get; private init; } = new Dictionary<string, object?>();
    internal string? TargetMetadataFingerprint { get; private init; }
    internal ProjectRecord? ExpectedRecord { get; private init; }
    internal IReadOnlyDictionary<string, object?> KeyValues { get; private init; } = new Dictionary<string, object?>();
    internal IReadOnlyDictionary<string, object?> Values { get; private init; } = new Dictionary<string, object?>();
    internal bool CopiesComputedArea => ExpectedRecord is null && TargetSchema is { } schema &&
        CopiesOfflineEquipmentArea(Source, Target, schema) &&
        SourceSchema is { } sourceSchema && FindColumn(sourceSchema, "Area")?.IsComputed == true;

    public static CompareTransferPreview Evaluate(
        CompareSource a, CompareSource b, CompareDifference difference, bool aToB)
    {
        var source = aToB ? a : b;
        var target = aToB ? b : a;
        CompareTransferPreview Block(string reason) => new(source, target, difference.Title, reason);

        if (difference.Status == CompareStatus.KeyConflict)
            return Block(difference.Issue ?? "Обнаружен конфликт ключа Record.");

        if (difference.Status is not (CompareStatus.Changed or CompareStatus.OnlyA or CompareStatus.OnlyB) ||
            difference.Pair is not { } pair)
            return Block("Для этого результата нет однозначной операции режима 3.");
        if (!target.CanWrite || target.Workspace.IsReadOnly)
            return Block("Целевой источник открыт только для чтения.");

        var fromTable = aToB ? pair.A : pair.B;
        var toTable = aToB ? pair.B : pair.A;
        var from = aToB ? pair.RecordA : pair.RecordB;
        var to = aToB ? pair.RecordB : pair.RecordA;
        if (from is null && (to is null || difference.Column is not null))
            return Block("Нет записи для синхронизации.");
        if (fromTable?.Class is null || toTable?.Class is null ||
            !Names.Equals(fromTable.Class.Id, toTable.Class.Id) ||
            !Names.Equals(fromTable.Schema.TableName, toTable.Schema.TableName))
            return Block("Требуются один сопоставленный класс и совместимые таблицы.");
        if (!fromTable.Class.Available || !toTable.Class.Available)
            return Block("Класс недоступен в одном из источников.");
        if (fromTable.MappingRole == "ambiguous" || toTable.MappingRole == "ambiguous")
            return Block("Не удалось подтвердить логические зависимости.");
        if (string.IsNullOrEmpty(source.ProjectId) || !Names.Equals(source.ProjectId, target.ProjectId))
        {
            if (string.IsNullOrEmpty(source.Origin) || !Names.Equals(source.Origin, target.Origin))
                return Block("Происхождение и соответствие ключей проектов не подтверждены.");
        }

        var schema = toTable.Schema;
        if (!ProjectWriteGuard.IsDataTableWritable(schema.TableName))
            return Block("Изменение реестра классов отключено.");
        if (!target.Workspace.IsOffline &&
            (toTable.MetadataFingerprint is null || (from is not null && (to is null ? target.PhysicalSchema?.FindTable(schema.TableName)?.CanCompareInsert : target.PhysicalSchema?.FindTable(schema.TableName)?.CanCompareUpdate) != true)))
            return Block("Условия записи в эту SQL-таблицу не подтверждены.");
        if (!schema.HasStableKey || schema.KeyColumns.Count == 0 ||
            !schema.KeyColumns.SequenceEqual(fromTable.Schema.KeyColumns, Names))
            return Block("Не подтверждён стабильный ключ целевой таблицы.");

        var keys = new Dictionary<string, object?>(Names);
        var sourceKeys = new Dictionary<string, object?>(Names);
        var values = new Dictionary<string, object?>(Names);
        var fields = new List<string>();

        try
        {
            foreach (var key in schema.KeyColumns)
            {
                var column = FindColumn(schema, key);
                if (column is null || (from ?? to!).Values[key] is null || (to is not null && to.Values[key] is null))
                    return Block("В записи отсутствует значение ключа.");
                keys.Add(column.Name, ToValue((to ?? from!).Values[key], column));
                var sourceColumn = FindColumn(fromTable.Schema, key);
                if (sourceColumn is null) return Block("В источнике отсутствует поле ключа.");
                sourceKeys.Add(sourceColumn.Name, ToValue((from ?? to!).Values[key], sourceColumn));
            }

            if (from is null)
            {
                var directionLabel = aToB ? "A → B" : "B → A";
                var deletion = $"{directionLabel} · Удалить объект из {target.Workspace.DisplayName} · {schema.TableName} · {string.Join(", ", keys.Select(k => $"{k.Key}={k.Value}"))}";
                return new CompareTransferPreview(source, target, deletion, null)
                {
                    Summary = deletion, SourceSchema = fromTable.Schema, TargetSchema = schema,
                    SourceKeyValues = sourceKeys, KeyValues = keys, ExpectedRecord = to,
                    TargetMetadataFingerprint = toTable.MetadataFingerprint
                };
            }
            if (difference.Column is string field)
            {
                var column = FindColumn(schema, field);
                var fromColumn = FindColumn(fromTable.Schema, field);
                if (to is null)
                    return Block("Сначала нужен существующий целевой объект.");
                if (column is null || fromColumn is null || !Compatible(column, fromColumn))
                    return Block("Структура целевого поля несовместима.");
                if (column.IsIdentity || column.IsComputed || column.IsReadOnly ||
                    schema.KeyColumns.Contains(field, Names))
                    return Block("Это ключевое, связанное или управляемое поле.");
                if (!from.Values.TryGetPropertyValue(fromColumn.Name, out var value))
                    return Block("В источнике параметр отсутствует.");
                values.Add(column.Name, ToValue(value, column));
                var oldValue = to.Values.TryGetPropertyValue(column.Name, out var old) ? Display(old) : "отсутствует";
                fields.Add($"{column.Name}  {oldValue} → {Display(value)}");
            }
            else if (to is not null)
            {
                if (difference.Children is not { Count: > 0 } children)
                    return Block("У объекта нет изменённых полей.");
                foreach (var child in children.Where(child => child.Status != CompareStatus.Equal))
                {
                    if (child.Column is null || !ReferenceEquals(child.Pair?.RecordA, pair.RecordA) ||
                        !ReferenceEquals(child.Pair?.RecordB, pair.RecordB))
                        return Block("Выберите отдельное изменённое поле.");
                    var fieldPreview = Evaluate(a, b, child, aToB);
                    if (!fieldPreview.IsAllowed)
                        return Block($"{child.Column}: {fieldPreview.BlockReason} Выберите отдельное поле.");
                    foreach (var change in fieldPreview.Values)
                    {
                        values.Add(change.Key, change.Value);
                        fields.Add($"{change.Key}  {Display(to.Values[change.Key])} → {Display(from.Values[change.Key])}");
                    }
                }
                if (values.Count == 0) return Block("У объекта нет изменённых полей.");
            }

            else
            {
                foreach (var column in schema.Columns)
                {
                    var fromColumn = FindColumn(fromTable.Schema, column.Name);
                    if (CopiesOfflineEquipmentArea(source, target, schema) && column.Name.Equals("Area", StringComparison.OrdinalIgnoreCase) &&
                        column.IsComputed && fromColumn?.IsComputed == true && Compatible(column, fromColumn) &&
                        from.Values.TryGetPropertyValue(fromColumn.Name, out var area))
                    {
                        _ = ToValue(area, column);
                        fields.Add($"{column.Name}  {Display(area)}");
                        continue;
                    }
                    if (column.IsIdentity || column.IsComputed || column.IsReadOnly ||
                        fromColumn is null || !Compatible(column, fromColumn) ||
                        !from.Values.TryGetPropertyValue(fromColumn.Name, out var value))
                        return Block($"Поле «{column.Name}» отсутствует, несовместимо или недоступно для записи.");
                    values.Add(column.Name, ToValue(value, column));
                    fields.Add($"{column.Name}  {Display(value)}");
                }

                if (fromTable.Schema.Columns.Count != schema.Columns.Count)
                    return Block("Набор полей целевого объекта отличается.");
            }

            OfflineProjectDataSource.ValidateValues(schema, values, isCreate: to is null, generatedIdentityColumns: null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException or OverflowException)
        {
            return Block("Тип, длина или обязательность выбранного значения не подтверждены.");
        }

        var keyLabel = string.Join(", ", keys.Select(key => $"{key.Key}={key.Value}"));
        var action = to is null ? "Добавить объект" : difference.Column is null ? "Изменить поля объекта" : "Изменить выбранное поле";
        var direction = aToB ? "A → B" : "B → A";
        var sourceLabel = source.PackagePath ?? $"Online · {source.Workspace.DisplayName}";
        var targetLabel = target.PackagePath ?? $"Online · {target.Workspace.DisplayName}";
        var summary = $"{direction} · {action}\nИсточник — {sourceLabel}\nЦель — {targetLabel}\n{toTable.Class.Name} · {keyLabel}";
        return new CompareTransferPreview(source, target, $"{summary}\n\n{string.Join("\n", fields)}", null)
        {
            Summary = summary,
            TargetSchema = schema,
            SourceSchema = fromTable.Schema,
            SourceRecord = new ProjectRecord { TableName = from.TableName, Key = from.Key, RowVersion = from.RowVersion,
                Values = (JsonObject)from.Values.DeepClone() },
            SourceKeyValues = sourceKeys,
            TargetMetadataFingerprint = toTable.MetadataFingerprint,
            ExpectedRecord = to is null ? null : new ProjectRecord
            {
                TableName = to.TableName, Key = to.Key, RowVersion = to.RowVersion,
                Values = (JsonObject)to.Values.DeepClone()
            },
            KeyValues = keys,
            Values = values
        };
    }

    // Edits can only replace fields already approved in this preview. Keep the
    // original expected row and package fingerprint for the eventual write.
    internal CompareTransferPreview WithEdits(IReadOnlyDictionary<string, string?> edits)
    {
        if (edits.Count == 0) return this;
        if (!IsAllowed || TargetSchema is null || DeletesRecord)
            throw new InvalidOperationException("Редактировать можно только поля существующей записи.");
        var updated = new Dictionary<string, object?>(Values, Names);
        foreach (var (field, input) in edits)
        {
            if (!Values.ContainsKey(field) || (ExpectedRecord is null && !Names.Equals(field, "Record")))
                throw new InvalidOperationException($"Поле «{field}» недоступно для этой операции.");
            var column = FindColumn(TargetSchema, field)!;
            try
            {
                JsonNode? node = input is null ? null : column.DataType.ToLowerInvariant() switch
                {
                    "nvarchar" or "varchar" => JsonValue.Create(input),
                    "bit" => JsonValue.Create(input.Trim() switch
                    {
                        "0" => false, "1" => true, var text => bool.Parse(text)
                    }),
                    _ => JsonNode.Parse(input.Trim().Replace(',', '.')) ?? throw new FormatException()
                };
                updated[field] = ToValue(node, column);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Проверьте значение поля «{field}» ({column.DataType}).", ex);
            }
        }
        OfflineProjectDataSource.ValidateValues(TargetSchema, updated, ExpectedRecord is null, null);
        var changes = updated.Select(pair =>
            $"{pair.Key}  {Display(ExpectedRecord?.Values[pair.Key])} → {Display(JsonSerializer.SerializeToNode(pair.Value))}");
        return new CompareTransferPreview(Source, Target, $"{Summary}\n\n{string.Join("\n", changes)}", null)
        {
            Summary = Summary, TargetSchema = TargetSchema, TargetMetadataFingerprint = TargetMetadataFingerprint,
            ExpectedRecord = ExpectedRecord, KeyValues = KeyValues.ToDictionary(k => k.Key, k => ExpectedRecord is null ? updated[k.Key] : k.Value, Names), Values = updated,
            SourceSchema = SourceSchema, SourceRecord = SourceRecord, SourceKeyValues = SourceKeyValues
        };
    }

    internal static bool IsEquipmentTable(ProjectTableSchema schema) =>
        (Names.Equals(schema.TableName, "dbo.Valves") || Names.Equals(schema.TableName, "dbo.Reglers")) && schema.KeyColumns.Count == 3 &&
        schema.KeyColumns.Contains("Record", Names) && schema.KeyColumns.Contains("PLC", Names) &&
        schema.KeyColumns.Contains("PLC_Class_Number", Names);

    private static bool CopiesOfflineEquipmentArea(CompareSource source, CompareSource target, ProjectTableSchema schema) =>
        source.Workspace.IsOffline && target.Workspace.IsOffline &&
        IsEquipmentTable(schema) && FindColumn(schema, "Area")?.IsComputed == true;

    internal static IEnumerable<(string Table, string[] Columns, object?[] Values)> EquipmentLookups(
        ProjectTableSchema schema,
        IReadOnlyDictionary<string, object?> values)
    {
        yield return ("dbo.PLC", ["Record"], [values["PLC"]]);
        yield return ("dbo.PLC_CFG", ["PLC_Number", "PLC_Class_Number"], [values["PLC"], values["PLC_Class_Number"]]);
        bool pid = Names.Equals(schema.TableName, "dbo.Reglers");
        if (pid)
        {
            foreach (var (field, target) in new[] { ("AI_Number", "dbo.AnaIN"), ("AO_Number", "dbo.AnaOUT") })
                if (values.TryGetValue(field, out var channel) && channel is not null && Convert.ToInt32(channel, CultureInfo.InvariantCulture) != 0)
                    yield return (target, ["PLC", "Record"], [values["PLC"], channel]);
        }
        if (values.TryGetValue("Type", out var type) && type is not null && Convert.ToInt32(type, CultureInfo.InvariantCulture) != 0)
            yield return (pid ? "dbo.Reglers_Types" : "dbo.Valves_Type", ["Record"], [type]);
        if (values.TryGetValue("Area", out var areaValue) && areaValue is string area && !string.IsNullOrWhiteSpace(area))
            yield return ("dbo.Areas", ["AreaName"], [area]);
    }

    private static ProjectColumnSchema? FindColumn(ProjectTableSchema schema, string name) =>
        schema.Columns.FirstOrDefault(column => Names.Equals(column.Name, name));

    private static bool Compatible(ProjectColumnSchema a, ProjectColumnSchema b) =>
        Names.Equals(a.DataType, b.DataType) && a.MaxLength == b.MaxLength &&
        a.Precision == b.Precision && a.Scale == b.Scale;

    private static string Display(JsonNode? node) => node?.ToJsonString() ?? "NULL";

    private static object? ToValue(JsonNode? node, ProjectColumnSchema column)
    {
        if (node is null)
            return null;
        if (node is not JsonValue scalar)
            throw new FormatException();
        var text = scalar.ToJsonString();
        switch (column.DataType.ToLowerInvariant())
        {
            case "tinyint": return byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "smallint": return short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "int": return int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "bigint": return long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "bit": return scalar.Deserialize<bool>();
            case "decimal":
            case "numeric":
                if (column.Precision > 28 || column.Scale > column.Precision) throw new FormatException();
                var number = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (CompareValues.Compare(scalar, JsonValue.Create(number), column.DataType, column.DataType) != CompareStatus.Equal)
                    throw new FormatException();
                if (column.Precision > 0)
                {
                    var limit = decimal.Parse("1" + new string('0', column.Precision - column.Scale), CultureInfo.InvariantCulture);
                    if (decimal.Round(number, column.Scale) != number || Math.Abs(number) >= limit) throw new FormatException();
                }
                return number;
            case "real":
                var real = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                return float.IsFinite(real) ? real : throw new FormatException();
            case "varchar":
            case "nvarchar":
                return scalar.Deserialize<string>();
            default:
                throw new FormatException();
        }
    }
}
