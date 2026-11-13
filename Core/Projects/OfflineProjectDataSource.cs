#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Configurator.Core.Projects;

/// <summary>Local source for an opened .cfgproj package. It never creates or uses a SQL connection.</summary>
public sealed class OfflineProjectDataSource : IProjectDataSource, IProjectCrudDataSource, IProjectBatchCrudDataSource
{
    private readonly ConfiguratorProject _project;
    // The local snapshot may contain 100k+ records. Repeated UI lookups must not
    // rescan the entire list for every field, combo box, or validation operation.
    private Dictionary<string, List<ProjectRecord>>? _recordsByTable;
    private Dictionary<string, List<string>>? _tableNamesByLeaf;
    private bool _suspendNotifications;
    public OfflineProjectDataSource(ConfiguratorProject project) => _project = project ?? throw new ArgumentNullException(nameof(project));
    public event EventHandler? ProjectChanged;
    public string DisplayName => _project.Manifest.Name;
    public bool IsReadOnly => false;
    public ConfiguratorProject Project => _project;

    /// <summary>Publishes a project metadata change made through a project-scoped adapter.</summary>
    public void NotifyProjectChanged() => Touch();

    /// <summary>Invalidates indexes after an out-of-band, project-scoped journal restore.</summary>
    public void InvalidateIndex()
    {
        _recordsByTable = null;
        _tableNamesByLeaf = null;
    }

    public IReadOnlyList<ProjectTableSchema> GetTables()
    {
        EnsureRecordIndex();
        var known = _project.TableSchemas.Select(x => x.TableName)
            .Concat(_project.Mappings.Select(x => x.TableName))
            .Concat(_recordsByTable!.Keys)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return known.Select(name => GetSchema(name) ?? new ProjectTableSchema { TableName = name }).ToList();
    }

    public IReadOnlyList<ProjectRecord> GetRecords(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return Array.Empty<ProjectRecord>();
        EnsureRecordIndex();
        var resolved = ResolveTableName(tableName);
        if (_recordsByTable!.TryGetValue(resolved, out var exact) && exact.Count > 0) return exact;

        // Legacy snapshots may use dbo.Table in one part of the package and Table
        // in another. Resolve only when the leaf is unambiguous.
        var leaf = GetLeaf(resolved);
        if (!_tableNamesByLeaf!.TryGetValue(leaf, out var matchingNames) || matchingNames.Count != 1)
            return Array.Empty<ProjectRecord>();
        return _recordsByTable.TryGetValue(matchingNames[0], out var records) ? records : Array.Empty<ProjectRecord>();
    }

