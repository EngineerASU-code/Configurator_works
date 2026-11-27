using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Architecture;
using Microsoft.Data.SqlClient;

namespace Configurator;

/// <summary>Builds a project-specific relation graph. It never creates or alters SQL relations.</summary>
public sealed class DatabaseArchitectureAnalyzer
{
    private readonly string _connectionString;
    public DatabaseArchitectureAnalyzer(string connectionString) => _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    public async Task<DatabaseSchema> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, cancellationToken);
        var schema = new DatabaseSchema { DatabaseName = connection.Database, ServerName = connection.DataSource, AnalyzedAtUtc = DateTimeOffset.UtcNow };
        await LoadTablesAsync(connection, schema, cancellationToken);
        await LoadKeysAsync(connection, schema, cancellationToken);
        await LoadForeignKeysAsync(connection, schema, cancellationToken);
        AddCompositeEntityKeyRelations(schema);
        AddScalarConventionRelations(schema);
        AddConfiguredLogicalRelations(schema);
        KnownProjectRelationRules.Add(schema);
        return schema;
    }

    private static async Task LoadTablesAsync(SqlConnection connection, DatabaseSchema schema, CancellationToken ct)
    {
        const string sql = @"
SELECT s.name AS SchemaName,t.name AS TableName,c.name AS ColumnName,ty.name AS DataType,
       c.max_length AS MaxLength,c.is_nullable AS IsNullable,c.is_identity AS IsIdentity,
       c.is_computed AS IsComputed,c.column_id AS ColumnId,dc.definition AS DefaultDefinition,
       CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')=1
            AND HAS_PERMS_BY_NAME(QUOTENAME(s.name)+'.'+QUOTENAME(t.name), 'OBJECT', 'UPDATE')=1
            AND DATABASEPROPERTYEX(DB_NAME(), 'Updateability')='READ_WRITE'
            AND NOT EXISTS (SELECT 1 FROM sys.triggers tr WHERE tr.parent_id=t.object_id AND tr.is_disabled=0)
            THEN 1 ELSE 0 END AS CanCompareUpdate,
       CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')=1
            AND HAS_PERMS_BY_NAME(QUOTENAME(s.name)+'.'+QUOTENAME(t.name), 'OBJECT', 'INSERT')=1
            AND DATABASEPROPERTYEX(DB_NAME(), 'Updateability')='READ_WRITE'
            AND NOT EXISTS (SELECT 1 FROM sys.triggers tr WHERE tr.parent_id=t.object_id AND tr.is_disabled=0)
            THEN 1 ELSE 0 END AS CanCompareInsert
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name,c.column_id;";
        var map = new Dictionary<string, DatabaseTable>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schemaName = Convert.ToString(reader["SchemaName"]) ?? "dbo";
            var tableName = Convert.ToString(reader["TableName"]) ?? "";
            var fullName = $"{schemaName}.{tableName}";
            if (!map.TryGetValue(fullName, out var table))
            {
                table = new DatabaseTable
                {
                    SchemaName = schemaName,
                    Name = tableName,
                    IsComplexEntity = IsComplexName(tableName),
                    CanCompareUpdate = Convert.ToBoolean(reader["CanCompareUpdate"]),
                    CanCompareInsert = Convert.ToBoolean(reader["CanCompareInsert"])
                };
                map[fullName] = table; schema.Tables.Add(table);
            }
            table.Columns.Add(new DatabaseColumn
            {
                Name = Convert.ToString(reader["ColumnName"]) ?? "",
                DataType = Convert.ToString(reader["DataType"]) ?? "",
                MaxLength = Convert.ToInt32(reader["MaxLength"]),
                IsNullable = Convert.ToBoolean(reader["IsNullable"]),
                IsIdentity = Convert.ToBoolean(reader["IsIdentity"]),
                IsComputed = Convert.ToBoolean(reader["IsComputed"]),
                DefaultDefinition = reader["DefaultDefinition"] is string definition ? definition : null
            });
        }
    }

    private static async Task LoadKeysAsync(SqlConnection connection, DatabaseSchema schema, CancellationToken ct)
    {
        const string sql = @"
SELECT s.name AS SchemaName,t.name AS TableName,i.name AS IndexName,i.is_primary_key AS IsPrimary,
       i.is_unique AS IsUnique,c.name AS ColumnName,ic.key_ordinal AS KeyOrdinal
FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE t.is_ms_shipped=0 AND (i.is_primary_key=1 OR i.is_unique=1) AND ic.is_included_column=0
ORDER BY s.name,t.name,i.index_id,ic.key_ordinal;";
        var map = new Dictionary<string, DatabaseKey>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tableName = $"{Convert.ToString(reader["SchemaName"])}.{Convert.ToString(reader["TableName"])}";
            var indexName = Convert.ToString(reader["IndexName"]) ?? "";
            var id = tableName + "\u001f" + indexName;
            if (!map.TryGetValue(id, out var key))
            {
                key = new DatabaseKey { Name = indexName, IsPrimary = Convert.ToBoolean(reader["IsPrimary"]), IsUnique = Convert.ToBoolean(reader["IsUnique"]) };
                map[id] = key; schema.FindTable(tableName)?.Keys.Add(key);
            }
            key.Columns.Add(Convert.ToString(reader["ColumnName"]) ?? "");
        }
    }

    private static async Task LoadForeignKeysAsync(SqlConnection connection, DatabaseSchema schema, CancellationToken ct)
    {
        const string sql = @"
SELECT fk.name AS ConstraintName,ss.name AS SourceSchema,st.name AS SourceTable,sc.name AS SourceColumn,
       ts.name AS TargetSchema,tt.name AS TargetTable,tc.name AS TargetColumn,fkc.constraint_column_id AS KeyOrdinal
FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id=fkc.constraint_object_id
JOIN sys.tables st ON st.object_id=fkc.parent_object_id JOIN sys.schemas ss ON ss.schema_id=st.schema_id
JOIN sys.columns sc ON sc.object_id=fkc.parent_object_id AND sc.column_id=fkc.parent_column_id
JOIN sys.tables tt ON tt.object_id=fkc.referenced_object_id JOIN sys.schemas ts ON ts.schema_id=tt.schema_id
JOIN sys.columns tc ON tc.object_id=fkc.referenced_object_id AND tc.column_id=fkc.referenced_column_id
ORDER BY fk.name,fkc.constraint_column_id;";
        var map = new Dictionary<string, DatabaseRelation>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = Convert.ToString(reader["ConstraintName"]) ?? "";
            var sourceTable = $"{reader["SourceSchema"]}.{reader["SourceTable"]}";
            var targetTable = $"{reader["TargetSchema"]}.{reader["TargetTable"]}";
            var key = $"{sourceTable}\u001f{name}\u001f{targetTable}";
            if (!map.TryGetValue(key, out var relation))
            {
                relation = new DatabaseRelation
                {
                    SourceTable = sourceTable, TargetTable = targetTable,
                    Kind = DatabaseRelationKind.PhysicalForeignKey, Confidence = RelationConfidence.Confirmed,
                    RuleId = name, Comment = "Physical SQL Server FK; authoritative."
                };
                map[key] = relation; schema.Relations.Add(relation);
            }
            relation.SourceColumns.Add(Convert.ToString(reader["SourceColumn"]) ?? "");
            relation.TargetColumns.Add(Convert.ToString(reader["TargetColumn"]) ?? "");
        }
    }

    private static void AddCompositeEntityKeyRelations(DatabaseSchema schema)
    {
        var ownKey = new[] { "PLC", "PLC_Class_Number", "Record" };
        foreach (var source in schema.Tables)
        {
            if (HasColumns(source, new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record" }))
            {
                var target = schema.Tables.FirstOrDefault(t => t.Name.Equals("Prog", StringComparison.OrdinalIgnoreCase) && HasUniqueKey(t, ownKey));
                if (target != null) AddContextRelation(schema, source, target, new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record" }, ownKey, "program-root-context", "Program child context resolves to Prog.(PLC, PLC_Class_Number, Record).");
            }
            if (HasColumns(source, new[] { "PLC", "PLC_Class_Matrix_Number", "Matrix_Record" }))
            {
                var target = schema.Tables.FirstOrDefault(t => t.Name.Equals("Matrix_List", StringComparison.OrdinalIgnoreCase) && HasUniqueKey(t, ownKey));
                if (target != null) AddContextRelation(schema, source, target, new[] { "PLC", "PLC_Class_Matrix_Number", "Matrix_Record" }, ownKey, "matrix-root-context", "Matrix child context resolves to Matrix_List.(PLC, PLC_Class_Number, Record).");
            }
            if (HasColumns(source, new[] { "PLC", "PLC_Class_Status_Number", "Status_Record" }))
            {
                var target = schema.Tables.FirstOrDefault(t => t.Name.Equals("Statuses", StringComparison.OrdinalIgnoreCase) && HasUniqueKey(t, ownKey));
                if (target != null) AddContextRelation(schema, source, target, new[] { "PLC", "PLC_Class_Status_Number", "Status_Record" }, ownKey, "status-context", "Status reference resolves by PLC + class + Record context.");
            }
            if (HasColumns(source, new[] { "PLC", "PLC_Class_Step_Number", "Step_Record" }))
            {
                var target = schema.Tables.FirstOrDefault(t => t.Name.Equals("Prog_Step", StringComparison.OrdinalIgnoreCase) && HasUniqueKey(t, ownKey));
                if (target != null) AddContextRelation(schema, source, target, new[] { "PLC", "PLC_Class_Step_Number", "Step_Record" }, ownKey, "step-context", "Step reference resolves by PLC + step class + Record.");
            }
            if (HasColumns(source, new[] { "Device_PLC", "Device_PLC_Class_Number", "Device_Record", "Device_Global_Class" }))
            {
                var classes = schema.Tables.FirstOrDefault(t => t.Name.Equals("Classes", StringComparison.OrdinalIgnoreCase));
                if (classes != null)
                    schema.Relations.Add(new DatabaseRelation
                    {
                        SourceTable = source.FullName, TargetTable = classes.FullName,
                        Kind = DatabaseRelationKind.PolymorphicClassReference, Confidence = RelationConfidence.High,
                        RuleId = RelationRuleCatalog.PolymorphicClass,
                        Comment = "Device_Global_Class chooses the concrete target table; Device_* identifies the target object."
                    }.WithColumns(new[] { "Device_Global_Class" }, new[] { "Record" }));
            }
        }
    }

    internal static void AddScalarConventionRelations(DatabaseSchema schema)
    {
        foreach (var source in schema.Tables)
        {
            AddScalar(schema, source, "PLC", "PLC", "Record", DatabaseRelationKind.ConventionalLookup, RelationConfidence.High, RelationRuleCatalog.PlcRecord, "PLC value resolves to PLC.Record.");
            AddScalar(schema, source, "Class", "Classes", "Record", DatabaseRelationKind.ClassLookup, RelationConfidence.High, RelationRuleCatalog.ClassRecord, "Class value resolves to Classes.Record.");
            AddPlcClassConfiguration(schema, source);
            AddArea(schema, source); AddUnit(schema, source); AddSuffixLookupRelations(schema, source);
        }

        var plcCfg = schema.Tables.FirstOrDefault(t => t.Name.Equals("PLC_CFG", StringComparison.OrdinalIgnoreCase));
        var classes = schema.Tables.FirstOrDefault(t => t.Name.Equals("Classes", StringComparison.OrdinalIgnoreCase));
        if (plcCfg != null && classes != null && HasColumn(plcCfg, "Class_Number") && HasColumn(classes, "Record"))
            schema.Relations.Add(new DatabaseRelation
            {
                SourceTable = plcCfg.FullName, TargetTable = classes.FullName,
                Kind = DatabaseRelationKind.ClassLookup, Confidence = RelationConfidence.High,
                RuleId = "plc-cfg-class-number", Comment = "PLC_CFG.Class_Number resolves the configured class label."
            }.WithColumns(new[] { "Class_Number" }, new[] { "Record" }));
    }

    private static void AddPlcClassConfiguration(DatabaseSchema schema, DatabaseTable source)
    {
        if (!HasColumns(source, new[] { "PLC", "PLC_Class_Number" })) return;
        var target = schema.Tables.FirstOrDefault(t => t.Name.Equals("PLC_CFG", StringComparison.OrdinalIgnoreCase) && HasColumns(t, new[] { "PLC_Number", "PLC_Class_Number" }) && HasUniqueKey(t, new[] { "PLC_Number", "PLC_Class_Number" }));
        if (target == null) return;
        schema.Relations.Add(new DatabaseRelation
        {
            SourceTable = source.FullName, TargetTable = target.FullName,
            Kind = DatabaseRelationKind.ContextualProgramReference, Confidence = RelationConfidence.High,
            RuleId = "plc-class-config", Comment = "Object PLC + PLC_Class_Number resolves through PLC_CFG; never map PLC_Class_Number directly to Classes."
        }.WithColumns(new[] { "PLC", "PLC_Class_Number" }, new[] { "PLC_Number", "PLC_Class_Number" }));
    }

    private static void AddArea(DatabaseSchema schema, DatabaseTable source)
    {
        if (!HasColumn(source, "Area")) return;
        var target = schema.Tables.FirstOrDefault(t => t.Name.Equals("Areas", StringComparison.OrdinalIgnoreCase) && HasColumn(t, "AreaName"));
        if (target == null) return;
        schema.Relations.Add(new DatabaseRelation
        {
            SourceTable = source.FullName, TargetTable = target.FullName, Kind = DatabaseRelationKind.AreaNameLookup,
            Confidence = RelationConfidence.High, RuleId = RelationRuleCatalog.AreaName,
            Comment = "Area stores business value AreaName, not Areas.Record."
        }.WithColumns(new[] { "Area" }, new[] { "AreaName" }));
    }

    private static void AddUnit(DatabaseSchema schema, DatabaseTable source)
    {
        var column = source.Columns.FirstOrDefault(c => c.Name.Equals("Unit", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("Units", StringComparison.OrdinalIgnoreCase));
        var target = schema.Tables.FirstOrDefault(t => t.Name.Equals("Units", StringComparison.OrdinalIgnoreCase) && HasColumn(t, "Unit_ID"));
        if (column == null || target == null) return;
        schema.Relations.Add(new DatabaseRelation
        {
            SourceTable = source.FullName, TargetTable = target.FullName, Kind = DatabaseRelationKind.UnitLookup,
            Confidence = RelationConfidence.Medium, RuleId = RelationRuleCatalog.UnitId,
            Comment = "Convention-based unit reference; unresolved values remain validation warnings."
        }.WithColumns(new[] { column.Name }, new[] { "Unit_ID" }));
    }

    private static void AddSuffixLookupRelations(DatabaseSchema schema, DatabaseTable source)
    {
        // A large part of the legacy databases uses lookup tables such as
        // AnaIn_Type / AnaIn_Types without declaring an SQL FK. Treat the
        // naming convention as a real logical dependency when the source
        // contains the matching field. This is deliberately limited to an
        // exact source-table prefix so we do not invent arbitrary relations.
        foreach (var column in source.Columns)
        {
            if (column.IsComputed) continue;
            if (string.IsNullOrWhiteSpace(column.Name)) continue;

            var candidates = schema.Tables
                .Where(t =>
                    (t.Name.Equals(source.Name + "_" + column.Name, StringComparison.OrdinalIgnoreCase) ||
                     t.Name.Equals(source.Name + "_" + column.Name + "s", StringComparison.OrdinalIgnoreCase)) &&
                    !t.FullName.Equals(source.FullName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count != 1) continue;
            var target = candidates[0];

            // Prefer Record because that is the canonical lookup identifier in
            // this database family. If it is absent, fall back only to a single
            // unique/PK column so the relation still has a deterministic target.
            var targetColumn = target.Columns.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name;
            if (targetColumn == null)
            {
                var singleColumnKey = target.Keys
                    .Where(k => k.IsUnique && k.Columns.Count == 1)
                    .SelectMany(k => k.Columns)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (singleColumnKey.Count == 1) targetColumn = singleColumnKey[0];
            }
            if (targetColumn == null) continue;

            var ruleId = RelationRuleCatalog.TypeLookup + ":" + source.Name + ":" + column.Name;
            if (schema.Relations.Any(r =>
                    r.SourceTable.Equals(source.FullName, StringComparison.OrdinalIgnoreCase) &&
                    r.TargetTable.Equals(target.FullName, StringComparison.OrdinalIgnoreCase) &&
                    r.SourceColumns.SequenceEqual(new[] { column.Name }, StringComparer.OrdinalIgnoreCase) &&
                    r.TargetColumns.SequenceEqual(new[] { targetColumn }, StringComparer.OrdinalIgnoreCase)))
                continue;

            schema.Relations.Add(new DatabaseRelation
            {
                SourceTable = source.FullName,
                TargetTable = target.FullName,
                Kind = DatabaseRelationKind.ConventionalLookup,
                // Exact <SourceTable>_<Column> naming is strong enough to be
                // used by deletion protection. It is still a logical relation:
                // no SQL constraint is created or changed.
                Confidence = RelationConfidence.High,
                RuleId = ruleId,
                Comment = $"Комментарий: значение поля {source.FullName}.{column.Name} должно соответствовать одному из значений {target.FullName}.{targetColumn}; список допустимых значений хранится в таблице {target.FullName}. Это логическая связь, ограничение SQL FK не создаётся."
            }.WithColumns(new[] { column.Name }, new[] { targetColumn }));
        }
    }

    private static void AddContextRelation(DatabaseSchema schema, DatabaseTable source, DatabaseTable target, IEnumerable<string> sourceColumns, IEnumerable<string> targetColumns, string ruleId, string comment)
    {
        schema.Relations.Add(new DatabaseRelation
        {
            SourceTable = source.FullName, TargetTable = target.FullName,
            Kind = DatabaseRelationKind.ContextualProgramReference, Confidence = RelationConfidence.High,
            RuleId = ruleId, Comment = comment
        }.WithColumns(sourceColumns, targetColumns));
    }

    private static void AddConfiguredLogicalRelations(DatabaseSchema schema)
    {
        try
        {
            var store = new GraphRelationStore(schema.ServerName, schema.DatabaseName);
            var document = store.Load();
            var suppressed = new HashSet<string>(document.SuppressedAutomaticRelations ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var manual in document.ManualRelations ?? new List<GraphRelationDefinition>())
            {
                var source = schema.FindTable(manual.Source);
                var target = schema.FindTable(manual.Target);
                if (source == null || target == null || string.IsNullOrWhiteSpace(manual.SourceColumn) || string.IsNullOrWhiteSpace(manual.TargetColumn))
                    continue;
                if (!source.Columns.Any(c => c.Name.Equals(manual.SourceColumn, StringComparison.OrdinalIgnoreCase)) ||
                    !target.Columns.Any(c => c.Name.Equals(manual.TargetColumn, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (schema.Relations.Any(r => r.SourceTable.Equals(manual.Source, StringComparison.OrdinalIgnoreCase) &&
                                               r.TargetTable.Equals(manual.Target, StringComparison.OrdinalIgnoreCase) &&
                                               r.SourceColumns.SequenceEqual(new[] { manual.SourceColumn }, StringComparer.OrdinalIgnoreCase) &&
                                               r.TargetColumns.SequenceEqual(new[] { manual.TargetColumn }, StringComparer.OrdinalIgnoreCase)))
                    continue;
                schema.Relations.Add(new DatabaseRelation
                {
                    SourceTable = manual.Source, TargetTable = manual.Target,
                    Kind = DatabaseRelationKind.UnresolvedCandidate, Confidence = RelationConfidence.High,
                    RuleId = "manual-" + manual.Source + "-" + manual.Target,
                    Comment = string.IsNullOrWhiteSpace(manual.Rule) ? "Manual logical relation." : manual.Rule
                }.WithColumns(new[] { manual.SourceColumn }, new[] { manual.TargetColumn }));
            }
        }
        catch
        {
            // A missing/corrupt local relation document must never prevent schema analysis.
        }
    }

    private static bool IsComplexName(string tableName) => tableName.StartsWith("Prog", StringComparison.OrdinalIgnoreCase) || tableName.StartsWith("Matrix", StringComparison.OrdinalIgnoreCase);
    private static bool HasColumn(DatabaseTable table, string name) => table.Columns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static bool HasColumns(DatabaseTable table, IEnumerable<string> names) => names.All(n => HasColumn(table, n));
    private static bool HasUniqueKey(DatabaseTable table, IEnumerable<string> columns) => table.Keys.Any(k => k.IsUnique && k.Columns.SequenceEqual(columns, StringComparer.OrdinalIgnoreCase));
    private static void AddScalar(DatabaseSchema schema, DatabaseTable source, string sourceColumn, string targetName, string targetColumn, DatabaseRelationKind kind, RelationConfidence confidence, string ruleId, string comment)
    {
        if (!HasColumn(source, sourceColumn)) return;
        var targets = schema.Tables.Where(t => t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase) && HasColumn(t, targetColumn)).ToList();
        if (targets.Count != 1) return;
        schema.Relations.Add(new DatabaseRelation { SourceTable = source.FullName, TargetTable = targets[0].FullName, Kind = kind, Confidence = confidence, RuleId = ruleId, Comment = comment }.WithColumns(new[] { sourceColumn }, new[] { targetColumn }));
    }
}

internal static class DatabaseRelationExtensions
{
    public static DatabaseRelation WithColumns(this DatabaseRelation relation, IEnumerable<string> source, IEnumerable<string> target)
    {
        relation.SourceColumns.AddRange(source); relation.TargetColumns.AddRange(target); return relation;
    }
}
