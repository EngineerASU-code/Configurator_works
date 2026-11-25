#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Configurator.Core.Workspace;
using Microsoft.Data.SqlClient;

namespace Configurator;

public sealed partial class ProgramSupportRepository
{
    private readonly string _connectionString;
    private readonly OfflineProjectDataSource? _offline;

    public ProgramSupportRepository(string connectionString) =>
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Не задана строка подключения.", nameof(connectionString))
            : connectionString;

    public ProgramSupportRepository(OfflineProjectDataSource offline)
    {
        _offline = offline ?? throw new ArgumentNullException(nameof(offline));
        _connectionString = string.Empty;
    }

    public Task<ProgramSupportTableData> LoadProgramAsync(string tableId, ProgramContext context) =>
        LoadAsync(Find(ProgramSupportCatalog.ProgramTables, tableId), GetProgramScope(Find(ProgramSupportCatalog.ProgramTables, tableId), context));

    public Task<ProgramSupportTableData> LoadRecipeValuesAsync(ProgramContext context, int recipeRecord)
    {
        var definition = Find(ProgramSupportCatalog.ProgramTables, "recipe-values");
        return LoadAsync(definition, GetProgramScope(definition, context), recipeRecord);
    }

    public Task<ProgramSupportTableData> LoadMatrixAsync(string tableId, object plcValue, object matrixClassValue, object? matrixRecord = null)
    {
        var definition = Find(ProgramSupportCatalog.MatrixTables, tableId);
        var scope = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLC"] = Required(plcValue, "PLC"),
            ["PLC_Class_Number"] = Required(matrixClassValue, "Класс Matrix")
        };
        if (definition.ScopeColumns.Contains("Record", StringComparer.OrdinalIgnoreCase))
            scope["Record"] = Required(matrixRecord, "Матрица");
        return LoadAsync(definition, scope);
    }

    public async Task ValidateMatrixDeviceReferencesAsync(DataTable groups, DataTable positionTypes, DataTable assignments,
        IReadOnlyList<MatrixDeviceClassOption> classes)
    {
        var groupNumbers = groups.Rows.Cast<DataRow>()
            .Where(row => row.RowState != DataRowState.Deleted)
            .Select(row => Convert.ToInt32(row["DeviceList"])).ToHashSet();
        foreach (DataRow row in assignments.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            int groupNumber = Convert.ToInt32(row["DeviceList"]);
            if (!groupNumbers.Contains(groupNumber))
                throw new InvalidOperationException($"Назначение объекта ссылается на отсутствующую группу устройств {groupNumber}.");
        }

        var classesByNumber = classes.Where(item => !string.IsNullOrWhiteSpace(item.TableName))
            .GroupBy(item => item.Number).ToDictionary(group => group.Key, group => group.First());
        var classByPosition = new Dictionary<int, int>();
        foreach (DataRow row in positionTypes.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            int position = Convert.ToInt32(row["Position"]);
            if (row["Device_Global_Class"] == DBNull.Value) continue;
            int classNumber = Convert.ToInt32(row["Device_Global_Class"]);
            if (!classByPosition.TryAdd(position, classNumber) && classByPosition[position] != classNumber)
                throw new InvalidOperationException($"Для позиции {position} указано несколько классов устройств.");
        }

        if (_offline != null)
        {
            var classesByNumberOffline = classes.Where(item => !string.IsNullOrWhiteSpace(item.TableName))
                .GroupBy(item => item.Number).ToDictionary(group => group.Key, group => group.First());
            foreach (DataRow row in assignments.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                int position = Convert.ToInt32(row["Position"]);
                int deviceClass = Convert.ToInt32(row["Device_PLC_Class_Number"]);
                int devicePlc = Convert.ToInt32(row["Device_PLC"]);
                int deviceRecord = Convert.ToInt32(row["Device_Record"]);
                if (!classByPosition.TryGetValue(position, out int expectedClass) || expectedClass != deviceClass)
                    throw new InvalidOperationException($"Объект в позиции {position} не соответствует выбранному классу устройств.");
                if (!classesByNumberOffline.TryGetValue(expectedClass, out var option))
                    throw new InvalidOperationException($"Класс устройств {expectedClass} отсутствует в локальном снимке.");
                if (!_offline.GetRecords(option.TableName).Any(record => OfflineNumber(record, "PLC") == devicePlc && OfflineNumber(record, "Record") == deviceRecord))
                    throw new InvalidOperationException($"Объект PLC={devicePlc}, класс={deviceClass}, Record={deviceRecord} не найден в локальном проекте.");
            }
            return;
        }

        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
        var checkedTables = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        var checkedReferences = new HashSet<(string Table, int Plc, int Record)>();
        foreach (DataRow row in assignments.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            int position = Convert.ToInt32(row["Position"]);
            if (!classByPosition.TryGetValue(position, out int expectedClass))
                throw new InvalidOperationException($"Для позиции {position} не настроен тип устройства.");
            if (row["Device_PLC"] == DBNull.Value || row["Device_PLC_Class_Number"] == DBNull.Value || row["Device_Record"] == DBNull.Value)
                throw new InvalidOperationException($"Ссылка объекта в позиции {position} заполнена не полностью.");
            int deviceClass = Convert.ToInt32(row["Device_PLC_Class_Number"]);
            int devicePlc = Convert.ToInt32(row["Device_PLC"]);
            int deviceRecord = Convert.ToInt32(row["Device_Record"]);
            if (deviceClass != expectedClass)
                throw new InvalidOperationException($"Объект в позиции {position} не соответствует выбранному классу устройств.");
            if (!classesByNumber.TryGetValue(expectedClass, out var classOption))
                throw new InvalidOperationException($"Класс устройств {expectedClass} недоступен в текущем проекте или не имеет основной таблицы.");
            if (!checkedTables.TryGetValue(classOption.TableName, out var metadata))
            {
                var definition = new ProgramSupportTableDefinition("device-reference", classOption.Name, classOption.TableName,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                metadata = await GetMetadataAsync(connection, null, definition);
                if (!metadata.Columns.ContainsKey("PLC") || !metadata.Columns.ContainsKey("Record"))
                    throw new InvalidOperationException($"Нельзя проверить ссылку класса «{classOption.Name}»: в {classOption.TableName} отсутствует PLC или Record.");
                checkedTables[classOption.TableName] = metadata;
            }
            if (!checkedReferences.Add((classOption.TableName, devicePlc, deviceRecord))) continue;
            using var exists = new SqlCommand($"SELECT TOP (1) 1 FROM {QuoteTable(classOption.TableName)} WHERE [PLC]=@plc AND [Record]=@record;", connection);
            exists.Parameters.AddWithValue("@plc", devicePlc);
            exists.Parameters.AddWithValue("@record", deviceRecord);
            if (await exists.ExecuteScalarAsync() == null)
                throw new InvalidOperationException($"Объект PLC={devicePlc}, класс={deviceClass}, Record={deviceRecord} не найден в текущем проекте.");
        }
    }

    public async Task<IReadOnlyList<MatrixDevicePlcOption>> LoadMatrixDevicePlcsAsync(MatrixDeviceClassOption deviceClass)
    {
        if (_offline != null)
            return _offline.GetRecords(deviceClass.TableName).Select(record => OfflineNumber(record, "PLC"))
                .Where(value => value.HasValue).Select(value => value!.Value).Distinct().OrderBy(value => value)
                .Select(value => new MatrixDevicePlcOption(value, value.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
        var metadata = await GetDeviceTableMetadataAsync(connection, deviceClass);
        if (!metadata.Columns.ContainsKey("PLC") || !metadata.Columns.ContainsKey("Record"))
            throw new InvalidOperationException($"Нельзя выбрать объект класса «{deviceClass.Name}»: в {deviceClass.TableName} отсутствует PLC или Record.");
        using var command = new SqlCommand($"SELECT DISTINCT [PLC] FROM {QuoteTable(deviceClass.TableName)} WHERE [PLC] IS NOT NULL ORDER BY [PLC];", connection);
        using var reader = await command.ExecuteReaderAsync();
        var result = new List<MatrixDevicePlcOption>();
        while (await reader.ReadAsync())
        {
            int number = Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            result.Add(new MatrixDevicePlcOption(number, number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public async Task<IReadOnlyList<MatrixDeviceObjectOption>> LoadMatrixDeviceObjectsAsync(MatrixDeviceClassOption deviceClass, int plc)
    {
        if (_offline != null)
        {
            var schema = _offline.GetSchema(deviceClass.TableName);
            string? display = new[] { "Name_L2", "NameL2", "Name_L1", "NameL1", "Name" }
                .FirstOrDefault(name => schema?.Columns.Any(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true);
            return _offline.GetRecords(deviceClass.TableName).Where(record => OfflineNumber(record, "PLC") == plc && OfflineNumber(record, "Record").HasValue)
                .Select(record =>
                {
                    int number = OfflineNumber(record, "Record")!.Value;
                    string name = display != null && record.Values.TryGetPropertyValue(display, out var value) ? value?.ToString() ?? "" : "";
                    return new MatrixDeviceObjectOption(plc, deviceClass.Number, number,
                        string.IsNullOrWhiteSpace(name) || name == number.ToString(System.Globalization.CultureInfo.InvariantCulture) ? number.ToString() : $"{number} · {name}");
                }).OrderBy(item => item.Record).ToList();
        }
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
        var metadata = await GetDeviceTableMetadataAsync(connection, deviceClass);
        if (!metadata.Columns.ContainsKey("PLC") || !metadata.Columns.ContainsKey("Record"))
            throw new InvalidOperationException($"Нельзя выбрать объект класса «{deviceClass.Name}»: в {deviceClass.TableName} отсутствует PLC или Record.");
        string? displayColumn = new[] { "Name_L2", "NameL2", "Name_L1", "NameL1", "Name" }
            .FirstOrDefault(name => metadata.Columns.ContainsKey(name));
        string displaySql = displayColumn == null
            ? "CONVERT(nvarchar(32), [Record])"
            : $"CONVERT(nvarchar(256), {Quote(displayColumn)})";
        using var command = new SqlCommand($"SELECT [PLC], [Record], {displaySql} AS [DisplayName] FROM {QuoteTable(deviceClass.TableName)} WHERE [PLC]=@plc ORDER BY [Record];", connection);
        command.Parameters.AddWithValue("@plc", plc);
        using var reader = await command.ExecuteReaderAsync();
        var result = new List<MatrixDeviceObjectOption>();
        while (await reader.ReadAsync())
        {
            int record = Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
            string name = reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2), System.Globalization.CultureInfo.CurrentCulture) ?? "";
            result.Add(new MatrixDeviceObjectOption(plc, deviceClass.Number, record,
                string.IsNullOrWhiteSpace(name) || name == record.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ? record.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : $"{record} · {name}"));
        }
        return result;
    }

    private static Task<TableMetadata> GetDeviceTableMetadataAsync(SqlConnection connection, MatrixDeviceClassOption deviceClass)
    {
        var definition = new ProgramSupportTableDefinition("device-reference", deviceClass.Name, deviceClass.TableName,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        return GetMetadataAsync(connection, null, definition);
    }

    public event Action<ChangeJournalEntry>? ChangeJournalRequested;

    public async Task SaveAsync(ProgramSupportTableData tableData, IReadOnlyDictionary<string, object> scope)
    {
        var definition = tableData?.Definition ?? throw new ArgumentNullException(nameof(tableData));
        if (!tableData.CanWrite || definition.ReadOnly)
            throw new InvalidOperationException(tableData.WriteBlockReason ?? definition.ReadOnlyReason ?? "Запись для этой таблицы заблокирована.");

        var replacements = new List<ChangeJournalRowChange>();
        if (_offline != null)
        {
            var insertSnapshots = await SaveOfflineAsync(tableData, scope, replacements);
            PublishJournal(tableData, insertSnapshots, replacements);
            return;
        }

        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
        using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var onlineInsertSnapshots = new Dictionary<DataRow, IReadOnlyDictionary<string, object?>>();
        try
        {
            var metadata = await GetMetadataAsync(connection, transaction, definition);
            EnsureWriteKey(definition, metadata.UniqueKeys);
            ValidateScope(definition, scope, metadata.Columns);

            foreach (DataRow row in tableData.Rows.Rows)
            {
                if (row.RowState == DataRowState.Unchanged || row.RowState == DataRowState.Detached) continue;
                switch (row.RowState)
                {
                    case DataRowState.Added:
                        if (definition.Id == "queue-selections")
                        {
                            using var parent = new SqlCommand("""
                                SELECT TOP (1) 1 FROM dbo.Prog_Queue WITH (UPDLOCK,HOLDLOCK)
                                WHERE PLC=@plc AND PLC_Class_Prog_Number=@class AND Prog_Record=@program AND Record=@queue;
                                """, connection, transaction);
                            parent.Parameters.AddWithValue("@plc", scope["PLC"]);
                            parent.Parameters.AddWithValue("@class", scope["PLC_Class_Prog_Number"]);
                            parent.Parameters.AddWithValue("@program", scope["Prog_Record"]);
                            parent.Parameters.AddWithValue("@queue", row["Queue_Record"]);
                            if (await parent.ExecuteScalarAsync() == null)
                                throw new InvalidOperationException("Выбранная очередь отсутствует в текущей программе.");
                        }
                        onlineInsertSnapshots[row] = await InsertAsync(connection, transaction, tableData, row, scope, metadata);
                        break;
                    case DataRowState.Modified:
                        await UpdateAsync(connection, transaction, tableData, row, scope, metadata);
                        break;
                    case DataRowState.Deleted:
                        await ApplyStagedDeletionReplacementAsync(tableData, row, replacements, connection, transaction);
                        await DeleteAsync(connection, transaction, tableData, row, scope, metadata);
                        break;
                }
            }
            await transaction.CommitAsync();
        }
        catch
        {
            try { await transaction.RollbackAsync(); } catch { }
            throw;
        }
        PublishJournal(tableData, onlineInsertSnapshots, replacements);
    }

    private void PublishJournal(ProgramSupportTableData tableData,
        IReadOnlyDictionary<DataRow, IReadOnlyDictionary<string, object?>> insertSnapshots, IReadOnlyList<ChangeJournalRowChange> replacements)
    {
        if (ChangeJournalRequested == null) return;
        var definition = tableData.Definition;
        var changes = new List<ChangeJournalRowChange>(replacements);
        foreach (DataRow row in tableData.Rows.Rows)
        {
            var kind = row.RowState switch
            {
                DataRowState.Added => ChangeJournalOperationKind.Insert,
                DataRowState.Modified => ChangeJournalOperationKind.Update,
                DataRowState.Deleted => ChangeJournalOperationKind.Delete,
                _ => (ChangeJournalOperationKind?)null
            };
            if (kind == null) continue;
            Dictionary<string, ChangeJournalValue> Values(IEnumerable<string> names, DataRowVersion version) =>
                names.Where(name => row.HasVersion(version) && row.Table.Columns.Contains(name))
                    .ToDictionary(name => name, name => ChangeJournalValue.FromObject(row[name, version]), StringComparer.OrdinalIgnoreCase);
            var columns = row.Table.Columns.Cast<DataColumn>().Select(column => column.ColumnName);
            var insertSnapshot = kind == ChangeJournalOperationKind.Insert && insertSnapshots.TryGetValue(row, out var snapshot)
                ? snapshot
                : null;
            var after = insertSnapshot == null
                ? Values(columns, DataRowVersion.Current)
                : insertSnapshot.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value!), StringComparer.OrdinalIgnoreCase);
            changes.Add(new ChangeJournalRowChange
            {
                TableName = definition.TableName,
                Kind = kind.Value,
                Before = Values(columns, DataRowVersion.Original),
                After = after,
                IdentityBefore = Values(definition.KeyColumns, DataRowVersion.Original),
                IdentityAfter = insertSnapshot == null
                    ? Values(definition.KeyColumns, DataRowVersion.Current)
                    : definition.KeyColumns.Where(insertSnapshot.ContainsKey)
                        .ToDictionary(name => name, name => ChangeJournalValue.FromObject(insertSnapshot[name]!), StringComparer.OrdinalIgnoreCase),
                AfterIsCompleteOfflineRecord = _offline != null && insertSnapshot != null
            });
        }
        if (changes.Count == 0) return;
        var sql = _offline == null ? new SqlConnectionStringBuilder(_connectionString) : null;
        ChangeJournalRequested.Invoke(new ChangeJournalEntry
        {
            Mode = 3,
            ProjectType = sql == null ? "Offline" : "Online",
            ProjectId = _offline?.Project.Manifest?.ProjectId ?? "",
            ProjectName = _offline?.Project.Manifest?.Name ?? sql?.InitialCatalog ?? "",
            Server = sql?.DataSource ?? "",
            Database = sql?.InitialCatalog ?? "",
            UserRole = UserAuthorizationService.CurrentRole?.Name ?? "",
            Category = definition.Caption,
            Source = definition.TableName,
            EntityType = definition.TableName,
            Description = $"{definition.Caption} · записей: {changes.Count}",
            OperationKind = changes.Count == 1 ? changes[0].Kind : ChangeJournalOperationKind.Composite,
            RestoreIncludesAllChanges = changes.All(change => change.Kind != ChangeJournalOperationKind.Insert ||
                (definition.KeyColumns.All(change.IdentityAfter.ContainsKey) &&
                    (_offline != null ? change.AfterIsCompleteOfflineRecord : change.After.Count == tableData.Columns.Count))),
            Changes = changes
        });
    }

    private async Task<ProgramSupportTableData> LoadAsync(ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope, int? recipeRecord = null)
    {
        if (_offline != null) return LoadOffline(definition, scope, recipeRecord);
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(_connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection);
        var metadata = await GetMetadataAsync(connection, null, definition);
        ValidateScope(definition, scope, metadata.Columns);

        string where = string.Join(" AND ", definition.ScopeColumns.Select((column, index) => $"{Quote(column)}=@s{index}"));
        if (recipeRecord.HasValue) where += " AND [Recipe_Record]=@recipeRecord";
        string order = definition.KeyColumns.Count == 0 ? "" : " ORDER BY " + string.Join(", ", definition.KeyColumns.Select(Quote));
        using var command = new SqlCommand($"SELECT * FROM {QuoteTable(definition.TableName)} WHERE {where}{order};", connection);
        AddScopeParameters(command, definition, scope);
        if (recipeRecord.HasValue) command.Parameters.Add("@recipeRecord", SqlDbType.Int).Value = recipeRecord.Value;
        var rows = new DataTable(definition.TableName);
        using (var reader = await command.ExecuteReaderAsync()) rows.Load(reader);
        foreach (DataColumn column in rows.Columns)
        {
            if (!definition.KeyColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase) &&
                metadata.Columns.TryGetValue(column.ColumnName, out var columnMetadata) && columnMetadata.HasDefault)
                column.AllowDBNull = true;
        }

        bool keyMatches = definition.KeyColumns.Count > 0 && metadata.UniqueKeys.Any(key => SameColumns(key, definition.KeyColumns));
        bool canWrite = !definition.ReadOnly && keyMatches;
        string? blockReason = definition.ReadOnlyReason;
        if (!definition.ReadOnly && !keyMatches)
            blockReason = "Запись отключена: текущий уникальный ключ таблицы не совпадает с проверенной схемой.";
        if (keyMatches)
            rows.PrimaryKey = definition.KeyColumns.Select(name => rows.Columns[name] ?? throw new InvalidOperationException($"В таблице отсутствует ключевое поле {name}." )).ToArray();

        return new ProgramSupportTableData
        {
            Definition = definition,
            Rows = rows,
            Columns = metadata.Columns.Keys.ToArray(),
            IdentityColumns = metadata.Columns.Values.Where(column => column.Identity).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ComputedColumns = metadata.Columns.Values.Where(column => column.Computed).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            DefaultColumns = metadata.Columns.Values.Where(column => column.HasDefault).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            CanWrite = canWrite,
            WriteBlockReason = blockReason,
            ProgMatrixRows = definition.Id == "program-matrix" ? rows.Rows.Cast<DataRow>().Select(ToProgMatrixRow).ToArray() : null,
            MatrixListRows = definition.Id == "matrix-list" ? rows.Rows.Cast<DataRow>().Select(ToMatrixListRow).ToArray() : null
        };
    }

    private ProgramSupportTableData LoadOffline(ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope, int? recipeRecord)
    {
        var schema = _offline!.GetSchema(definition.TableName)
            ?? throw new InvalidOperationException($"В локальном проекте отсутствует схема «{definition.TableName}».");
        foreach (var name in definition.ScopeColumns)
            if (!schema.Columns.Any(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"В {schema.TableName} нет поля контекста «{name}».");
        var rows = new DataTable(schema.TableName);
        foreach (var column in schema.Columns) rows.Columns.Add(column.Name, OfflineType(column.DataType));
        foreach (var record in _offline.GetRecords(schema.TableName))
        {
            if (definition.ScopeColumns.Any(name => !record.Values.TryGetPropertyValue(name, out var value) ||
                !OfflineEquals(value, scope.TryGetValue(name, out var expected) ? expected : null))) continue;
            if (recipeRecord.HasValue && !OfflineEquals(record.Values["Recipe_Record"], recipeRecord.Value)) continue;
            var row = rows.NewRow();
            foreach (DataColumn column in rows.Columns)
                if (record.Values.TryGetPropertyValue(column.ColumnName, out var value))
                    row[column] = OfflineValue(value, schema.Columns.First(item => item.Name.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase)).DataType);
            rows.Rows.Add(row);
        }
        bool keyMatches = schema.HasStableKey && definition.KeyColumns.Count > 0 && SameColumns(schema.KeyColumns, definition.KeyColumns);
        if (keyMatches) rows.PrimaryKey = definition.KeyColumns.Select(name => rows.Columns[name]!).ToArray();
        rows.AcceptChanges();
        return new ProgramSupportTableData
        {
            Definition = definition, Rows = rows, Columns = schema.Columns.Select(column => column.Name).ToArray(),
            IdentityColumns = schema.Columns.Where(column => column.IsIdentity).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ComputedColumns = schema.Columns.Where(column => column.IsComputed).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            DefaultColumns = schema.Columns.Where(column => column.HasDefault).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            CanWrite = !definition.ReadOnly && keyMatches,
            WriteBlockReason = definition.ReadOnly ? definition.ReadOnlyReason : keyMatches ? null : "Запись отключена: стабильный ключ снимка не совпадает с проверенной схемой.",
            ProgMatrixRows = definition.Id == "program-matrix" ? rows.Rows.Cast<DataRow>().Select(ToProgMatrixRow).ToArray() : null,
            MatrixListRows = definition.Id == "matrix-list" ? rows.Rows.Cast<DataRow>().Select(ToMatrixListRow).ToArray() : null
        };
    }

    private async Task<IReadOnlyDictionary<DataRow, IReadOnlyDictionary<string, object?>>> SaveOfflineAsync(
        ProgramSupportTableData tableData, IReadOnlyDictionary<string, object> scope, List<ChangeJournalRowChange> replacements)
    {
        var definition = tableData.Definition;
        var schema = _offline!.GetSchema(definition.TableName)
            ?? throw new InvalidOperationException($"В локальном проекте отсутствует схема «{definition.TableName}».");
        if (!schema.HasStableKey || !SameColumns(schema.KeyColumns, definition.KeyColumns))
            throw new InvalidOperationException("Изменение отменено: ключ таблицы в локальном снимке отличается от проверенного ключа редактора.");
        var crud = (IProjectCrudDataSource)_offline;
        var insertSnapshots = new Dictionary<DataRow, IReadOnlyDictionary<string, object?>>();
        await _offline.ExecuteMutationTransactionAsync(async () =>
        {
            foreach (DataRow row in tableData.Rows.Rows)
            {
                if (row.RowState is DataRowState.Unchanged or DataRowState.Detached) continue;
                if (row.RowState == DataRowState.Added)
                {
                    if (definition.Id == "queue-selections" && !_offline.GetRecords("dbo.Prog_Queue").Any(parent =>
                        OfflineEquals(parent.Values["Record"], row["Queue_Record"]) &&
                        new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record" }.All(field => OfflineEquals(parent.Values[field], scope[field]))))
                        throw new InvalidOperationException("Выбранная очередь отсутствует в текущей программе.");
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in scope) values[pair.Key] = pair.Value;
                    foreach (DataColumn column in row.Table.Columns)
                    {
                        var metadata = schema.Columns.FirstOrDefault(item => item.Name.Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));
                        if (metadata == null || metadata.IsIdentity || metadata.IsComputed || metadata.IsReadOnly) continue;
                        bool included = definition.ScopeColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase)
                            || definition.KeyColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase)
                            || definition.EditableColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase)
                            || definition.ProtectedColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase);
                        if (!included || (row[column] == DBNull.Value && metadata.HasDefault)) continue;
                        values[column.ColumnName] = row[column] == DBNull.Value ? null : row[column];
                    }
                    if (UsesScopedMatrixRecord(definition) &&
                        schema.Columns.FirstOrDefault(column => column.Name.Equals("Record", StringComparison.OrdinalIgnoreCase)) is
                            { IsIdentity: false, IsComputed: false, IsReadOnly: false })
                    {
                        var suggestion = new ScopedRecordSuggestion();
                        foreach (var record in _offline.GetRecords(schema.TableName))
                            if (definition.ScopeColumns.All(column => OfflineEquals(record.Values[column], scope[column])))
                                suggestion.Add(record.Values["Record"]?.ToString());
                        values["Record"] = Convert.ChangeType(suggestion.GetValue(), row.Table.Columns["Record"]!.DataType);
                    }
                    var generatedKey = await ((IProjectCreateWithKeyDataSource)_offline).CreateRecordAndGetKeyAsync(schema.TableName, values);
                    var created = _offline.GetRecords(schema.TableName).SingleOrDefault(record =>
                        definition.KeyColumns.All(column => generatedKey.TryGetValue(column, out var expected) &&
                            record.Values.TryGetPropertyValue(column, out var actual) && OfflineEquals(actual, expected)));
                    if (created == null)
                        throw new InvalidOperationException($"Не удалось перечитать созданную запись {definition.TableName} по выданному ключу.");
                    var actualSnapshot = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in created.Values)
                    {
                        var column = schema.Columns.FirstOrDefault(item => item.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                        if (column != null) actualSnapshot[pair.Key] = OfflineValue(pair.Value, column.DataType);
                    }
                    insertSnapshots[row] = actualSnapshot;
                    continue;
                }
                var version = row.RowState == DataRowState.Deleted ? DataRowVersion.Original : DataRowVersion.Current;
                var key = definition.KeyColumns.ToDictionary(name => name, name => (object?)row[name, version], StringComparer.OrdinalIgnoreCase);
                if (row.RowState == DataRowState.Deleted)
                {
                    await ApplyStagedDeletionReplacementAsync(tableData, row, replacements, null, null);
                    EnsureNoOfflineDependents(definition, row);
                    int count = await crud.DeleteRecordsAsync(schema.TableName, schema.KeyColumns, new[] { key });
                    if (count != 1) throw new InvalidOperationException("Состав локального снимка изменился; удаление отменено.");
                    continue;
                }
                var editable = definition.EditableColumns.Where(name => !definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var changed = row.Table.Columns.Cast<DataColumn>().Where(column =>
                    !Equals(row[column, DataRowVersion.Original], row[column, DataRowVersion.Current])).Select(column => column.ColumnName).ToList();
                var forbidden = changed.Where(name => !editable.Contains(name)).ToList();
                if (forbidden.Count > 0)
                    throw new InvalidOperationException($"Изменение ключей, связей или полей без разрешённой записи заблокировано: {string.Join(", ", forbidden)}.");
                var changes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in changed)
                    if (row.Table.Columns.Contains(name)) changes[name] = row[name] == DBNull.Value ? null : row[name];
                if (changes.Count > 0) await crud.UpdateRecordAsync(schema.TableName, schema.KeyColumns, key, changes);
            }
        });
        return insertSnapshots;
    }

    private void EnsureNoOfflineDependents(ProgramSupportTableDefinition definition, DataRow row)
    {
        foreach (var check in KnownDependencies(definition.TableName))
        {
            var childSchema = _offline!.GetSchema(check.Table);
            if (childSchema == null || check.Columns.Any(name => !childSchema.Columns.Any(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))) continue;
            if (_offline.GetRecords(check.Table).Any(record => check.Columns.Select((name, index) =>
                    record.Values.TryGetPropertyValue(name, out var actual) &&
                    OfflineEquals(actual, row[check.ParentColumns[index], DataRowVersion.Original])).All(matches => matches)))
                throw new InvalidOperationException($"Удаление заблокировано: в {check.Table} найдены зависимые строки.");
        }
    }

    private static int? OfflineNumber(ProjectRecord record, string name)
        => record.Values.TryGetPropertyValue(name, out var value) && int.TryParse(value?.ToString(), out int number) ? number : null;

    private static Type OfflineType(string type) => (type ?? "").ToLowerInvariant() switch
    {
        "tinyint" => typeof(byte), "smallint" => typeof(short), "int" => typeof(int), "bigint" => typeof(long),
        "bit" => typeof(bool), "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "float" => typeof(double), "real" => typeof(float), "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "uniqueidentifier" => typeof(Guid), _ => typeof(string)
    };

    private static object OfflineValue(System.Text.Json.Nodes.JsonNode? value, string sqlType)
    {
        if (value == null) return DBNull.Value;
        string raw = value.ToString();
        Type type = OfflineType(sqlType);
        if (type == typeof(string)) return raw;
        try { return Convert.ChangeType(raw, type, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return DBNull.Value; }
    }

    private static bool OfflineEquals(System.Text.Json.Nodes.JsonNode? value, object? expected)
        => value == null ? expected == null || expected == DBNull.Value :
            string.Equals(value.ToString(), Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private static ProgMatrixRow ToProgMatrixRow(DataRow row) => new(
        new ProgMatrixKey(Convert.ToInt32(row["Record"]), Convert.ToInt32(row["PLC"]), Convert.ToInt32(row["PLC_Class_Number"])),
        OptionalValue(row, "Type") == DBNull.Value ? null : Convert.ToInt32(OptionalValue(row, "Type")),
        NullableText(OptionalValue(row, "Name_L1")), NullableText(OptionalValue(row, "Name_L2")),
        NullableText(OptionalValue(row, "DescriptionL1")), NullableText(OptionalValue(row, "DescriptionL2")));

    private static MatrixListRow ToMatrixListRow(DataRow row) => new(
        new MatrixListKey(Convert.ToInt32(row["Record"]), Convert.ToInt32(row["PLC"]), Convert.ToInt32(row["PLC_Class_Number"])),
        Convert.ToString(OptionalValue(row, "Name_L1")) ?? "", Convert.ToString(OptionalValue(row, "Name_L2")) ?? "",
        OptionalValue(row, "DB_Number") == DBNull.Value ? null : Convert.ToInt32(OptionalValue(row, "DB_Number")));

    private static object OptionalValue(DataRow row, string column)
        => row.Table.Columns.Contains(column) ? row[column] : DBNull.Value;

    private static string? NullableText(object value) => value == null || value == DBNull.Value ? null : Convert.ToString(value);

    internal static IReadOnlyDictionary<string, object> GetProgramScope(ProgramSupportTableDefinition definition, ProgramContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        object Value(string column) => column switch
        {
            "PLC" => Required(context.PlcValue, "PLC"),
            "PLC_Class_Prog_Number" => Required(context.ClassNumberValue, "Класс программы"),
            "Prog_Record" => Required(context.RecordValue, "Программа"),
            "Seq_Record" => Required(context.SeqRecordValue, "Последовательность"),
            _ => throw new InvalidOperationException($"Неизвестное поле контекста {column}.")
        };
        return definition.ScopeColumns.ToDictionary(column => column, Value, StringComparer.OrdinalIgnoreCase);
    }

    private static object Required(object? value, string label)
    {
        if (value == null || value == DBNull.Value) throw new InvalidOperationException($"Сначала выберите значение «{label}».");
        return value;
    }

    private async Task<IReadOnlyDictionary<string, object?>> InsertAsync(SqlConnection connection, SqlTransaction transaction, ProgramSupportTableData tableData,
        DataRow row, IReadOnlyDictionary<string, object> scope, TableMetadata metadata)
    {
        var definition = tableData.Definition;
        if (!definition.AllowInsert) throw new InvalidOperationException($"Добавление строк в {definition.Caption} отключено до проверки зависимостей.");
        foreach (var pair in scope) row[pair.Key] = pair.Value;
        if (definition.KeyColumns.Contains("Record", StringComparer.OrdinalIgnoreCase) && !definition.ScopeColumns.Contains("Record", StringComparer.OrdinalIgnoreCase)
            && !metadata.Columns["Record"].Identity)
            row["Record"] = await NextRecordAsync(connection, transaction, definition, scope, metadata.Columns["Record"].Type);

        foreach (string key in definition.KeyColumns)
            if (row[key] == DBNull.Value || row[key] == null)
                throw new InvalidOperationException($"Укажите поле ключа «{key}» перед сохранением.");

        foreach (var column in metadata.Columns.Values.Where(column => !column.Nullable && !column.Identity && !column.Computed && !column.HasDefault))
        {
            if (!row.Table.Columns.Contains(column.Name) || row[column.Name] == DBNull.Value || row[column.Name] == null)
                throw new InvalidOperationException($"Заполните обязательное поле «{column.Name}».");
            if (row[column.Name] is string text && string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"Заполните обязательное поле «{column.Name}».");
        }

        var insertColumns = new List<string>();
        var insertValues = new List<string>();
        using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        foreach (DataColumn column in row.Table.Columns)
        {
            string name = column.ColumnName;
            if (metadata.Columns[name].Identity || metadata.Columns[name].Computed) continue;
            bool included = definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                || definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                || definition.EditableColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                || definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
            if (!included) continue;
            if ((row[name] == DBNull.Value || row[name] == null) && metadata.Columns[name].HasDefault && !definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            insertColumns.Add(Quote(name));
            string parameter = "@v" + insertValues.Count;
            insertValues.Add(parameter);
            command.Parameters.AddWithValue(parameter, row[name] ?? DBNull.Value);
        }
        var keyDefinitions = definition.KeyColumns.Select(column => Quote(column) + " sql_variant").ToList();
        var outputColumns = string.Join(", ", definition.KeyColumns.Select(Quote));
        var outputValues = string.Join(", ", definition.KeyColumns.Select(column => "INSERTED." + Quote(column)));
        var joins = string.Join(" AND ", definition.KeyColumns.Select(column => $"target.{Quote(column)} = inserted.{Quote(column)}"));
        command.CommandText = $"DECLARE @inserted TABLE ({string.Join(", ", keyDefinitions)}); " +
            $"INSERT INTO {QuoteTable(definition.TableName)} ({string.Join(", ", insertColumns)}) " +
            $"OUTPUT {outputValues} INTO @inserted ({outputColumns}) VALUES ({string.Join(", ", insertValues)}); " +
            $"SELECT target.* FROM {QuoteTable(definition.TableName)} AS target INNER JOIN @inserted AS inserted ON {joins};";
        var snapshot = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Не удалось перечитать созданную строку по её ключу; транзакция отменена.");
        for (var index = 0; index < reader.FieldCount; index++)
            snapshot[reader.GetName(index)] = reader.IsDBNull(index) ? DBNull.Value : reader.GetValue(index);
        if (await reader.ReadAsync())
            throw new InvalidOperationException("Ключ созданной строки не уникален; транзакция отменена.");
        if (metadata.Columns.Keys.Any(column => !snapshot.ContainsKey(column)))
            throw new InvalidOperationException("Снимок созданной строки неполон; транзакция отменена.");
        return snapshot;
    }

    private async Task UpdateAsync(SqlConnection connection, SqlTransaction transaction, ProgramSupportTableData tableData,
        DataRow row, IReadOnlyDictionary<string, object> scope, TableMetadata metadata)
    {
        var definition = tableData.Definition;
        var changed = definition.EditableColumns
            .Where(name => row.Table.Columns.Contains(name)
                && !definition.KeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !definition.ScopeColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !definition.ProtectedColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !ValuesEqual(row[name, DataRowVersion.Original], row[name, DataRowVersion.Current]))
            .ToList();
        var forbidden = row.Table.Columns.Cast<DataColumn>()
            .Where(column => !ValuesEqual(row[column, DataRowVersion.Original], row[column, DataRowVersion.Current]))
            .Select(column => column.ColumnName)
            .Where(name => !changed.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (forbidden.Length > 0)
            throw new InvalidOperationException($"Изменение ключей, связей или полей без разрешённой записи заблокировано: {string.Join(", ", forbidden)}.");
        if (changed.Count == 0) return;

        using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        var assignments = new List<string>();
        for (int i = 0; i < changed.Count; i++)
        {
            assignments.Add($"{Quote(changed[i])}=@n{i}");
            command.Parameters.AddWithValue("@n" + i, row[changed[i]] ?? DBNull.Value);
        }
        var predicates = AddKeyAndOriginalPredicates(command, definition, row, scope, changed);
        command.CommandText = $"DECLARE @affected TABLE (Marker int NOT NULL); UPDATE {QuoteTable(definition.TableName)} SET {string.Join(", ", assignments)} OUTPUT 1 INTO @affected(Marker) WHERE {predicates}; SELECT COUNT(*) FROM @affected;";
        if (await ExecuteAffectedCountAsync(command) != 1)
            throw new InvalidOperationException("Строка изменилась после загрузки либо ключ не уникален. Обновите список; транзакция отменена.");
    }

    private async Task DeleteAsync(SqlConnection connection, SqlTransaction transaction, ProgramSupportTableData tableData,
        DataRow row, IReadOnlyDictionary<string, object> scope, TableMetadata metadata)
    {
        var definition = tableData.Definition;
        if (!definition.AllowDelete) throw new InvalidOperationException($"Удаление строк из {definition.Caption} отключено.");
        var dependencies = await CountKnownDependentsAsync(connection, transaction, definition, row);
        if (dependencies > 0)
            throw new InvalidOperationException($"Удаление заблокировано: найдены зависимые строки ({dependencies}). Сначала измените или удалите связи.");
        await EnsureNoForeignKeyDependentsAsync(connection, transaction, definition, row);

        using var command = new SqlCommand { Connection = connection, Transaction = transaction };
        string predicates = AddKeyAndOriginalPredicates(command, definition, row, scope, Array.Empty<string>(), DataRowVersion.Original);
        command.CommandText = $"DECLARE @affected TABLE (Marker int NOT NULL); DELETE FROM {QuoteTable(definition.TableName)} OUTPUT 1 INTO @affected(Marker) WHERE {predicates}; SELECT COUNT(*) FROM @affected;";
        if (await ExecuteAffectedCountAsync(command) != 1)
            throw new InvalidOperationException("Удаление не затронуло ровно одну строку; транзакция отменена.");
    }

    private static async Task<int> ExecuteAffectedCountAsync(SqlCommand command)
    {
        object? result = await command.ExecuteScalarAsync();
        return result == null || result == DBNull.Value
            ? 0
            : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountKnownDependentsAsync(SqlConnection connection, SqlTransaction transaction,
        ProgramSupportTableDefinition definition, DataRow row)
    {
        int count = 0;
        foreach (var check in KnownDependencies(definition.TableName))
        {
            using var exists = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@table) IS NULL THEN 0 ELSE 1 END;", connection, transaction);
            exists.Parameters.AddWithValue("@table", check.Table);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0) continue;
            string predicate = string.Join(" AND ", check.Columns.Select((column, index) => $"{Quote(column)}=@m{index}"));
            using var command = new SqlCommand($"SELECT COUNT_BIG(1) FROM {QuoteTable(check.Table)} WITH (UPDLOCK,HOLDLOCK) WHERE {predicate};", connection, transaction);
            for (int i = 0; i < check.Columns.Length; i++)
                command.Parameters.AddWithValue("@m" + i, row[check.ParentColumns[i], DataRowVersion.Original]);
            count = checked(count + Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        return count;
    }

    private static async Task EnsureNoForeignKeyDependentsAsync(SqlConnection connection, SqlTransaction transaction,
        ProgramSupportTableDefinition definition, DataRow row)
    {
        const string fkSql = @"
SELECT fk.object_id, s.name AS SchemaName, t.name AS TableName, cc.name AS ChildColumn, pc.name AS ParentColumn,
       fkc.constraint_column_id
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables t ON fkc.parent_object_id = t.object_id
JOIN sys.schemas s ON t.schema_id = s.schema_id
JOIN sys.columns cc ON cc.object_id = fkc.parent_object_id AND cc.column_id = fkc.parent_column_id
JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id AND pc.column_id = fkc.referenced_column_id
WHERE fkc.referenced_object_id = OBJECT_ID(@table) AND fkc.parent_object_id <> fkc.referenced_object_id
ORDER BY fk.object_id, fkc.constraint_column_id;";
        using var metadataCommand = new SqlCommand(fkSql, connection, transaction);
        metadataCommand.Parameters.AddWithValue("@table", definition.TableName);
        using var reader = await metadataCommand.ExecuteReaderAsync();
        var groups = new Dictionary<string, List<(string Schema, string Table, string Child, string Parent)>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            string fk = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (!groups.TryGetValue(fk, out var columns)) groups[fk] = columns = new();
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        await reader.CloseAsync();

        foreach (var relation in groups.Values)
        {
            if (relation.Any(column => !row.Table.Columns.Contains(column.Parent))) continue;
            string where = string.Join(" AND ", relation.Select((column, index) => $"{Quote(column.Child)}=@r{index}"));
            string childTable = Quote(relation[0].Schema) + "." + Quote(relation[0].Table);
            using var count = new SqlCommand($"SELECT TOP (1) 1 FROM {childTable} WHERE {where};", connection, transaction);
            for (int i = 0; i < relation.Count; i++)
                count.Parameters.AddWithValue("@r" + i, row[relation[i].Parent, DataRowVersion.Original]);
            if (await count.ExecuteScalarAsync() != null)
                throw new InvalidOperationException($"Удаление заблокировано внешним ключом: {relation[0].Table} содержит зависимые строки.");
        }
    }

    private static string AddKeyAndOriginalPredicates(SqlCommand command, ProgramSupportTableDefinition definition,
        DataRow row, IReadOnlyDictionary<string, object> scope, IReadOnlyList<string> changed,
        DataRowVersion rowVersion = DataRowVersion.Current)
    {
        int index = 0;
        var predicates = new List<string>();
        foreach (string name in definition.KeyColumns.Concat(definition.ScopeColumns).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var value = row[name, rowVersion];
            predicates.Add(AddPredicate(command, name, value, "k" + index++));
        }
        foreach (string name in changed)
        {
            var value = row[name, DataRowVersion.Original];
            predicates.Add(AddPredicate(command, name, value, "o" + index++));
        }
        return string.Join(" AND ", predicates);
    }

    private static string AddPredicate(SqlCommand command, string column, object value, string parameter)
    {
        if (value == null || value == DBNull.Value) return $"{Quote(column)} IS NULL";
        command.Parameters.AddWithValue("@" + parameter, value);
        return $"{Quote(column)}=@{parameter}";
    }

    internal static bool UsesScopedMatrixRecord(ProgramSupportTableDefinition definition) =>
        definition.Id is "matrix-list" or "program-matrix";

    private static async Task<object> NextRecordAsync(SqlConnection connection, SqlTransaction transaction,
        ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope, Type recordType)
    {
        string where = string.Join(" AND ", definition.ScopeColumns.Select((column, index) => $"{Quote(column)}=@s{index}"));
        string record = Quote("Record");
        bool useGap = UsesScopedMatrixRecord(definition);
        string value = useGap ? record : $"COALESCE(MAX(CONVERT(bigint,{record})),0)+1";
        using var command = new SqlCommand($"SELECT {value} FROM {QuoteTable(definition.TableName)} WITH (UPDLOCK,HOLDLOCK) WHERE {where};", connection, transaction);
        AddScopeParameters(command, definition, scope);
        if (!useGap)
            return Convert.ChangeType(await command.ExecuteScalarAsync() ?? 1L, recordType, System.Globalization.CultureInfo.InvariantCulture);

        using var reader = await command.ExecuteReaderAsync();
        var suggestion = new ScopedRecordSuggestion();
        while (await reader.ReadAsync())
            suggestion.Add(reader.GetValue(0));
        return Convert.ChangeType(suggestion.GetValue(), recordType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<TableMetadata> GetMetadataAsync(SqlConnection connection, SqlTransaction? transaction,
        ProgramSupportTableDefinition definition)
    {
        const string columnSql = @"
SELECT c.name, t.name AS TypeName, c.is_nullable, c.is_identity, c.is_computed,
       CONVERT(bit, CASE WHEN dc.object_id IS NULL THEN 0 ELSE 1 END) AS HasDefault
FROM sys.columns c
JOIN sys.types t ON c.user_type_id=t.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
WHERE c.object_id=OBJECT_ID(@table);";
        using var columnCommand = new SqlCommand(columnSql, connection, transaction);
        columnCommand.Parameters.AddWithValue("@table", definition.TableName);
        using var columnReader = await columnCommand.ExecuteReaderAsync();
        var columns = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
        while (await columnReader.ReadAsync())
        {
            var name = columnReader.GetString(0);
            columns[name] = new ColumnMetadata(name, columnReader.GetString(1), columnReader.GetBoolean(2),
                columnReader.GetBoolean(3), columnReader.GetBoolean(4), columnReader.GetBoolean(5));
        }
        await columnReader.CloseAsync();
        if (columns.Count == 0) throw new InvalidOperationException($"Таблица {definition.TableName} отсутствует в текущей базе.");

        const string keySql = @"
SELECT i.index_id, c.name, ic.key_ordinal
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.object_id=OBJECT_ID(@table) AND i.is_unique=1 AND i.is_hypothetical=0 AND i.has_filter=0 AND ic.key_ordinal>0
ORDER BY i.index_id, ic.key_ordinal;";
        using var keyCommand = new SqlCommand(keySql, connection, transaction);
        keyCommand.Parameters.AddWithValue("@table", definition.TableName);
        using var keyReader = await keyCommand.ExecuteReaderAsync();
        var keyGroups = new Dictionary<int, List<string>>();
        while (await keyReader.ReadAsync())
        {
            int indexId = keyReader.GetInt32(0);
            if (!keyGroups.TryGetValue(indexId, out var key)) keyGroups[indexId] = key = new();
            key.Add(keyReader.GetString(1));
        }
        await keyReader.CloseAsync();
        return new TableMetadata(columns, keyGroups.Values.Select(key => (IReadOnlyList<string>)key).ToArray());
    }

    private static void EnsureWriteKey(ProgramSupportTableDefinition definition, IReadOnlyList<IReadOnlyList<string>> actualKeys)
    {
        if (definition.ReadOnly || definition.KeyColumns.Count == 0 || !actualKeys.Any(key => SameColumns(key, definition.KeyColumns)))
            throw new InvalidOperationException(definition.ReadOnlyReason ?? $"Запись в {definition.TableName} остановлена: уникальный ключ отличается от схемы backup.");
    }

    private static bool SameColumns(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static void ValidateScope(ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope,
        IReadOnlyDictionary<string, ColumnMetadata> columns)
    {
        foreach (string name in definition.ScopeColumns)
        {
            if (!columns.ContainsKey(name)) throw new InvalidOperationException($"В {definition.TableName} отсутствует контекстный столбец {name}; операция остановлена.");
            if (!scope.TryGetValue(name, out var value) || value == null || value == DBNull.Value)
                throw new InvalidOperationException($"Контекст «{name}» не выбран; операция остановлена.");
        }
        foreach (string name in definition.KeyColumns)
            if (!columns.ContainsKey(name)) throw new InvalidOperationException($"В {definition.TableName} отсутствует ключевой столбец {name}; операция остановлена.");
    }

    private static void AddScopeParameters(SqlCommand command, ProgramSupportTableDefinition definition, IReadOnlyDictionary<string, object> scope)
    {
        for (int i = 0; i < definition.ScopeColumns.Count; i++)
            command.Parameters.AddWithValue("@s" + i, scope[definition.ScopeColumns[i]]);
    }

    private static ProgramSupportTableDefinition Find(IReadOnlyList<ProgramSupportTableDefinition> catalog, string id) =>
        catalog.FirstOrDefault(table => table.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Неизвестен раздел рабочих данных.", nameof(id));

    private static bool ValuesEqual(object left, object right) =>
        left == DBNull.Value ? right == DBNull.Value : right != DBNull.Value && Equals(left, right);

    private static string QuoteTable(string tableName)
    {
        var parts = tableName.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new InvalidOperationException("Разрешены только таблицы схемы dbo.");
        return Quote(parts[0]) + "." + Quote(parts[1]);
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private sealed record ColumnMetadata(string Name, string TypeName, bool Nullable, bool Identity, bool Computed, bool HasDefault)
    {
        public Type Type => TypeName.ToLowerInvariant() switch
        {
            "int" => typeof(int), "bigint" => typeof(long), "smallint" => typeof(short), "tinyint" => typeof(byte),
            "bit" => typeof(bool), "real" => typeof(float), "float" => typeof(double), "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
            "date" => typeof(DateTime), "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
            _ => typeof(string)
        };
    }

    private sealed record TableMetadata(Dictionary<string, ColumnMetadata> Columns, IReadOnlyList<IReadOnlyList<string>> UniqueKeys);
}
