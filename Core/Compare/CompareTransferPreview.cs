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

    public bool IsAllowed => BlockReason is null;
    public string? BlockReason { get; }
    public string Description { get; }
    internal string Summary { get; private init; } = string.Empty;

    internal CompareSource Source { get; }
    internal CompareSource Target { get; }
    internal ProjectTableSchema? TargetSchema { get; private init; }
    internal ProjectTableSchema? SourceSchema { get; private init; }
    internal ProjectRecord? SourceRecord { get; private init; }
    internal IReadOnlyDictionary<string, object?> SourceKeyValues { get; private init; } = new Dictionary<string, object?>();
    internal string? TargetMetadataFingerprint { get; private init; }
    internal ProjectRecord? ExpectedRecord { get; private init; }
    internal IReadOnlyDictionary<string, object?> KeyValues { get; private init; } = new Dictionary<string, object?>();
    internal IReadOnlyDictionary<string, object?> Values { get; private init; } = new Dictionary<string, object?>();

    public static CompareTransferPreview Evaluate(
        CompareSource a, CompareSource b, CompareDifference difference, bool aToB)
    {
        var source = aToB ? a : b;
        var target = aToB ? b : a;
        CompareTransferPreview Block(string reason) => new(source, target, difference.Title, reason);

        if (difference.Status is not (CompareStatus.Changed or CompareStatus.OnlyA or CompareStatus.OnlyB) ||
            difference.Pair is not { } pair)
            return Block("Для этого результата нет однозначной операции режима 3.");
        if (!target.CanWrite || target.Workspace.IsReadOnly)
            return Block("Целевой источник открыт только для чтения.");

        var fromTable = aToB ? pair.A : pair.B;
        var toTable = aToB ? pair.B : pair.A;
        var from = aToB ? pair.RecordA : pair.RecordB;
        var to = aToB ? pair.RecordB : pair.RecordA;
        if (from is null)
            return Block("В источнике элемента нет. Удаление не предусмотрено.");
        if (fromTable?.Class is null || toTable?.Class is null ||
            !Names.Equals(fromTable.Class.Id, toTable.Class.Id) ||
            !Names.Equals(fromTable.Schema.TableName, toTable.Schema.TableName))
            return Block("Требуются один сопоставленный класс и совместимые таблицы.");
        if (!fromTable.Class.Available || !toTable.Class.Available)
            return Block("Класс недоступен в одном из источников.");
        if (fromTable.HasUnresolvedDependencies || toTable.HasUnresolvedDependencies)
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
            (toTable.MetadataFingerprint is null || (to is null ? target.PhysicalSchema?.FindTable(schema.TableName)?.CanCompareInsert : target.PhysicalSchema?.FindTable(schema.TableName)?.CanCompareUpdate) != true))
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
                if (column is null || from.Values[key] is null || (to is not null && to.Values[key] is null))
                    return Block("В записи отсутствует значение ключа.");
                keys.Add(column.Name, ToValue((to ?? from).Values[key], column));
                var sourceColumn = FindColumn(fromTable.Schema, key);
                if (sourceColumn is null) return Block("В источнике отсутствует поле ключа.");
                sourceKeys.Add(sourceColumn.Name, ToValue(from.Values[key], sourceColumn));
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
                    schema.KeyColumns.Contains(field, Names) || IsManagedField(field) ||
                    fromTable.DependencyColumns.Contains(field) || toTable.DependencyColumns.Contains(field))
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
                bool valve = IsValveTable(schema) &&
                    Names.Equals(fromTable.Class.Id, "valve") && Names.Equals(toTable.Class.Id, "valve") &&
                    fromTable.DependencyColumns.All(IsValveLookupColumn) && toTable.DependencyColumns.All(IsValveLookupColumn);
                if ((!valve && (fromTable.Class.IsSystem || toTable.Class.IsSystem ||
                    fromTable.Class.Types.Count != 0 || toTable.Class.Types.Count != 0 ||
                    fromTable.DependencyColumns.Count != 0 || toTable.DependencyColumns.Count != 0)) ||
                    fromTable.Class.StorageMappings.Count != 1 || toTable.Class.StorageMappings.Count != 1 ||
                    fromTable.Class.ComplexTables.Count != 0 || toTable.Class.ComplexTables.Count != 0 ||
                    !string.IsNullOrEmpty(fromTable.Class.BaseClassId) || !string.IsNullOrEmpty(toTable.Class.BaseClassId))
                    return Block("Создание поддерживается для независимого объекта с одной явно сопоставленной таблицей.");
                var physical = target.PhysicalSchema?.FindTable(schema.TableName);
                if (physical is null || physical.Keys.Any(key => key.IsUnique && !key.Columns.SequenceEqual(schema.KeyColumns, Names)))
                    return Block("Не удалось подтвердить отсутствие дополнительных ограничений уникальности.");

                foreach (var column in schema.Columns)
                {
                    var fromColumn = FindColumn(fromTable.Schema, column.Name);
                    if (column.IsIdentity || column.IsComputed || column.IsReadOnly ||
                        fromColumn is null || !Compatible(column, fromColumn) ||
                        !from.Values.TryGetPropertyValue(fromColumn.Name, out var value) ||
                        (IsManagedField(column.Name) && !Names.Equals(column.Name, "Name") &&
                         !(valve && IsValveLookupColumn(column.Name)) &&
                         !schema.KeyColumns.Contains(column.Name, Names)))
                        return Block($"Поле «{column.Name}» отсутствует, несовместимо или недоступно для записи.");
                    values.Add(column.Name, ToValue(value, column));
                    fields.Add($"{column.Name}  {Display(value)}");
                }

                if (fromTable.Schema.Columns.Count != values.Count)
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
        if (!IsAllowed || TargetSchema is null || ExpectedRecord is null)
            throw new InvalidOperationException("Редактировать можно только поля существующей записи.");
        var updated = new Dictionary<string, object?>(Values, Names);
        foreach (var (field, input) in edits)
        {
            if (!Values.ContainsKey(field))
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
        OfflineProjectDataSource.ValidateValues(TargetSchema, updated, false, null);
        var changes = updated.Select(pair =>
            $"{pair.Key}  {Display(ExpectedRecord.Values[pair.Key])} → {Display(JsonSerializer.SerializeToNode(pair.Value))}");
        return new CompareTransferPreview(Source, Target, $"{Summary}\n\n{string.Join("\n", changes)}", null)
        {
            Summary = Summary, TargetSchema = TargetSchema, TargetMetadataFingerprint = TargetMetadataFingerprint,
            ExpectedRecord = ExpectedRecord, KeyValues = KeyValues, Values = updated,
            SourceSchema = SourceSchema, SourceRecord = SourceRecord, SourceKeyValues = SourceKeyValues
        };
    }

    internal static bool IsValveTable(ProjectTableSchema schema) =>
        Names.Equals(schema.TableName, "dbo.Valves") && schema.KeyColumns.Count == 3 &&
        schema.KeyColumns.Contains("Record", Names) && schema.KeyColumns.Contains("PLC", Names) &&
        schema.KeyColumns.Contains("PLC_Class_Number", Names);

    private static bool IsValveLookupColumn(string field) =>
        Names.Equals(field, "PLC") || Names.Equals(field, "PLC_Class_Number") ||
        Names.Equals(field, "Type") || Names.Equals(field, "Area");

    internal static IEnumerable<(string Table, string[] Columns, object?[] Values)> ValveLookups(
        IReadOnlyDictionary<string, object?> values)
    {
        yield return ("dbo.PLC", ["Record"], [values["PLC"]]);
        yield return ("dbo.PLC_CFG", ["PLC_Number", "PLC_Class_Number"], [values["PLC"], values["PLC_Class_Number"]]);
        if (values.TryGetValue("Type", out var type) && type is not null && Convert.ToInt32(type, CultureInfo.InvariantCulture) != 0)
            yield return ("dbo.Valves_Type", ["Record"], [type]);
        if (values.TryGetValue("Area", out var areaValue) && areaValue is string area && !string.IsNullOrWhiteSpace(area))
            yield return ("dbo.Areas", ["AreaName"], [area]);
    }

    private static ProjectColumnSchema? FindColumn(ProjectTableSchema schema, string name) =>
        schema.Columns.FirstOrDefault(column => Names.Equals(column.Name, name));

    private static bool Compatible(ProjectColumnSchema a, ProjectColumnSchema b) =>
        Names.Equals(a.DataType, b.DataType) && a.MaxLength == b.MaxLength &&
        a.Precision == b.Precision && a.Scale == b.Scale;

    private static bool IsManagedField(string name) => name.Equals("Class", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ClassNumber", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PLC", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Type", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Area", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Unit", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ParentId", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_Record", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_Number", StringComparison.OrdinalIgnoreCase);

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
