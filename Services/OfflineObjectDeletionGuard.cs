#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Compare;
using Configurator.Core.Projects;

namespace Configurator;

internal static class OfflineObjectDeletionGuard
{
    public static IReadOnlyList<DependencyReplacementWindow.DependencyOption> BuildOptions(
        OfflineProjectDataSource source, string tableName, IReadOnlyList<Dictionary<string, object?>> targets)
    {
        var project = source.Project;
        var captured = DatabaseGraphRelationMaterializer.ReadCapturedSchema(project.Architecture["physicalSchema"]);
        var schema = DatabaseGraphRelationMaterializer.WithProjectTables(captured, project.TableSchemas);
        var document = new GraphRelationStore(project).Load(strict: true);
        var relations = DatabaseGraphRelationMaterializer.Materialize(schema,
            new GraphRelationProjectDocument { ManualRelations = document.ManualRelations });
        var resolved = GraphRelationDefinition.ResolveTableName(schema.Tables.Select(t => t.FullName), tableName);
        var selectedValues = targets.Select(target => target).ToList();
        var options = new List<DependencyReplacementWindow.DependencyOption>();

        foreach (var relation in relations.Where(r => r.Target.Equals(resolved, StringComparison.OrdinalIgnoreCase)))
        {
            var sourceTable = schema.FindTable(relation.Source)!;
            var targetTable = schema.FindTable(relation.Target)!;
            var columnPairs = relation.SourceColumns.Zip(relation.TargetColumns).Select(pair => (
                Source: sourceTable.Columns.Single(c => c.Name.Equals(pair.First, StringComparison.OrdinalIgnoreCase)),
                Target: targetTable.Columns.Single(c => c.Name.Equals(pair.Second, StringComparison.OrdinalIgnoreCase)))).ToArray();

            foreach (var target in selectedValues)
            {
                var deletedValues = columnPairs.Select(pair => target.TryGetValue(pair.Target.Name, out var value)
                    ? value
                    : throw new InvalidOperationException($"В объекте отсутствует значение поля связи «{pair.Target.Name}».")).ToArray();
                if (deletedValues.Any(value => value == null || value == DBNull.Value)) continue;

                foreach (var dependent in source.GetRecords(relation.Source))
                {
                    bool possibleReference = columnPairs.Select((pair, index) =>
                        dependent.Values.TryGetPropertyValue(pair.Source.Name, out var value) &&
                        CouldMatch(value, ToNode(deletedValues[index]), pair.Source.DataType, pair.Target.DataType)).All(x => x);
                    if (!possibleReference) continue;

                    var dependentIdentity = GetStableIdentity(source.GetSchema(relation.Source), dependent);
                    var hit = new DependencyHit
                    {
                        SourceTable = relation.Source,
                        SourceKey = dependentIdentity,
                        SourceReferenceValues = columnPairs.ToDictionary(pair => pair.Source.Name,
                            pair => ToValue(dependent.Values[pair.Source.Name], pair.Source.DataType), StringComparer.OrdinalIgnoreCase),
                        SourceColumnDescription = string.Join(", ", relation.SourceColumns),
                        TargetTable = relation.Target,
                        Relation = new DatabaseRelation
                        {
                            SourceTable = relation.Source,
                            TargetTable = relation.Target,
                            Kind = relation.Origin == DatabaseGraphRelationOrigin.PhysicalForeignKey
                                ? DatabaseRelationKind.PhysicalForeignKey : DatabaseRelationKind.UnresolvedCandidate,
                            Confidence = relation.Confidence ?? RelationConfidence.High,
                            RuleId = relation.Id,
                            Comment = relation.Rule
                        }
                    };
                    hit.Relation.SourceColumns.AddRange(relation.SourceColumns);
                    hit.Relation.TargetColumns.AddRange(relation.TargetColumns);

                    var candidates = source.GetRecords(relation.Target)
                        .Where(candidate => !IsSelectedTarget(candidate, relation.TargetColumns, selectedValues))
                        .Select(candidate => BuildCandidate(candidate, relation.TargetColumns, targetTable.Columns.Select(c => c.Name)))
                        .Where(candidate => candidate != null).Cast<ReplacementCandidate>().ToList();
                    if (candidates.Count == 0)
                        throw new InvalidOperationException($"Зависимые записи «{relation.Source}» ссылаются на «{relation.Target}», но в локальной копии нет допустимой записи для замены.");

                    options.Add(new DependencyReplacementWindow.DependencyOption
                    {
                        Hit = hit,
                        CurrentValue = string.Join(" · ", deletedValues.Select(FormatValue)),
                        Candidates = candidates
                    });
                }
            }
        }
        return options;
    }

