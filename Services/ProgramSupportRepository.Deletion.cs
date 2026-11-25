#nullable enable
using System.Data;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;

namespace Configurator;

internal sealed record ProgramSupportDeletionPlan(
    ProgramSupportRepository Owner,
    IReadOnlyDictionary<string, object> Source,
    IReadOnlyList<DependencyReplacementWindow.DependencyOption> Options);

public sealed partial class ProgramSupportRepository
{
    private sealed record Dependency(string Table, string[] Columns, string[] ParentColumns);

    private static Dependency[] KnownDependencies(string table) => table switch
    {
        "dbo.Prog_Recipe" => [new("dbo.Prog_Recipes",
            ["PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Recipe_Record"],
            ["PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Record"])],
        "dbo.Prog_Queue" => [new("dbo.Prog_QueueSelections",
            ["PLC", "PLC_Class_Prog_Number", "Prog_Record", "Queue_Record"],
            ["PLC", "PLC_Class_Prog_Number", "Prog_Record", "Record"])],
        "dbo.Matrix_List" =>
        [
            MatrixChild("dbo.Matrix_CommandList"), MatrixChild("dbo.Matrix_Commands"), MatrixChild("dbo.Matrix_DeviceList"),
            MatrixChild("dbo.Matrix_DeviceListType"), MatrixChild("dbo.Matrix_Devices"),
            new("dbo.Prog_Queue", ["PLC", "PLC_Class_Matrix_Number", "Matrix_Record"], ["PLC", "PLC_Class_Number", "Record"]),
            new("dbo.Prog_QueueSelections", ["PLC", "PLC_Class_Matrix_Number", "Matrix_Record"], ["PLC", "PLC_Class_Number", "Record"])
        ],
        "dbo.Matrix_CommandList" => [MatrixChild("dbo.Matrix_Commands", "CmdNumber")],
        "dbo.Matrix_DeviceList" =>
        [
            MatrixChild("dbo.Matrix_Devices", "DeviceList"),
            new("dbo.Prog_QueueSelections", ["PLC", "PLC_Class_Matrix_Number", "Matrix_Record", "Matrix_DeviceList"],
                ["PLC", "PLC_Class_Number", "Record", "DeviceList"])
        ],
        "dbo.Matrix_DeviceListType" => [MatrixChild("dbo.Matrix_Devices", "Position")],
        _ => []
    };

    private static Dependency MatrixChild(string table, string? field = null)
    {
        string[] columns = field == null ? ["PLC", "PLC_Class_Number", "Record"] : ["PLC", "PLC_Class_Number", "Record", field];
        return new Dependency(table, columns, columns);
    }

