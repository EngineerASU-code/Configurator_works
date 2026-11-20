#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;

namespace Configurator;

public partial class ProjectModeView
{
    private async Task<bool> DeleteOfflineConfigurationValueAsync(OfflineProjectDataSource source, int expectedGeneration)
    {
        if (_selectedRow == null) return false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var table = source.ResolveTableName(_configurationTableName);
        var schema = source.GetSchema(table)
            ?? throw new InvalidOperationException($"В локальном проекте отсутствует схема «{table}». Удаление отменено.");
        var rowIndex = _configurationRows.Rows.IndexOf(_selectedRow);
        var records = source.GetRecords(table);
        if (rowIndex < 0 || rowIndex >= records.Count)
            throw new InvalidOperationException("Не удалось сопоставить выбранную запись со снимком проекта. Удаление отменено.");
        var targetRecord = records[rowIndex];
        var relations = BuildOfflineDeletionRelations(source, table);
        var targetKey = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> identityColumns = schema.HasStableKey ? schema.KeyColumns : new[] { _configurationKeyColumn };
        foreach (var column in identityColumns.Where(column => !string.IsNullOrWhiteSpace(column)))
            if (TryReadProjectValue(targetRecord, schema, column, out var value))
                targetKey[column] = value;
        var targetColumns = relations.SelectMany(relation => relation.TargetColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var column in targetColumns)
        {
            if (!TryReadProjectValue(targetRecord, schema, column, out var value))
                throw new InvalidOperationException($"В записи отсутствует поле связи «{column}». Удаление отменено.");
            targetKey[column] = value;
        }

        var dependencies = FindOfflineDependents(source, targetRecord, targetKey, relations);
        SessionTestLog.Trace("CRUD", $"operation=DeleteConfigurationValue; phase=dependencies; source=offline; table='{table}'; dependentRows={dependencies.Count}; elapsedMs={timer.ElapsedMilliseconds}");
        if (dependencies.Any(item => item.Hit.SourceKey.Count == 0))
            throw new InvalidOperationException("В снимке найдены зависимые строки без стабильного ключа. Их нельзя безопасно заменить; удаление отменено.");
        if (dependencies.Count > 0 && dependencies.Any(item => !CanReplaceOfflineDependency(item)))
            throw new InvalidOperationException("Среди зависимых строк есть поля, которые локальная схема считает неизменяемыми. Удаление отменено.");

        IReadOnlyList<ReferenceReplacement> replacements = Array.Empty<ReferenceReplacement>();
        if (dependencies.Count > 0)
        {
            var options = new List<DependencyReplacementWindow.DependencyOption>();
            foreach (var dependency in dependencies)
            {
                var candidates = BuildOfflineReplacementCandidates(source, dependency, targetKey);
                if (candidates.Count == 0 || candidates.All(candidate => candidate.IsConflict))
                    throw new InvalidOperationException($"Для зависимости {dependency.Hit.SourceTable}.{dependency.Hit.SourceColumnDescription} нет безопасного значения для замены.");
                var firstTargetColumn = dependency.Relation.TargetColumns[0];
                options.Add(new DependencyReplacementWindow.DependencyOption
                {
                    Hit = dependency.Hit,
                    CurrentValue = targetKey.TryGetValue(firstTargetColumn, out var current) ? current : "",
                    Candidates = candidates
                });
            }
            var dialog = new DependencyReplacementWindow(options, requireExistingCandidates: true) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return false;
            replacements = dialog.Replacements;
            if (replacements.Count != dependencies.Count)
                throw new InvalidOperationException("Не удалось сопоставить варианты замены с зависимыми строками. Удаление отменено.");
        }

        IReadOnlyList<string> deleteKeyColumns = schema.HasStableKey ? schema.KeyColumns : Array.Empty<string>();
        var deleteKeyValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (schema.HasStableKey)
        {
            foreach (var column in deleteKeyColumns)
            {
                if (!TryReadProjectValue(targetRecord, schema, column, out var value))
                    throw new InvalidOperationException($"В снимке отсутствует ключевой столбец «{column}». Удаление отменено.");
                deleteKeyValues[column] = value;
            }
        }
        var targetIdentity = schema.HasStableKey
            ? deleteKeyValues.ToDictionary(pair => pair.Key, pair => pair.Value ?? DBNull.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["__ProjectRecordKey"] = targetRecord.Key };
        foreach (var pair in targetKey)
            if (!targetIdentity.ContainsKey(pair.Key)) targetIdentity[pair.Key] = pair.Value ?? DBNull.Value;

        if (expectedGeneration != _workspaceGeneration || !ReferenceEquals(source, _offlineSource)) return false;
        var commitTimer = System.Diagnostics.Stopwatch.StartNew();
        var before = SnapshotProjectRecord(targetRecord, schema);
        var relatedChanges = new List<ChangeJournalRowChange>();
        await source.ExecuteMutationTransactionAsync(async () =>
        {
            var selectedUpdates = dependencies.Select((dependency, index) => new { dependency, replacement = replacements[index] })
                .GroupBy(item => item.dependency.Record);
            foreach (var group in selectedUpdates)
            {
                var dependency = group.First().dependency;
                var dependencySchema = source.GetSchema(dependency.Hit.SourceTable)
                    ?? throw new InvalidOperationException($"Нет схемы зависимой таблицы «{dependency.Hit.SourceTable}».");
                var rowBefore = SnapshotProjectRecord(dependency.Record, dependencySchema);
                var update = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in group)
                {
                    for (var columnIndex = 0; columnIndex < item.replacement.SourceColumns.Count; columnIndex++)
                    {
                        var column = item.replacement.SourceColumns[columnIndex];
                        var value = item.replacement.NewValues[columnIndex];
                        if (update.TryGetValue(column, out var previous) && !ValuesEqual(previous, value))
                            throw new InvalidOperationException($"Для поля «{column}» выбраны разные замены. Удаление отменено.");
                        update[column] = value;
                    }
                    foreach (var pair in item.replacement.AdditionalAssignments)
                    {
                        if (update.TryGetValue(pair.Key, out var previous) && !ValuesEqual(previous, pair.Value))
                            throw new InvalidOperationException($"Для поля «{pair.Key}» выбраны разные замены. Удаление отменено.");
                        update[pair.Key] = pair.Value;
                    }
                }
                var rowAfter = new Dictionary<string, object>(rowBefore, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in update) rowAfter[pair.Key] = pair.Value ?? DBNull.Value;
                var oldIdentity = ReadStableIdentity(dependency.Record, dependencySchema);
                var newIdentity = IdentityFromValues(rowAfter, dependencySchema);
                if (oldIdentity.Count == 0 || newIdentity.Count == 0)
                    throw new InvalidOperationException($"Не удалось сохранить ключ зависимой строки «{dependency.Hit.SourceTable}» в журнале.");

                await source.UpdateRecordAsync(dependency.Hit.SourceTable, dependencySchema.KeyColumns,
                    dependency.Hit.SourceKey, update);
                relatedChanges.Add(new ChangeJournalRowChange
                {
                    TableName = dependency.Hit.SourceTable,
                    Kind = ChangeJournalOperationKind.Update,
                    Before = JournalValues(rowBefore),
                    After = JournalValues(rowAfter),
                    IdentityBefore = JournalValues(oldIdentity),
                    IdentityAfter = JournalValues(newIdentity)
                });
            }

            int deleted;
            if (schema.HasStableKey)
                deleted = await source.DeleteRecordsAsync(table, deleteKeyColumns,
                    new IReadOnlyDictionary<string, object?>[] { deleteKeyValues });
            else
                deleted = source.DeleteSnapshotRecord(table, targetRecord.Key) ? 1 : 0;
            if (deleted != 1)
                throw new InvalidOperationException("Удаление не затронуло ровно одну строку. Все изменения отменены.");
        });