    public static void Check(OfflineProjectDataSource source, string tableName,
        IReadOnlyList<Dictionary<string, object?>> targets)
    {
        if (BuildOptions(source, tableName, targets).Count > 0)
            throw new InvalidOperationException("Удаление остановлено: найдены связанные записи. Сначала выберите замену связи.");
    }

    public static bool ValuesEqual(JsonNode? actual, object? expected) =>
        CouldMatch(actual, ToNode(expected), "", "");

    private static Dictionary<string, object> GetStableIdentity(ProjectTableSchema? schema, ProjectRecord record)
    {
        if (schema is not { HasStableKey: true, KeyColumns.Count: > 0 })
            throw new InvalidOperationException($"Нельзя безопасно заменить ссылку в «{record.TableName}»: в снимке нет подтверждённого ключа строки.");
        var identity = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in schema.KeyColumns)
        {
            if (!record.Values.TryGetPropertyValue(column, out var value) || value == null)
                throw new InvalidOperationException($"Нельзя безопасно заменить ссылку в «{record.TableName}»: в ключе отсутствует «{column}».");
            identity[column] = ToValue(value, schema.Columns.Single(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)).DataType);
        }
        return identity;
    }

    private static ReplacementCandidate? BuildCandidate(ProjectRecord record, IReadOnlyList<string> columns, IEnumerable<string> tableColumns)
    {
        var values = new List<object>();
        foreach (var column in columns)
        {
            if (!record.Values.TryGetPropertyValue(column, out var value) || value == null) return null;
            values.Add(ToValue(value, ""));
        }
        var displayColumn = tableColumns.FirstOrDefault(name =>
            name.Equals("NameL1", StringComparison.OrdinalIgnoreCase) || name.Equals("Name_L1", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Name", StringComparison.OrdinalIgnoreCase) || name.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase));
        var display = displayColumn != null && !columns.Contains(displayColumn, StringComparer.OrdinalIgnoreCase) &&
            record.Values.TryGetPropertyValue(displayColumn, out var label) && label != null
                ? ToValue(label, "").ToString() ?? string.Empty
                : string.Join(" · ", values.Select(FormatValue));
        return new ReplacementCandidate { Values = values, Display = display };
    }

    private static bool IsSelectedTarget(ProjectRecord candidate, IReadOnlyList<string> relationColumns,
        IReadOnlyList<Dictionary<string, object?>> selected)
    {
        return selected.Any(target => relationColumns.All(column =>
            target.TryGetValue(column, out var targetValue) && candidate.Values.TryGetPropertyValue(column, out var candidateValue) &&
            CouldMatch(candidateValue, ToNode(targetValue), "", "")));
    }

    private static JsonNode? ToNode(object? value) => value is JsonNode node ? node : ProjectKeyCodec.ToNode(value);

    private static bool CouldMatch(JsonNode? left, JsonNode? right, string sourceType, string targetType)
    {
        if (left == null || right == null) return false;
        if (CompareValues.Compare(left, right, sourceType, targetType) != CompareStatus.Changed) return true;
        return left is JsonValue l && l.TryGetValue<string>(out var a) && right is JsonValue r && r.TryGetValue<string>(out var b) &&
            CultureInfo.InvariantCulture.CompareInfo.Compare(a.TrimEnd(' '), b.TrimEnd(' '),
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth) == 0;
    }

    private static string FormatValue(object? value) => value switch
    {
        null or DBNull => "<NULL>",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    private static object ToValue(JsonNode? node, string dataType)
    {
        if (node is not JsonValue value) throw new InvalidOperationException("Значение ключа не является простым типом.");
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<int>(out var intValue)) return intValue;
        if (value.TryGetValue<long>(out var longValue)) return longValue;
        if (value.TryGetValue<decimal>(out var decimalValue)) return decimalValue;
        if (value.TryGetValue<double>(out var doubleValue)) return doubleValue;
        if (value.TryGetValue<bool>(out var boolValue)) return boolValue;
        if (value.TryGetValue<DateTime>(out var dateValue)) return dateValue;
        throw new InvalidOperationException($"Не удалось прочитать значение типа «{dataType}» из локального снимка.");
    }
}