    internal async Task<ProgramSupportDeletionPlan> PrepareDeletionReplacementAsync(ProgramSupportTableData tableData,
        DataRow row, IReadOnlyDictionary<string, object> scope)
    {
        if (!tableData.CanWrite || !tableData.Definition.AllowDelete ||
            !ReferenceEquals(row.Table, tableData.Rows) || !row.HasVersion(DataRowVersion.Original))
            throw new InvalidOperationException("Строка недоступна для удаления.");
        var source = DeletionSnapshot(row);
        var dependencies = KnownDependencies(tableData.Definition.TableName);
        if (dependencies.Length == 0) return new(this, source, []);
        using var connection = _offline == null ? SqlProviderGuard.CreateConnection(_connectionString) : null;
        if (connection != null) await SqlProviderGuard.OpenConnectionAsync(connection);
        var definition = tableData.Definition;
        var matches = new List<(Dependency Dependency, ProjectTableSchema Schema, Dictionary<string, object> Row)>();

        foreach (var dependency in dependencies)
        {
            var schema = await DeletionSchemaAsync(dependency.Table, connection, null);
            foreach (var item in await ReadDependentRowsAsync(dependency, source, schema, connection, null))
            {
                if (schema?.HasStableKey != true || schema.KeyColumns.Count == 0)
                    throw new InvalidOperationException($"В таблице {dependency.Table} отсутствует подтверждённый ключ для замены.");
                matches.Add((dependency, schema, item));
            }
        }
        if (matches.Count == 0) return new(this, source, []);

        var sourceScope = Pick(source, definition.ScopeColumns);
        if (sourceScope.Any(pair => !scope.TryGetValue(pair.Key, out var value) ||
                !Equals(Convert.ChangeType(value, pair.Value.GetType(), System.Globalization.CultureInfo.InvariantCulture), pair.Value)))
            throw new InvalidOperationException("Исходная строка относится к другой области редактирования.");
        var roots = await ReadDeletionRowsAsync(definition.TableName, sourceScope,
            await DeletionSchemaAsync(definition.TableName, connection, null), connection, null);
        var sourceKey = Pick(source, definition.KeyColumns);
        var removedKeys = tableData.Rows.Rows.Cast<DataRow>().Where(item => item.RowState == DataRowState.Deleted)
            .Select(item => Pick(DeletionSnapshot(item), definition.KeyColumns)).ToArray();
        var candidates = new List<IReadOnlyDictionary<string, object>>();
        string? blockedCandidate = null;
        foreach (var candidate in roots.Where(item => !Matches(item, sourceKey) && !removedKeys.Any(key => Matches(item, key))))
        {
            if (!CompatibleMatrixPosition(definition.TableName, source, candidate))
            {
                blockedCandidate ??= "Для назначенных объектов нужен тот же класс устройств.";
                continue;
            }

            bool collision = false;
            foreach (var match in matches)
            {
                if (!match.Schema.KeyColumns.Intersect(match.Dependency.Columns, StringComparer.OrdinalIgnoreCase).Any()) continue;
                var oldKey = Pick(match.Row, match.Schema.KeyColumns);
                var newKey = Pick(ApplyAssignments(match.Dependency, match.Row, candidate), match.Schema.KeyColumns);
                if (!Matches(newKey, oldKey) && (await ReadDeletionRowsAsync(match.Dependency.Table, newKey, match.Schema, connection, null)).Count != 0)
                {
                    blockedCandidate = $"В {match.Dependency.Table} уже есть строка с таким составным ключом.";
                    collision = true;
                    break;
                }
            }
            if (!collision) candidates.Add(candidate);
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException("Есть зависимые записи. В текущей области нет доступной замены. " + blockedCandidate);
        var options = matches.Select(match =>
        {
            var mapping = match.Dependency;
            var relation = new DatabaseRelation { SourceTable = mapping.Table, TargetTable = definition.TableName, Confidence = RelationConfidence.Confirmed };
            relation.SourceColumns.AddRange(mapping.Columns);
            relation.TargetColumns.AddRange(mapping.ParentColumns);
            return new DependencyReplacementWindow.DependencyOption
            {
                Hit = new DependencyHit
                {
                    SourceTable = mapping.Table, TargetTable = definition.TableName, Relation = relation,
                    SourceKey = Pick(match.Row, match.Schema.KeyColumns), SourceValues = match.Row,
                    SourceReferenceValues = Pick(match.Row, mapping.Columns),
                    TargetName = DependencyRecordLabel.Name(source),
                    TargetKey = sourceKey
                },
                Candidates = candidates.Select(candidate => new ReplacementCandidate
                {
                    Values = mapping.ParentColumns.Select(column => candidate[column]).ToArray(),
                    TargetValues = candidate,
                    Display = DependencyRecordLabel.Name(candidate),
                    DisplayKey = Pick(candidate, definition.KeyColumns)
                }).ToArray()
            };
        }).ToArray();
        return new(this, source, options);
    }

    internal void StageDeletionReplacement(ProgramSupportTableData tableData, DataRow row,
        ProgramSupportDeletionPlan plan, IReadOnlyList<ReferenceReplacement> replacements)
    {
        if (!ReferenceEquals(this, plan.Owner) || !ReferenceEquals(tableData.Rows, row.Table) ||
            !SameSnapshot(DeletionSnapshot(row), plan.Source))
            throw new InvalidOperationException("Строка для удаления изменилась. Повторите выбор.");
        if (plan.Options.Count == 0 && replacements.Count == 0) return;
        if (plan.Options.Count != replacements.Count)
            throw new InvalidOperationException("Нужна подтверждённая замена для каждой зависимой строки.");
        IReadOnlyDictionary<string, object>? replacement = null;
        foreach (var option in plan.Options)
        {
            var match = replacements.SingleOrDefault(item => item.SourceTable == option.Hit.SourceTable &&
                item.SourceColumns.SequenceEqual(option.Hit.Relation.SourceColumns, StringComparer.OrdinalIgnoreCase) &&
                item.KeyColumns.SequenceEqual(option.Hit.SourceKey.Keys, StringComparer.OrdinalIgnoreCase) &&
                item.KeyValues.SequenceEqual(option.Hit.SourceKey.Values));
            var candidate = option.Candidates.SingleOrDefault(item => matchesCandidate(item, match));
            if (candidate == null || (replacement != null && !Matches(replacement, candidate.TargetValues)))
                throw new InvalidOperationException("Для всех зависимых строк выберите одну и ту же сохранённую запись.");
            replacement = candidate.TargetValues;
        }

        tableData.StagedDeletions[row] = (plan, replacement!);
        static bool matchesCandidate(ReplacementCandidate candidate, ReferenceReplacement? match) =>
            match != null && Matches(candidate.TargetValues, match.TargetValues) && candidate.Values.SequenceEqual(match.NewValues);
    }

    internal static bool HasStagedDeletionReplacements(ProgramSupportTableData tableData) =>
        tableData.StagedDeletions.Keys.Any(row => row.RowState == DataRowState.Deleted);

    private async Task ApplyStagedDeletionReplacementAsync(ProgramSupportTableData tableData, DataRow row,
        List<ChangeJournalRowChange> journal, SqlConnection? connection, SqlTransaction? transaction)
    {
        if (!tableData.StagedDeletions.TryGetValue(row, out var staged)) return;
        var (plan, replacement) = staged;
        if (!ReferenceEquals(this, plan.Owner))
            throw new InvalidOperationException("Подтверждение удаления относится к другому источнику.");
        var definition = tableData.Definition;
        var rootSchema = await DeletionSchemaAsync(definition.TableName, connection, transaction);
        await VerifyRootAsync(plan.Source);
        await VerifyRootAsync(replacement);
        if (!CompatibleMatrixPosition(definition.TableName, plan.Source, replacement))
            throw new InvalidOperationException("Для назначенных объектов нужен тот же класс устройств.");

        if (connection != null)
            await ProgramManagementRepository.EnsureSafeReplacementSqlAsync(connection, transaction!, definition.TableName, CancellationToken.None);

        foreach (var dependency in KnownDependencies(definition.TableName))
        {
            var schema = await DeletionSchemaAsync(dependency.Table, connection, transaction);
            var rows = await ReadDependentRowsAsync(dependency, plan.Source, schema, connection, transaction);
            var options = plan.Options.Where(item => item.Hit.SourceTable == dependency.Table &&
                item.Hit.Relation.SourceColumns.SequenceEqual(dependency.Columns, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (rows.Count != options.Length || options.Any(option => rows.Count(item => SameSnapshot(item, option.Hit.SourceValues)) != 1))
                throw new InvalidOperationException($"Зависимости в {dependency.Table} изменились. Повторите выбор замены.");
            if (options.Length == 0) continue;
            if (schema?.HasStableKey != true || schema.KeyColumns.Count == 0)
                throw new InvalidOperationException($"Не подтверждён ключ {dependency.Table}.");
            ProjectWriteGuard.EnsureDataTableWritable(dependency.Table);
            var assignments = dependency.Columns.Select((column, index) => (column, replacement[dependency.ParentColumns[index]]))
                .Where(pair => !ValuesEqual(pair.Item2, rows[0][pair.column]))
                .ToDictionary(pair => pair.column, pair => pair.Item2, StringComparer.OrdinalIgnoreCase);
            if (assignments.Count == 0) throw new InvalidOperationException("Ссылка для замены не изменилась.");
            if (connection != null)
                await ProgramManagementRepository.EnsureSafeReplacementSqlAsync(connection, transaction!, dependency.Table, CancellationToken.None);
            foreach (var before in rows)
            {
                var oldKey = Pick(before, schema.KeyColumns);
                var after = ApplyAssignments(dependency, before, replacement);
                foreach (var column in assignments.Keys)
                {
                    var metadata = schema.Columns.Single(item => item.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
                    if (metadata.IsIdentity || metadata.IsComputed || metadata.IsReadOnly)
                        throw new InvalidOperationException($"Поле {dependency.Table}.{column} доступно только для чтения.");
                }
                var newKey = Pick(after, schema.KeyColumns);
                if (!Matches(oldKey, newKey) && (await ReadDeletionRowsAsync(dependency.Table, newKey, schema, connection, transaction)).Count != 0)
                    throw new InvalidOperationException($"В {dependency.Table} уже есть строка с таким составным ключом. Перенос отменён.");
                var values = Pick(after, assignments.Keys);
                if (_offline != null)
                    await _offline.UpdateRecordAsync(dependency.Table, schema.KeyColumns, NullableValues(oldKey), NullableValues(values));
                else
                {
                    using var command = new SqlCommand { Connection = connection, Transaction = transaction };
                    var set = string.Join(", ", values.Select((pair, i) =>
                    {
                        command.Parameters.AddWithValue("@set" + i, pair.Value);
                        return $"{Quote(pair.Key)}=@set{i}";
                    }));
                    var where = AddDeletionPredicates(command, oldKey);
                    command.CommandText = $"UPDATE {QuoteTable(dependency.Table)} SET {set} WHERE {where};";
                    if (await command.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException($"Не удалось заменить ссылку в {dependency.Table}.");
                }
                var actual = await ReadDeletionRowsAsync(dependency.Table, newKey, schema, connection, transaction);
                if (actual.Count != 1 || !SameSnapshot(actual[0], after))
                    throw new InvalidOperationException($"Перенос записи {dependency.Table} не подтверждён.");
                journal.Add(new ChangeJournalRowChange
                {
                    TableName = dependency.Table, Kind = ChangeJournalOperationKind.Update,
                    Before = JournalDeletionValues(before), After = JournalDeletionValues(after),
                    IdentityBefore = JournalDeletionValues(oldKey), IdentityAfter = JournalDeletionValues(newKey)
                });
            }
        }

        if (_offline != null)
            OfflineObjectDeletionGuard.Check(_offline, definition.TableName, new[] { NullableValues(plan.Source) });

        async Task VerifyRootAsync(IReadOnlyDictionary<string, object> expected)
        {
            var key = Pick(expected, definition.KeyColumns);
            var actual = await ReadDeletionRowsAsync(definition.TableName, key, rootSchema, connection, transaction);
            if (actual.Count != 1 || !SameSnapshot(actual[0], expected))
                throw new InvalidOperationException("Исходная строка или выбранная замена изменилась. Повторите выбор замены.");
        }
    }

    private static bool CompatibleMatrixPosition(string table, IReadOnlyDictionary<string, object> source,
        IReadOnlyDictionary<string, object> replacement) =>
        table != "dbo.Matrix_DeviceListType" ||
        (source.TryGetValue("Device_Global_Class", out var sourceClass) && sourceClass != DBNull.Value &&
            replacement.TryGetValue("Device_Global_Class", out var replacementClass) && ValuesEqual(sourceClass, replacementClass));

    private static Dictionary<string, object> ApplyAssignments(Dependency dependency,
        IReadOnlyDictionary<string, object> before, IReadOnlyDictionary<string, object> replacement)
    {
        var after = new Dictionary<string, object>(before, StringComparer.OrdinalIgnoreCase);
        foreach (var (column, index) in dependency.Columns.Select((name, index) => (name, index)))
            after[column] = Convert.ChangeType(replacement[dependency.ParentColumns[index]], before[column].GetType(),
                System.Globalization.CultureInfo.InvariantCulture);
        return after;
    }

    private async Task<ProjectTableSchema?> DeletionSchemaAsync(string table, SqlConnection? connection, SqlTransaction? transaction) =>
        _offline != null ? _offline.GetSchema(table) :
            await OnlineProjectDataSource.ReadSchemaAsync(connection!, transaction, table, CancellationToken.None);

    private Task<IReadOnlyList<Dictionary<string, object>>> ReadDependentRowsAsync(Dependency dependency,
        IReadOnlyDictionary<string, object> source, ProjectTableSchema? schema, SqlConnection? connection, SqlTransaction? transaction)
    {
        if (schema != null && dependency.Columns.Any(name => !schema.Columns.Any(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"В {dependency.Table} не подтверждены поля зависимости.");
        var key = dependency.Columns.Select((column, i) => (column, value: source[dependency.ParentColumns[i]]))
            .ToDictionary(pair => pair.column, pair => pair.value, StringComparer.OrdinalIgnoreCase);
        return ReadDeletionRowsAsync(dependency.Table, key, schema, connection, transaction);
    }

    private async Task<IReadOnlyList<Dictionary<string, object>>> ReadDeletionRowsAsync(string table,
        IReadOnlyDictionary<string, object> key, ProjectTableSchema? schema, SqlConnection? connection, SqlTransaction? transaction)
    {
        if (schema == null) return [];
        if (_offline != null)
            return _offline.GetRecords(table).Where(row => key.All(pair =>
                    row.Values.TryGetPropertyValue(pair.Key, out var value) && OfflineEquals(value, pair.Value)))
                .Select(row => schema.Columns.ToDictionary(column => column.Name,
                    column => OfflineValue(row.Values[column.Name], column.DataType), StringComparer.OrdinalIgnoreCase)).ToArray();
        using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        var where = AddDeletionPredicates(command, key);
        var locking = transaction == null ? string.Empty : " WITH (UPDLOCK,HOLDLOCK)";
        command.CommandText = $"SELECT * FROM {QuoteTable(table)}{locking} WHERE {where};";
        using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, reader.GetValue, StringComparer.OrdinalIgnoreCase));
        return rows;
    }

    private static string AddDeletionPredicates(SqlCommand command, IReadOnlyDictionary<string, object> key) =>
        string.Join(" AND ", key.Select((pair, i) =>
        {
            if (pair.Value == DBNull.Value) return $"{Quote(pair.Key)} IS NULL";
            command.Parameters.AddWithValue("@key" + i, pair.Value);
            return $"{Quote(pair.Key)}=@key{i}";
        }));

    private static Dictionary<string, object> DeletionSnapshot(DataRow row) =>
        row.Table.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName,
            column => row[column, DataRowVersion.Original] is byte[] bytes ? (object)bytes.ToArray() : row[column, DataRowVersion.Original],
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object> Pick(IReadOnlyDictionary<string, object> row, IEnumerable<string> columns) =>
        columns.ToDictionary(column => column, column => row[column], StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> NullableValues(IReadOnlyDictionary<string, object> source) =>
        source.ToDictionary(pair => pair.Key, pair => pair.Value == DBNull.Value ? null : pair.Value, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, ChangeJournalValue> JournalDeletionValues(IReadOnlyDictionary<string, object> source) =>
        source.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase);

    private static bool SameSnapshot(IReadOnlyDictionary<string, object> row, IReadOnlyDictionary<string, object> expected) =>
        row.Count == expected.Count && Matches(row, expected);

    private static bool Matches(IReadOnlyDictionary<string, object> row, IReadOnlyDictionary<string, object> expected) =>
        expected.All(pair => row.TryGetValue(pair.Key, out var value) &&
            (value is byte[] bytes && pair.Value is byte[] other ? bytes.SequenceEqual(other) : ValuesEqual(value, pair.Value)));
}