        SessionTestLog.Trace("CRUD", $"operation=DeleteConfigurationValue; phase=committed; source=offline; table='{table}'; elapsedMs={commitTimer.ElapsedMilliseconds}");
        var description = $"Удалено значение {_configurationKind}: «{GetConfigurationKeyValue()}»";
        if (dependencies.Count > 0)
            description += $"; заменено ссылок: {dependencies.Count}";
        RecordMode3Mutation(table, ChangeJournalOperationKind.Delete, description, before, null!,
            targetIdentity, null!, expectedGeneration,
            relatedChanges: relatedChanges,
            journalOperationKind: dependencies.Count > 0 ? ChangeJournalOperationKind.Composite : ChangeJournalOperationKind.Delete);
        await RefreshConfigurationLookupsAsync();
        TxtStatus.Text = description;
        ShowActionFeedback(description);
        return true;
    }

    private List<DatabaseRelation> BuildOfflineDeletionRelations(OfflineProjectDataSource source, string targetTable)
    {
        var result = new List<DatabaseRelation>();
        var targetSchema = source.GetSchema(targetTable);
        if (targetSchema == null) return result;

        void Add(string sourceTableName, IEnumerable<string> sourceColumns, string targetTableName, IEnumerable<string> targetColumns,
            string rule, DatabaseRelationKind kind = DatabaseRelationKind.ConventionalLookup, RelationConfidence confidence = RelationConfidence.High)
        {
            var sourceColumnList = sourceColumns?.Where(column => !string.IsNullOrWhiteSpace(column)).ToList() ?? new List<string>();
            var targetColumnList = targetColumns?.Where(column => !string.IsNullOrWhiteSpace(column)).ToList() ?? new List<string>();
            if (string.IsNullOrWhiteSpace(sourceTableName) || string.IsNullOrWhiteSpace(targetTableName) ||
                sourceColumnList.Count == 0 || sourceColumnList.Count != targetColumnList.Count) return;
            string resolvedSource = source.ResolveTableName(sourceTableName);
            string resolvedTarget = source.ResolveTableName(targetTableName);
            var sourceSchema = source.GetSchema(resolvedSource);
            var resolvedTargetSchema = source.GetSchema(resolvedTarget);
            bool sourceColumnsExist = sourceColumnList.All(sourceColumn =>
                sourceSchema?.Columns.Any(column => column.Name.Equals(sourceColumn, StringComparison.OrdinalIgnoreCase)) == true ||
                source.GetRecords(resolvedSource).Any(record => record.Values.ContainsKey(sourceColumn)));
            if (!sourceColumnsExist || resolvedTargetSchema == null ||
                targetColumnList.Any(targetColumn => !resolvedTargetSchema.Columns.Any(column => column.Name.Equals(targetColumn, StringComparison.OrdinalIgnoreCase))) ||
                !resolvedTarget.Equals(targetTable, StringComparison.OrdinalIgnoreCase)) return;
            if (result.Any(relation => relation.SourceTable.Equals(resolvedSource, StringComparison.OrdinalIgnoreCase) &&
                relation.SourceColumns.SequenceEqual(sourceColumnList, StringComparer.OrdinalIgnoreCase) &&
                relation.TargetColumns.SequenceEqual(targetColumnList, StringComparer.OrdinalIgnoreCase))) return;
            var relation = new DatabaseRelation
            {
                SourceTable = resolvedSource,
                TargetTable = resolvedTarget,
                Kind = kind,
                Confidence = confidence,
                RuleId = rule,
                Comment = $"Значение {string.Join(", ", sourceColumnList)} использует запись из {resolvedTarget}.{string.Join(", ", targetColumnList)}."
            };
            relation.SourceColumns.AddRange(sourceColumnList);
            relation.TargetColumns.AddRange(targetColumnList);
            result.Add(relation);
        }

        // Captured relations are the same physical and inferred edges used by Online.
        // Keep composite keys intact so deletion checks every part of the relationship.
        var capturedSchema = DatabaseGraphRelationMaterializer.ReadCapturedSchema(source.Project.Architecture["physicalSchema"]);
        if (capturedSchema?.FindTable(targetTable) == null)
            throw new InvalidOperationException("Локальный снимок не содержит схему связей этой таблицы. Обновите снимок перед удалением.");
        foreach (var relation in capturedSchema.Relations.Where(relation =>
                     relation.TargetTable.Equals(targetTable, StringComparison.OrdinalIgnoreCase) &&
                     relation.Kind != DatabaseRelationKind.UnresolvedCandidate))
        {
            if (!relation.IsUsableForDeletionProtection)
                throw new InvalidOperationException($"Связь «{relation.SourceTable} → {targetTable}» не подтверждена. Удаление отменено.");
            Add(relation.SourceTable, relation.SourceColumns, relation.TargetTable, relation.TargetColumns,
                relation.RuleId, relation.Kind, relation.Confidence);
        }

        var graph = new GraphRelationStore(_offlineProject!).Load(strict: true);
        foreach (var manual in graph.ManualRelations ?? new List<GraphRelationDefinition>())
            Add(manual.Source, new[] { manual.SourceColumn }, manual.Target, new[] { manual.TargetColumn }, "project-manual");

        foreach (var definition in _architecture.Classes)
        {
            var storage = definition.PrimaryStorage?.TableName;
            if (string.IsNullOrWhiteSpace(storage)) continue;
            foreach (var field in _architecture.GetEffectiveFields(definition))
                if (!string.IsNullOrWhiteSpace(field.LookupTable) && !string.IsNullOrWhiteSpace(field.LookupKeyField))
                    Add(storage, new[] { field.Name }, field.LookupTable, new[] { field.LookupKeyField }, "class-field-lookup");
        }

        var targetLeaf = targetTable.Split('.').Last().Trim('[', ']');
        if (targetLeaf.Equals("PLC", StringComparison.OrdinalIgnoreCase) || targetLeaf.Equals("PLCs", StringComparison.OrdinalIgnoreCase))
        {
            var targetColumn = targetSchema.Columns.FirstOrDefault(column => column.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name
                ?? targetSchema.KeyColumns.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(targetColumn))
            {
                foreach (var sourceSchema in source.GetTables())
                {
                    if (sourceSchema.TableName.Equals(targetTable, StringComparison.OrdinalIgnoreCase)) continue;
                    Add(sourceSchema.TableName, new[] { "PLC" }, targetTable, new[] { targetColumn }, "plc-record");
                    if (sourceSchema.TableName.Split('.').Last().Equals("PLC_CFG", StringComparison.OrdinalIgnoreCase))
                        Add(sourceSchema.TableName, new[] { "PLC_Number" }, targetTable, new[] { targetColumn }, "plc-config-number");
                }
            }
        }
        else if (targetLeaf.Equals("Area", StringComparison.OrdinalIgnoreCase) || targetLeaf.Equals("Areas", StringComparison.OrdinalIgnoreCase))
        {
            var targetColumn = targetSchema.Columns.FirstOrDefault(column => column.Name.Equals("AreaName", StringComparison.OrdinalIgnoreCase))?.Name;
            if (!string.IsNullOrWhiteSpace(targetColumn))
                foreach (var sourceSchema in source.GetTables())
                {
                    if (sourceSchema.TableName.Equals(targetTable, StringComparison.OrdinalIgnoreCase)) continue;
                    Add(sourceSchema.TableName, new[] { "Area" }, targetTable, new[] { targetColumn }, "area-name");
                }
        }
        return result;
    }

    private List<OfflineDependency> FindOfflineDependents(OfflineProjectDataSource source, ProjectRecord target,
        IReadOnlyDictionary<string, object> targetKey, IEnumerable<DatabaseRelation> relations)
    {
        var result = new List<OfflineDependency>();
        foreach (var relation in relations)
        {
            if (relation.SourceColumns.Count == 0 || relation.SourceColumns.Count != relation.TargetColumns.Count ||
                relation.TargetColumns.Any(column => !targetKey.ContainsKey(column) || targetKey[column] == null || targetKey[column] == DBNull.Value)) continue;
            var sourceSchema = source.GetSchema(relation.SourceTable);
            var matching = source.GetRecords(relation.SourceTable).Where(record => !ReferenceEquals(record, target) &&
                relation.SourceColumns.Select((column, index) =>
                {
                    if (sourceSchema != null)
                        return TryReadProjectValue(record, sourceSchema, column, out var current) &&
                            ValuesEqual(current, targetKey[relation.TargetColumns[index]]);
                    if (!record.Values.TryGetPropertyValue(column, out var node)) return false;
                    return ValuesEqual(node?.ToString(), targetKey[relation.TargetColumns[index]]);
                }).All(matches => matches));

            foreach (var record in matching)
            {
                var sourceKey = sourceSchema == null ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    : ReadStableIdentity(record, sourceSchema);
                if (sourceSchema == null || sourceKey.Count != sourceSchema.KeyColumns.Count || !sourceSchema.HasStableKey)
                    sourceKey.Clear();
                result.Add(new OfflineDependency
                {
                    Record = record,
                    Relation = relation,
                    Hit = new DependencyHit
                    {
                        SourceTable = relation.SourceTable,
                        SourceKey = sourceKey,
                        SourceColumnDescription = string.Join(", ", relation.SourceColumns),
                        TargetTable = relation.TargetTable,
                        Relation = relation
                    }
                });
            }
        }
        return result;
    }

    private bool CanReplaceOfflineDependency(OfflineDependency dependency)
    {
        var schema = _offlineSource?.GetSchema(dependency.Hit.SourceTable);
        return schema != null && dependency.Relation.SourceColumns.All(name =>
        {
            var column = schema.Columns.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return column != null && !column.IsReadOnly && !column.IsIdentity && !column.IsComputed;
        });
    }

    private List<ReplacementCandidate> BuildOfflineReplacementCandidates(OfflineProjectDataSource source,
        OfflineDependency dependency, IReadOnlyDictionary<string, object> targetKey)
    {
        var sourceSchema = source.GetSchema(dependency.Hit.SourceTable)!;
        var targetSchema = source.GetSchema(dependency.Relation.TargetTable)!;
        var targetRecords = source.GetRecords(dependency.Relation.TargetTable);
        var collected = new List<(string Token, ReplacementCandidate Candidate)>();
        foreach (var record in targetRecords)
        {
            var values = new List<object>();
            var tokenParts = new List<string>();
            bool usable = true;
            for (var index = 0; index < dependency.Relation.TargetColumns.Count; index++)
            {
                var targetColumn = dependency.Relation.TargetColumns[index];
                var sourceColumn = dependency.Relation.SourceColumns[index];
                if (!TryReadProjectValue(record, targetSchema, targetColumn, out var targetValue) ||
                    targetValue == DBNull.Value || targetValue == null ||
                    !TryReadProjectValue(record, targetSchema, targetColumn, out var rawValue))
                {
                    usable = false;
                    break;
                }
                var sourceType = sourceSchema.Columns.First(column => column.Name.Equals(sourceColumn, StringComparison.OrdinalIgnoreCase)).DataType;
                if (!TryConvertValue(rawValue, sourceType, out var assignment)) { usable = false; break; }
                values.Add(assignment);
                tokenParts.Add(NormalizeValue(assignment));
            }
            if (!usable || values.Select((value, index) => ValuesEqual(value, targetKey[dependency.Relation.TargetColumns[index]])).All(matches => matches))
                continue;

            var display = FormatOfflineLookupCandidate(record, targetSchema, dependency.Relation.TargetColumns);
            var candidate = new ReplacementCandidate
            {
                Values = values,
                Display = display,
                IsConflict = WouldConflictWithOfflineKey(source, dependency, values, out var conflict),
                ConflictDescription = conflict
            };
            collected.Add((string.Join("\u001f", tokenParts), candidate));
        }

        // If a logical lookup value is duplicated in the snapshot, its replacement label
        // cannot identify one unambiguous target. Do not offer it as a safe candidate.
        var duplicates = collected.GroupBy(item => item.Token, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return collected.Where(item => !duplicates.Contains(item.Token)).Select(item => item.Candidate).ToList();
    }

    private bool WouldConflictWithOfflineKey(OfflineProjectDataSource source, OfflineDependency dependency,
        IReadOnlyList<object> values, out string description)
    {
        description = string.Empty;
        var schema = source.GetSchema(dependency.Hit.SourceTable);
        if (schema == null || !schema.HasStableKey || schema.KeyColumns.Count == 0 ||
            !schema.KeyColumns.Intersect(dependency.Relation.SourceColumns, StringComparer.OrdinalIgnoreCase).Any()) return false;
        var projected = (JsonObject)dependency.Record.Values.DeepClone();
        for (var index = 0; index < dependency.Relation.SourceColumns.Count; index++)
            projected[dependency.Relation.SourceColumns[index]] = ProjectKeyCodec.ToNode(values[index]);
        var key = ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => projected[name]);
        var collision = source.GetRecords(dependency.Hit.SourceTable).FirstOrDefault(record =>
            !ReferenceEquals(record, dependency.Record) &&
            string.Equals(ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => record.Values[name]), key, StringComparison.Ordinal));
        if (collision == null) return false;
        description = "такая замена создаст дубликат ключа зависимой строки";
        return true;
    }

    private static string FormatOfflineLookupCandidate(ProjectRecord record, ProjectTableSchema schema, IReadOnlyList<string> lookupColumns)
    {
        string Get(string name) => TryReadProjectValue(record, schema, name, out var value) && value != DBNull.Value
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        var leaf = schema.TableName.Split('.').Last().Trim('[', ']');
        var key = string.Join(" · ", lookupColumns.Select(Get));
        if (leaf.Equals("Area", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Areas", StringComparison.OrdinalIgnoreCase))
        {
            var number = Get("Record");
            var name = new[] { "AreaNameL2", "AreaNameL1", "AreaName" }.Select(Get).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(number)) return string.IsNullOrWhiteSpace(name) ? number : $"{number}. {name}";
        }
        else
        {
            var name = new[] { "NameL1", "Name", "DescriptionL1", "NameL2" }.Select(Get).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(name)) return string.IsNullOrWhiteSpace(key) ? name : $"{key} · {name}";
        }
        return key;
    }

    private static Dictionary<string, object> SnapshotProjectRecord(ProjectRecord record, ProjectTableSchema schema)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in record.Values)
        {
            var column = schema.Columns.FirstOrDefault(candidate => candidate.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            values[pair.Key] = column != null && TryConvertValue(pair.Value, column.DataType, out var value)
                ? value : pair.Value == null ? DBNull.Value : pair.Value.ToString() ?? string.Empty;
        }
        return values;
    }

    private static Dictionary<string, object> ReadStableIdentity(ProjectRecord record, ProjectTableSchema schema)
    {
        var identity = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!schema.HasStableKey) return identity;
        foreach (var column in schema.KeyColumns)
            if (TryReadProjectValue(record, schema, column, out var value)) identity[column] = value;
        return identity;
    }

    private static Dictionary<string, object> IdentityFromValues(IReadOnlyDictionary<string, object> values, ProjectTableSchema schema)
        => schema.HasStableKey
            ? schema.KeyColumns.Where(values.ContainsKey).ToDictionary(column => column, column => values[column], StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    private static bool TryReadProjectValue(ProjectRecord record, ProjectTableSchema schema, string column, out object value)
    {
        value = DBNull.Value;
        if (!record.Values.TryGetPropertyValue(column, out var node)) return false;
        var columnSchema = schema.Columns.FirstOrDefault(candidate => candidate.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
        if (node == null) return true;
        if (columnSchema != null && TryConvertSnapshotValue(node, columnSchema.DataType, out value)) return true;
        value = node.ToString();
        return true;
    }

    internal static bool TryConvertSnapshotValue(JsonNode? node, string dataType, out object value)
    {
        value = DBNull.Value;
        if (node == null) return true;
        return TryConvertValue(node.ToString(), dataType, out value);
    }

    private static bool TryConvertValue(object? rawValue, string dataType, out object value)
    {
        value = DBNull.Value;
        if (rawValue == null || rawValue == DBNull.Value) return true;
        var raw = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
        try
        {
            value = dataType.ToLowerInvariant() switch
            {
                "bit" => raw == "1" ? true : raw == "0" ? false : bool.Parse(raw),
                "tinyint" => byte.Parse(raw, CultureInfo.InvariantCulture),
                "smallint" => short.Parse(raw, CultureInfo.InvariantCulture),
                "int" => int.Parse(raw, CultureInfo.InvariantCulture),
                "bigint" => long.Parse(raw, CultureInfo.InvariantCulture),
                "decimal" or "numeric" or "money" or "smallmoney" => decimal.Parse(raw, CultureInfo.InvariantCulture),
                "float" or "real" => double.Parse(raw, CultureInfo.InvariantCulture),
                "date" or "datetime" or "datetime2" or "smalldatetime" => DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "datetimeoffset" => DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture),
                "uniqueidentifier" => Guid.Parse(raw),
                "binary" or "varbinary" or "image" => Convert.FromBase64String(raw),
                _ => raw
            };
            return true;
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left == null || left == DBNull.Value) return right == null || right == DBNull.Value;
        if (right == null || right == DBNull.Value) return false;
        return string.Equals(NormalizeValue(left), NormalizeValue(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeValue(object value)
        => Convert.ToString(value == DBNull.Value ? null : value, CultureInfo.InvariantCulture) ?? string.Empty;

    private sealed class OfflineDependency
    {
        public ProjectRecord Record { get; init; } = new();
        public DatabaseRelation Relation { get; init; } = new();
        public DependencyHit Hit { get; init; } = new();
    }
}
