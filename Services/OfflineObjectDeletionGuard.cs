#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator.Core.Compare;
using Configurator.Core.Projects;

namespace Configurator;

internal static class OfflineObjectDeletionGuard
{
    public static void Check(OfflineProjectDataSource source, string tableName,
        IReadOnlyList<Dictionary<string, object>> targets)
    {
        var project = source.Project;
        var captured = DatabaseGraphRelationMaterializer.ReadCapturedSchema(project.Architecture["physicalSchema"]);
        var schema = DatabaseGraphRelationMaterializer.WithProjectTables(captured, project.TableSchemas);
        var document = new GraphRelationStore(project).Load(strict: true);
        // Hiding an edge in the graph does not remove dependencies from the data.
        var relations = DatabaseGraphRelationMaterializer.Materialize(schema,
            new GraphRelationProjectDocument { ManualRelations = document.ManualRelations });
        var resolved = GraphRelationDefinition.ResolveTableName(schema.Tables.Select(t => t.FullName), tableName);
        foreach (var relation in relations.Where(r => r.Target.Equals(resolved, StringComparison.OrdinalIgnoreCase)))
        {
            var dependentSchema = schema.FindTable(relation.Source);
            var targetSchema = schema.FindTable(relation.Target);
            var columns = relation.SourceColumns.Zip(relation.TargetColumns).Select(pair => (
                Source: dependentSchema.Columns.Single(c => c.Name.Equals(pair.First, StringComparison.OrdinalIgnoreCase)),
                Target: targetSchema.Columns.Single(c => c.Name.Equals(pair.Second, StringComparison.OrdinalIgnoreCase)))).ToArray();
            foreach (var target in targets)
            {
                var targetValues = columns.Select(pair => target.TryGetValue(pair.Target.Name, out var value)
                    ? ProjectKeyCodec.ToNode(value)
                    : throw new InvalidOperationException($"В объекте отсутствует значение поля связи «{pair.Target.Name}».")).ToArray();
                if (targetValues.Any(value => value == null)) continue;
                foreach (var dependent in source.GetRecords(relation.Source))
                {
                    bool possibleReference = true;
                    for (int i = 0; i < columns.Length && possibleReference; i++)
                    {
                        var pair = columns[i];
                        if (dependent.Values.TryGetPropertyValue(pair.Source.Name, out var value))
                            possibleReference = CouldMatch(value, targetValues[i]!, pair.Source.DataType, pair.Target.DataType);
                    }
                    if (possibleReference)
                        throw new InvalidOperationException(
                            $"Удаление остановлено. Локальные записи «{relation.Source}» могут ссылаться на объект из «{relation.Target}».");
                }
            }
        }
    }

    private static bool CouldMatch(JsonNode? left, JsonNode right, string sourceType, string targetType)
    {
        if (left == null) return false;
        var result = CompareValues.Compare(left, right, sourceType, targetType);
        if (result != CompareStatus.Changed) return true;
        return left is JsonValue l && l.TryGetValue<string>(out var a) &&
            right is JsonValue r && r.TryGetValue<string>(out var b) &&
            CultureInfo.InvariantCulture.CompareInfo.Compare(a.TrimEnd(' '), b.TrimEnd(' '),
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth) == 0;
    }
}