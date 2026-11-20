#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;

namespace Configurator;

public enum DatabaseGraphRelationOrigin
{
    PhysicalForeignKey,
    Inferred,
    Manual
}

public sealed class DatabaseGraphRelation
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public List<string> SourceColumns { get; init; } = new();
    public List<string> TargetColumns { get; init; } = new();
    public string Rule { get; init; } = "";
    public RelationConfidence? Confidence { get; init; }
    public DatabaseGraphRelationOrigin Origin { get; init; }
    public bool IsManual => Origin == DatabaseGraphRelationOrigin.Manual;
}

/// <summary>Combines captured SQL relations with project-defined graph relations without inventing endpoints.</summary>
public static class DatabaseGraphRelationMaterializer
{
    private static readonly JsonSerializerOptions CapturedSchemaOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
    };

    public static DatabaseSchema? ReadCapturedSchema(JsonNode? captured) =>
        captured?.Deserialize<DatabaseSchema>(CapturedSchemaOptions);

    public static DatabaseSchema WithProjectTables(DatabaseSchema? captured, IReadOnlyList<ProjectTableSchema>? currentTables)
    {
        var schema = new DatabaseSchema
        {
            DatabaseName = captured?.DatabaseName ?? "",
            ServerName = captured?.ServerName ?? "",
            AnalyzedAtUtc = captured?.AnalyzedAtUtc ?? DateTimeOffset.UtcNow
        };
        foreach (var tableSchema in currentTables ?? Array.Empty<ProjectTableSchema>())
        {
            var split = tableSchema.TableName.Split('.', 2);
            var table = new DatabaseTable
            {
                SchemaName = split.Length == 2 ? split[0] : "dbo",
                Name = split.Length == 2 ? split[1] : split[0]
            };
            foreach (var column in tableSchema.Columns ?? new List<ProjectColumnSchema>())
                table.Columns.Add(new DatabaseColumn
                {
                    Name = column.Name,
                    DataType = column.DataType,
                    MaxLength = column.MaxLength,
                    IsNullable = column.IsNullable,
                    IsIdentity = column.IsIdentity,
                    IsComputed = column.IsComputed
                });
            if (tableSchema.KeyColumns is { Count: > 0 })
            {
                var key = new DatabaseKey { Name = "Snapshot", IsPrimary = tableSchema.HasStableKey, IsUnique = true };
                key.Columns.AddRange(tableSchema.KeyColumns);
                table.Keys.Add(key);
            }
            schema.Tables.Add(table);
        }
        if (captured is not null) schema.Relations.AddRange(captured.Relations ?? new List<DatabaseRelation>());
        return schema;
    }

    public static IReadOnlyList<DatabaseGraphRelation> Materialize(
        DatabaseSchema schema,
        GraphRelationProjectDocument? document = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        document ??= new GraphRelationProjectDocument();
        var tables = (schema.Tables ?? new List<DatabaseTable>()).ToDictionary(table => table.FullName, StringComparer.OrdinalIgnoreCase);
        var suppressed = new HashSet<string>(document.SuppressedAutomaticRelations ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var result = new List<DatabaseGraphRelation>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relation in schema.Relations ?? new List<DatabaseRelation>())
        {
            // Old snapshots also stored manually configured links in physicalSchema.
            // The graph document is their authoritative source and retains provenance.
            if (relation.Kind == DatabaseRelationKind.UnresolvedCandidate && relation.RuleId.StartsWith("manual-", StringComparison.OrdinalIgnoreCase))
                continue;
            bool physical = relation.Kind == DatabaseRelationKind.PhysicalForeignKey;
            var source = ExactTable(tables, relation.SourceTable);
            var target = ExactTable(tables, relation.TargetTable);
            if (source is null || target is null) continue;
            var sourceTable = tables[source];
            var targetTable = tables[target];
            var sourceColumns = relation.SourceColumns ?? new List<string>();
            var targetColumns = relation.TargetColumns ?? new List<string>();
            if (!HasColumns(sourceTable, sourceColumns) || !HasColumns(targetTable, targetColumns)) continue;

            var id = physical
                ? $"FK|{relation.RuleId}|{source}|{target}"
                : AutomaticId(source, target, sourceColumns, targetColumns);
            if (!physical && suppressed.Contains(id)) continue;
            if (!ids.Add(id)) continue;
            result.Add(new DatabaseGraphRelation
            {
                Id = id,
                Source = source,
                Target = target,
                SourceColumns = sourceColumns.ToList(),
                TargetColumns = targetColumns.ToList(),
                Rule = relation.Comment,
                Confidence = relation.Confidence,
                Origin = physical ? DatabaseGraphRelationOrigin.PhysicalForeignKey : DatabaseGraphRelationOrigin.Inferred
            });
        }

        foreach (var manual in (document.ManualRelations ?? new List<GraphRelationDefinition>()).Where(relation => relation is not null))
        {
            var source = GraphRelationDefinition.ResolveTableName(tables.Keys, manual.Source);
            var target = GraphRelationDefinition.ResolveTableName(tables.Keys, manual.Target);
            if (source is null || target is null || string.IsNullOrWhiteSpace(manual.SourceColumn) || string.IsNullOrWhiteSpace(manual.TargetColumn)) continue;
            if (!(tables[source].Columns ?? new List<DatabaseColumn>()).Any(column => string.Equals(column.Name, manual.SourceColumn, StringComparison.OrdinalIgnoreCase)) ||
                !(tables[target].Columns ?? new List<DatabaseColumn>()).Any(column => string.Equals(column.Name, manual.TargetColumn, StringComparison.OrdinalIgnoreCase))) continue;

            var id = AutomaticId(source, target, new[] { manual.SourceColumn }, new[] { manual.TargetColumn });
            if (!ids.Add(id)) continue;
            result.Add(new DatabaseGraphRelation
            {
                Id = id,
                Source = source,
                Target = target,
                SourceColumns = new List<string> { manual.SourceColumn },
                TargetColumns = new List<string> { manual.TargetColumn },
                Rule = manual.Rule,
                Origin = DatabaseGraphRelationOrigin.Manual
            });
        }

        return result;
    }

    private static string? ExactTable(IReadOnlyDictionary<string, DatabaseTable> tables, string name) =>
        tables.Keys.FirstOrDefault(table => table.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string AutomaticId(string source, string target, IEnumerable<string> sourceColumns, IEnumerable<string> targetColumns) =>
        $"LOGICAL|{source}|{target}|{string.Join(",", sourceColumns)}|{string.Join(",", targetColumns)}";

    private static bool HasColumns(DatabaseTable table, IEnumerable<string> columns) =>
        columns.Any() && columns.All(name => (table.Columns ?? new List<DatabaseColumn>()).Any(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)));
}