    public ProjectTableSchema? GetSchema(string tableName)
    {
        var resolved = ResolveTableName(tableName);
        var exact = _project.TableSchemas.FirstOrDefault(x => string.Equals(x.TableName, resolved, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var leaf = resolved.Split('.').Last().Trim('[', ']');
        var matches = _project.TableSchemas.Where(x => x.TableName.Split('.').Last().Trim('[', ']')
            .Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Resolve legacy class mappings such as "Valve" to snapshot names such as "dbo.Valve".</summary>
    public string ResolveTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return tableName;
        EnsureRecordIndex();
        var exact = _project.TableSchemas.Select(x => x.TableName)
            .Concat(_project.Mappings.Select(x => x.TableName))
            .Concat(_recordsByTable!.Keys)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var leaf = GetLeaf(tableName);
        var matches = _project.TableSchemas.Select(x => x.TableName)
            .Concat(_project.Mappings.Select(x => x.TableName))
            .Concat(_recordsByTable.Keys)
            .Where(x => !string.IsNullOrWhiteSpace(x) && GetLeaf(x).Equals(leaf, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : tableName;
    }

    public Task CreateRecordAsync(string tableName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("Не задана таблица.", nameof(tableName));
        if (values.Count == 0) throw new InvalidOperationException("Нет данных для создания записи.");
        var resolved = ResolveTableName(tableName);
        var schema = GetSchema(resolved) ?? throw new InvalidOperationException($"Для таблицы «{resolved}» отсутствует проверенная схема.");
        var keyColumns = schema.KeyColumns;
        if (!schema.HasStableKey || keyColumns.Count == 0) throw new InvalidOperationException($"В локальном снимке для таблицы «{resolved}» не сохранены стабильные ключевые столбцы.");
        var normalizedValues = values.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var generatedIdentityColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in normalizedValues.Keys)
            if (FindColumn(schema, name).IsIdentity)
                throw new InvalidOperationException($"Значение поля identity «{name}» нельзя задавать вручную.");
        EnsureRecordIndex();
        foreach (var keyColumn in keyColumns.Where(k => !normalizedValues.ContainsKey(k)))
        {
            var identityColumn = schema.Columns.FirstOrDefault(c => c.Name.Equals(keyColumn, StringComparison.OrdinalIgnoreCase) && c.IsIdentity);
            if (identityColumn == null)
                throw new InvalidOperationException($"Для создания записи не задано ключевое значение «{keyColumn}».");
            long max = 0;
            if (_recordsByTable!.TryGetValue(resolved, out var currentRecords))
                foreach (var record in currentRecords)
                    if (record.Values.TryGetPropertyValue(keyColumn, out var node) && long.TryParse(node?.ToString(), out var number)) max = Math.Max(max, number);
            normalizedValues[keyColumn] = checked(max + 1);
            generatedIdentityColumns.Add(keyColumn);
        }
        ValidateValues(schema, normalizedValues, isCreate: true, generatedIdentityColumns);
        var key = BuildKey(schema, keyColumns, normalizedValues);
        if (_recordsByTable!.TryGetValue(resolved, out var existing) && existing.Any(r => KeyMatches(schema, r, keyColumns, normalizedValues)))
            throw new InvalidOperationException("Запись с таким ключом уже существует в локальном проекте.");
        var json = new System.Text.Json.Nodes.JsonObject();
        foreach (var pair in normalizedValues) json[pair.Key] = ProjectKeyCodec.ToNode(pair.Value);
        _project.LocalRecords.Add(new ProjectRecord { TableName = resolved, Key = key, Values = json });
        Touch();
        return Task.CompletedTask;
    }

    public Task CreateRecordsAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (records == null || records.Count == 0) return Task.CompletedTask;
        var backup = _project.LocalRecords.Select(r => new ProjectRecord
        {
            TableName = r.TableName, Key = r.Key, RowVersion = r.RowVersion,
            Values = (System.Text.Json.Nodes.JsonObject)r.Values.DeepClone()
        }).ToList();
        _suspendNotifications = true;
        try
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CreateRecordAsync(tableName, record, cancellationToken).GetAwaiter().GetResult();
            }
            _suspendNotifications = false;
            Touch();
            return Task.CompletedTask;
        }
        catch
        {
            _project.LocalRecords.Clear();
            _project.LocalRecords.AddRange(backup);
            _suspendNotifications = false;
            InvalidateIndexes();
            throw;
        }
    }

    public Task UpdateRecordAsync(string tableName, IReadOnlyList<string> keyColumns,
        IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (keyColumns == null || keyColumns.Count == 0) throw new InvalidOperationException("Нельзя изменить запись без ключевых столбцов.");
        ArgumentNullException.ThrowIfNull(keyValues);
        ArgumentNullException.ThrowIfNull(values);
        if (keyColumns.Any(k => !keyValues.ContainsKey(k))) throw new InvalidOperationException("Не заданы все значения ключа.");
        var resolved = ResolveTableName(tableName);
        var schema = GetSchema(resolved) ?? throw new InvalidOperationException($"Для таблицы «{resolved}» отсутствует проверенная схема.");
        if (!schema.HasStableKey || !SameColumns(schema.KeyColumns, keyColumns))
            throw new InvalidOperationException("Переданные ключевые столбцы не совпадают со стабильным ключом проверенной схемы.");
        EnsureRecordIndex();
        if (!_recordsByTable!.TryGetValue(resolved, out var tableRecords)) throw new InvalidOperationException($"В локальном проекте отсутствует таблица «{resolved}».");
        var matches = tableRecords.Where(r => KeyMatches(schema, r, keyColumns, keyValues)).ToList();
        if (matches.Count != 1) throw new InvalidOperationException(matches.Count == 0
            ? "Изменяемая запись не найдена в локальном проекте."
            : "Ключ записи неоднозначен в локальном проекте; изменение отменено.");
        var record = matches[0];
        var candidate = (System.Text.Json.Nodes.JsonObject)record.Values.DeepClone();
        var normalizedValues = values.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        ValidateValues(schema, normalizedValues, isCreate: false, null);
        foreach (var pair in normalizedValues)
        {
            var column = FindColumn(schema, pair.Key);
            candidate[column.Name] = ProjectKeyCodec.ToNode(pair.Value);
        }
        var newKey = ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => candidate[name]);
        if (tableRecords.Any(other => !ReferenceEquals(other, record) &&
            string.Equals(ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => other.Values[name]), newKey, StringComparison.Ordinal)))
            throw new InvalidOperationException("Запись с таким ключом уже существует в локальном проекте.");
        record.Values = candidate;
        record.Key = newKey;
        Touch();
        return Task.CompletedTask;
    }

    public Task<int> DeleteRecordsAsync(string tableName, IReadOnlyList<string> keyColumns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> keyValues, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (keyValues == null || keyValues.Count == 0) return Task.FromResult(0);
        if (keyColumns == null || keyColumns.Count == 0) throw new InvalidOperationException("Нельзя удалить запись без ключевых столбцов.");
        var resolved = ResolveTableName(tableName);
        var schema = GetSchema(resolved) ?? throw new InvalidOperationException($"Для таблицы «{resolved}» отсутствует проверенная схема.");
        if (!schema.HasStableKey || !SameColumns(schema.KeyColumns, keyColumns))
            throw new InvalidOperationException("Переданные ключевые столбцы не совпадают со стабильным ключом проверенной схемы.");
        EnsureRecordIndex();
        if (!_recordsByTable!.TryGetValue(resolved, out var tableRecords)) return Task.FromResult(0);
        foreach (var key in keyValues)
            if (tableRecords.Count(r => KeyMatches(schema, r, keyColumns, key)) > 1)
                throw new InvalidOperationException("Ключ удаления неоднозначен в локальном проекте; удаление отменено.");
        var targets = tableRecords.Where(r => keyValues.Any(kv => KeyMatches(schema, r, keyColumns, kv))).ToHashSet();
        int removed = _project.LocalRecords.RemoveAll(r => targets.Contains(r));
        if (removed > 0) Touch();
        return Task.FromResult(removed);
    }

    private static string BuildKey(ProjectTableSchema schema, IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object?> values) =>
        ProjectKeyCodec.Encode(schema, keyColumns, name => ProjectKeyCodec.ToNode(values.TryGetValue(name, out var value) ? value : null));

    private static bool KeyMatches(ProjectTableSchema schema, ProjectRecord record, IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object?> keyValues)
    {
        if (keyColumns.Any(key => !keyValues.ContainsKey(key))) return false;
        var actual = ProjectKeyCodec.Encode(schema, keyColumns, name => record.Values[name]);
        var expected = BuildKey(schema, keyColumns, keyValues);
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static bool SameColumns(IReadOnlyList<string> expected, IReadOnlyList<string> actual) =>
        expected.Count == actual.Count && expected.Zip(actual).All(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase));

    private static ProjectColumnSchema FindColumn(ProjectTableSchema schema, string name) =>
        schema.Columns.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Поле «{name}» отсутствует в проверенной схеме.");

    private static void ValidateValues(ProjectTableSchema schema, IReadOnlyDictionary<string, object?> values, bool isCreate, ISet<string>? generatedIdentityColumns)
    {
        foreach (var pair in values)
        {
            var column = FindColumn(schema, pair.Key);
            if (column.IsReadOnly || column.IsComputed || (column.IsIdentity && !(isCreate && generatedIdentityColumns?.Contains(column.Name) == true)))
                throw new InvalidOperationException($"Поле «{column.Name}» доступно только для чтения.");
            var value = pair.Value == DBNull.Value ? null : pair.Value;
            if (value == null)
            {
                if (!column.IsNullable) throw new InvalidOperationException($"Поле «{column.Name}» не допускает пустое значение.");
                continue;
            }
            var type = column.DataType.ToLowerInvariant();
            bool valid = type switch
            {
                "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "uniqueidentifier" => value is string,
                "bit" => value is bool || value is byte b && b <= 1,
                "tinyint" or "smallint" or "int" or "bigint" => IsIntegerInRange(value, type),
                "decimal" or "numeric" or "money" or "smallmoney" => decimal.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _),
                "float" or "real" => double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
                "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => value is DateTime or DateTimeOffset || DateTimeOffset.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out _),
                "binary" or "varbinary" or "image" => value is byte[],
                _ => false
            };
            if (!valid) throw new InvalidOperationException($"Значение поля «{column.Name}» не соответствует типу «{column.DataType}»; локально подтвердить операцию нельзя.");
            if (value is string text && column.MaxLength > 0)
            {
                int limit = type is "nvarchar" or "nchar" ? column.MaxLength / 2 : column.MaxLength;
                if (text.Length > limit) throw new InvalidOperationException($"Значение поля «{column.Name}» превышает допустимую длину.");
            }
        }
        if (isCreate)
            foreach (var column in schema.Columns.Where(c => !c.IsNullable && !c.IsIdentity && !c.IsComputed && !c.IsReadOnly && !c.HasDefault))
                if (!values.ContainsKey(column.Name)) throw new InvalidOperationException($"Для обязательного поля «{column.Name}» не задано значение.");
    }

    private static bool IsIntegerInRange(object value, string type)
    {
        if (!long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var number)) return false;
        return type switch { "tinyint" => number is >= 0 and <= byte.MaxValue, "smallint" => number is >= short.MinValue and <= short.MaxValue, "int" => number is >= int.MinValue and <= int.MaxValue, _ => true };
    }

    /// <summary>Upserts a record in the local project only.</summary>
    public void UpsertRecord(ProjectRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.TableName) || string.IsNullOrWhiteSpace(record.Key))
            throw new ArgumentException("Для локальной записи обязательны имя таблицы и ключ.", nameof(record));
        EnsureRecordIndex();
        var resolvedTable = ResolveTableName(record.TableName);
        var existing = _recordsByTable!.TryGetValue(resolvedTable, out var tableRecords)
            ? tableRecords.FirstOrDefault(x => x.Key.Equals(record.Key, StringComparison.Ordinal))
            : null;
        if (existing == null) _project.LocalRecords.Add(record);
        else
        {
            existing.Values = record.Values;
            existing.RowVersion = record.RowVersion;
        }
        Touch();
    }

    /// <summary>Deletes a record from the local project only.</summary>
    public bool DeleteRecord(string tableName, string key)
    {
        EnsureRecordIndex();
        var resolvedTable = ResolveTableName(tableName);
        if (!_recordsByTable!.TryGetValue(resolvedTable, out var tableRecords)) return false;
        var keysToRemove = tableRecords.Where(x => x.Key.Equals(key, StringComparison.Ordinal)).ToHashSet();
        var removed = _project.LocalRecords.RemoveAll(x => keysToRemove.Contains(x));
        if (removed > 0) Touch();
        return removed > 0;
    }

    /// <summary>
    /// Deletes exactly one row by its persisted snapshot identity. This is used only for
    /// Offline snapshots of tables without a declared stable SQL key; it never matches on
    /// display text or inferred column values.
    /// </summary>
    public bool DeleteSnapshotRecord(string tableName, string snapshotKey)
    {
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("Не задана таблица.", nameof(tableName));
        if (string.IsNullOrWhiteSpace(snapshotKey)) throw new ArgumentException("Не задан внутренний ключ записи локального снимка.", nameof(snapshotKey));
        EnsureRecordIndex();
        var resolvedTable = ResolveTableName(tableName);
        if (!_recordsByTable!.TryGetValue(resolvedTable, out var tableRecords)) return false;
        var matches = tableRecords.Where(x => string.Equals(x.Key, snapshotKey, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) return false;
        if (matches.Count != 1)
            throw new InvalidOperationException("В локальном снимке обнаружены повторяющиеся внутренние ключи записей. Удаление отменено.");
        var target = matches[0];
        int removed = _project.LocalRecords.RemoveAll(x => ReferenceEquals(x, target));
        if (removed != 1)
            throw new InvalidOperationException("Не удалось однозначно определить запись локального снимка. Проект не изменён.");
        Touch();
        return true;
    }

    /// <summary>Replaces a table schema in the local project only.</summary>
    public void UpsertSchema(ProjectTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (string.IsNullOrWhiteSpace(schema.TableName)) throw new ArgumentException("Не задано имя таблицы.", nameof(schema));
        var index = _project.TableSchemas.FindIndex(x => x.TableName.Equals(schema.TableName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) _project.TableSchemas.Add(schema); else _project.TableSchemas[index] = schema;
        Touch();
    }

    private void EnsureRecordIndex()
    {
        if (_recordsByTable != null && _tableNamesByLeaf != null) return;
        _recordsByTable = new Dictionary<string, List<ProjectRecord>>(StringComparer.OrdinalIgnoreCase);
        _tableNamesByLeaf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _project.LocalRecords.Where(x => !string.IsNullOrWhiteSpace(x.TableName))
                     .GroupBy(x => x.TableName, StringComparer.OrdinalIgnoreCase))
        {
            _recordsByTable[group.Key] = group.ToList();
            var leaf = GetLeaf(group.Key);
            if (!_tableNamesByLeaf.TryGetValue(leaf, out var names))
                _tableNamesByLeaf[leaf] = names = new List<string>();
            names.Add(group.Key);
        }
    }

    private static string GetLeaf(string name) => (name ?? string.Empty).Split('.').Last().Trim('[', ']');

    private void InvalidateIndexes()
    {
        _recordsByTable = null;
        _tableNamesByLeaf = null;
    }

    private void Touch()
    {
        InvalidateIndexes();
        if (_suspendNotifications) return;
        _project.Manifest.ModifiedUtc = DateTimeOffset.UtcNow;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }
}
