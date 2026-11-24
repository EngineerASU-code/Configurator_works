using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Configurator
{
    public partial class TableViewModel
    {
        private readonly DatabaseService _db;
        private readonly Func<string, CancellationToken, Task<List<ColumnInfo>>> _loadColumns;
        private readonly Func<string, CancellationToken, Task<List<string>>> _loadKeys;

        public string CurrentTable { get; private set; } = "";
        public List<ColumnInfo> Columns { get; private set; }
        public List<string> KeyColumns { get; private set; } = new();
        public string KeyColumn => KeyColumns.Count > 0 ? KeyColumns[0] : "";
        public bool UsesValueIdentity { get; private set; }
        public bool HasValidKey => KeyColumns.Count > 0 || UsesValueIdentity;
        public bool CanDuplicateCurrentRow => KeyColumns.Count > 0 || Columns?.Any(c =>
            c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase) &&
            !c.IsIdentity && !c.IsReadOnly && IsNumericRecordType(c.DataType)) == true;

        public int CurrentPage { get; set; } = 1;
        public int PageSize { get; } = 100;
        public int TotalCount { get; set; }
        private readonly Dictionary<string, TableColumnViewSettings> _columnViews = new(StringComparer.OrdinalIgnoreCase);
        internal TableColumnViewSettings ColumnView
        {
            get
            {
                if (!_columnViews.TryGetValue(CurrentTable, out var settings))
                    _columnViews.Add(CurrentTable, settings = new TableColumnViewSettings());
                return settings;
            }
        }
        public DataTable CurrentPageData { get; private set; }

        public Dictionary<string, string> CurrentRowValues { get; private set; } = new();
        public Dictionary<string, object> CurrentKeyValues { get; private set; } = new();
        public Dictionary<string, object> CurrentIdentityValues { get; private set; } = new();
        public Dictionary<string, object> CurrentRawRowValues { get; private set; } = new();
        private bool _metadataLoaded;
        private readonly Dictionary<string, (List<ColumnInfo> Columns, List<string> Keys)> _metadataCache =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _countCache =
            new(StringComparer.Ordinal);

        public object CurrentKeyValue => KeyColumns.Count > 0 && CurrentKeyValues.ContainsKey(KeyColumns[0])
            ? CurrentKeyValues[KeyColumns[0]]
            : null;

        public int TotalPages => TotalCount == 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;

        public TableViewModel(string connectionString)
        {
            _db = new DatabaseService(connectionString);
            _loadColumns = _db.GetTableColumnsAsync;
            _loadKeys = _db.GetPrimaryKeyColumnsAsync;
        }

        internal TableViewModel(Func<string, CancellationToken, Task<List<ColumnInfo>>> loadColumns,
            Func<string, CancellationToken, Task<List<string>>> loadKeys)
        {
            _loadColumns = loadColumns;
            _loadKeys = loadKeys;
        }

        // === P1-11: Async ===
        public async Task<DataTable> GetTableListAsync(CancellationToken ct = default)
        {
            return await _db.GetTableListAsync(ct);
        }

        // === P1-11: Async ===
        public async Task SelectTableAsync(string tableName, CancellationToken ct = default)
        {
            if (string.Equals(CurrentTable, tableName, StringComparison.OrdinalIgnoreCase) && _metadataLoaded)
            {
                CurrentPage = 1;
                return;
            }

            CurrentTable = tableName;
            CurrentPage = 1;
            _metadataLoaded = false;

            if (_metadataCache.TryGetValue(tableName, out var cached))
            {
                Columns = cached.Columns;
                KeyColumns = cached.Keys;
                UpdateIdentityMode();
                _metadataLoaded = true;
                return;
            }

            // Метаданные двух независимых запросов получаем параллельно только при первом открытии таблицы.
            var columnsTask = _loadColumns(tableName, ct);
            var keysTask = _loadKeys(tableName, ct);
            await Task.WhenAll(columnsTask, keysTask);
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(CurrentTable, tableName, StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException();
            Columns = columnsTask.Result;
            KeyColumns = keysTask.Result;
            UpdateIdentityMode();
            _metadataCache[tableName] = (Columns, KeyColumns);
            _metadataLoaded = true;
        }

        private void UpdateIdentityMode()
            => UsesValueIdentity = KeyColumns.Count == 0 && Columns?.Count > 0 && Columns.All(IsValueIdentityColumn);

        internal static bool IsValueIdentityColumn(ColumnInfo column)
        {
            string type = (column.DataType ?? string.Empty).Trim().ToLowerInvariant();
            return type is not ("text" or "ntext" or "image" or "xml");
        }

        private static bool IsNumericRecordType(string dataType)
            => (dataType ?? string.Empty).Trim().ToLowerInvariant() is
                "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real";

        // === P1-11: Async ===
        public async Task<DataTable> LoadPageAsync(CancellationToken ct = default)
        {
            var sorts = ColumnView.Sorts.ToList();
            var fallback = KeyColumns.Count > 0 ? KeyColumns : Columns?.Take(1).Select(column => column.Name) ?? Enumerable.Empty<string>();
            foreach (var name in fallback)
            {
                if (sorts.Any(sort => sort.Column.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                var column = Columns?.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                sorts.Add(new TableColumnSort(name, System.ComponentModel.ListSortDirection.Ascending, column?.UsesTextOrdering == true));
            }

            var data = await _db.GetColumnViewPageAsync(CurrentTable, CurrentPage, PageSize, sorts, ColumnView.Filters, ct);
            ct.ThrowIfCancellationRequested();
            CurrentPageData = data;
            return data;
        }

        /// <summary>
        /// После CRUD перезагружается только текущая страница, без COUNT(*).
        /// Это сохраняет скорость интерфейса и позволяет восстановить курсор.
        /// </summary>
        public async Task<DataTable> ReloadCurrentPageAsync(CancellationToken ct = default)
        {
            return await LoadPageAsync(ct);
        }

        public void NotifyInserted(int count = 1)
        {
            if (count > 0) TotalCount += count;
        }

        public async Task RefreshTotalCountAsync(CancellationToken ct = default)
        {
            InvalidateCountCache();
            TotalCount = await GetTotalCountAsync(ct);
        }

        public void NotifyDeleted(int count)
        {
            if (count <= 0) return;
            TotalCount = Math.Max(0, TotalCount - count);
        }

        public Task<int> GetTotalCountAsync(CancellationToken ct = default)
            => ReadTotalCountAsync(useCache: true, ct);

        public Task<int> GetFreshTotalCountAsync(CancellationToken ct = default)
            => ReadTotalCountAsync(useCache: false, ct);

        private async Task<int> ReadTotalCountAsync(bool useCache, CancellationToken ct)
        {
            string table = CurrentTable;
            if (string.IsNullOrWhiteSpace(table)) return 0;
            var filters = ColumnView.Filters;
            if (useCache && filters.Length == 0 && _countCache.TryGetValue(table, out int cached))
                return TotalCount = cached;

            int count = await _db.CountColumnViewRowsAsync(table, filters, ct);
            ct.ThrowIfCancellationRequested();
            if (filters.Length == 0) _countCache[table] = count;
            return TotalCount = count;
        }

        private void InvalidateCountCache() => _countCache.Clear();

        public async Task<PagedResult> LoadPagedDataAsync(CancellationToken ct = default)
        {
            var dataTask = LoadPageAsync(ct);
            var countTask = GetTotalCountAsync(ct);
            await Task.WhenAll(dataTask, countTask);
            return new PagedResult { Data = dataTask.Result, TotalCount = countTask.Result };
        }

        public void SetCurrentRow(DataRowView row)
        {
            CurrentRowValues.Clear();
            CurrentKeyValues.Clear();
            CurrentIdentityValues.Clear();
            CurrentRawRowValues.Clear();

            if (Columns == null) return;

            foreach (var col in Columns)
            {
                if (row.DataView.Table.Columns.Contains(col.Name))
                {
                    var rawValue = row[col.Name];
                    CurrentRawRowValues[col.Name] = rawValue;
                    CurrentRowValues[col.Name] = rawValue == DBNull.Value ? "" : rawValue?.ToString() ?? "";
                }
                else
                {
                    CurrentRowValues[col.Name] = "";
                    CurrentRawRowValues[col.Name] = DBNull.Value;
                }
            }

            foreach (var keyCol in KeyColumns)
            {
                if (row.DataView.Table.Columns.Contains(keyCol))
                    CurrentKeyValues[keyCol] = row[keyCol];
            }

            if (UsesValueIdentity)
            {
                foreach (var col in Columns.Where(IsValueIdentityColumn))
                {
                    if (row.DataView.Table.Columns.Contains(col.Name))
                        CurrentIdentityValues[col.Name] = row[col.Name];
                }
            }
        }


        public void ClearCurrentRow()
        {
            CurrentRowValues.Clear();
            CurrentKeyValues.Clear();
            CurrentIdentityValues.Clear();
            CurrentRawRowValues.Clear();
        }

        // === P1-11: Async ===
        public async Task<int> GetNextRecordValueAsync(CancellationToken ct = default)
        {
            // Record — это отдельный инженерный идентификатор. Он может входить
            // в составной PK (например PLC + Record), поэтому количество PK-
            // столбцов не должно запрещать автогенерацию Record.
            var record = Columns?.FirstOrDefault(c =>
                c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase));

            if (record != null && !record.IsIdentity && !record.IsReadOnly)
            {
                var type = (record.DataType ?? "").Trim().ToLowerInvariant();
                if (type is "tinyint" or "smallint" or "int" or "bigint" or
                    "decimal" or "numeric" or "money" or "smallmoney" or
                    "float" or "real")
                {
                    return await _db.GetMaxRecordValueAsync(CurrentTable, record.Name, ct) + 1;
                }
            }

            // Для обычных таблиц без Record сохраняем старую логику:
            // автогенерируем только одиночный целочисленный ключ.
            if (KeyColumns.Count != 1)
                throw new System.Exception("Автогенерация ключа поддерживается только для одиночного целочисленного ключа или поля Record.");

            var key = Columns?.FirstOrDefault(c =>
                c.Name.Equals(KeyColumns[0], StringComparison.OrdinalIgnoreCase));
            if (key == null || key.IsIdentity || key.IsReadOnly ||
                !string.Equals(key.DataType, "int", StringComparison.OrdinalIgnoreCase))
                throw new System.Exception("Невозможно автоматически определить следующий ключ.");

            return await _db.GetMaxRecordValueAsync(CurrentTable, key.Name, ct) + 1;
        }

        public string RecordColumn => Columns?.FirstOrDefault(c => c.Name.Equals("Record", StringComparison.OrdinalIgnoreCase))?.Name ?? KeyColumn;

        public string PlcColumn => Columns?.FirstOrDefault(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase))?.Name ?? "";

        public bool HasScopedRecord => !string.IsNullOrWhiteSpace(RecordColumn) && !string.IsNullOrWhiteSpace(PlcColumn);

        public bool CanAutoGenerateKey
        {
            get
            {
                if (KeyColumns.Count != 1) return false;
                var keyCol = Columns?.FirstOrDefault(c => c.Name == KeyColumns[0]);
                return keyCol != null && !keyCol.IsIdentity && keyCol.DataType == "int";
            }
        }

        // === P1-11: Async ===
        public async Task InsertAsync(Dictionary<string, object> values, CancellationToken ct = default)
        {
            await _db.InsertRowAsync(CurrentTable, values, ct);
            InvalidateCountCache();
        }

        public async Task<Dictionary<string, object>> DuplicateCurrentRowAsync(CancellationToken ct = default)
        {
            // Если между расчётом Record и INSERT другой клиент успел занять
            // это значение, пересчитываем его несколько раз. Это особенно важно
            // для составного PLC + Record ключа.
            const int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var values = await BuildDuplicateValuesAsync(ct);
                try
                {
                    await InsertAsync(values, ct);
                    return values;
                }
                catch (SqlException ex) when ((ex.Number == 2601 || ex.Number == 2627) && attempt < maxAttempts)
                {
                    // Duplicate key. Перечитываем занятые Record и пробуем ещё раз.
                }
            }

            throw new InvalidOperationException("Не удалось создать копию: значение ключа занято другой записью.");
        }

        // === P1-11: Async ===
        public async Task UpdateAsync(Dictionary<string, object> values, CancellationToken ct = default)
        {
            if (!HasValidKey)
                throw new System.Exception("Не удалось определить идентификатор строки.");

            await UpdateSnapshotAsync(CurrentTable, UsesValueIdentity, KeyColumns.ToList(),
                new Dictionary<string, object>(UsesValueIdentity ? CurrentIdentityValues : CurrentKeyValues),
                new Dictionary<string, object>(CurrentRawRowValues), values, ct);
        }

        public async Task UpdateSnapshotAsync(string table, bool usesValueIdentity, List<string> keyColumns,
            Dictionary<string, object> identity, Dictionary<string, object> originalValues,
            Dictionary<string, object> values, CancellationToken ct = default)
        {

            if (usesValueIdentity)
            {
                if (identity.Count == 0)
                    throw new InvalidOperationException("Не удалось определить исходное состояние строки.");
                await _db.UpdateRowByValuesAsync(table, identity, values, ct);
                InvalidateCountCache();
                return;
            }

            // Keep the old key in WHERE, but include edited key columns in SET. SQL Server's
            // unique constraint rejects a conflicting new key and the update transaction rolls back.
            bool keyIsBeingChanged = keyColumns.Any(k => values.ContainsKey(k) &&
                originalValues.TryGetValue(k, out var oldKey) &&
                !Equals(oldKey ?? DBNull.Value, values[k] ?? DBNull.Value));

            var updateValues = values
                .Where(v => keyIsBeingChanged || !keyColumns.Contains(v.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(v => v.Key, v => v.Value);

            await _db.UpdateRowAsync(table, keyColumns, identity, updateValues, ct,
                new Dictionary<string, object>(originalValues, StringComparer.OrdinalIgnoreCase));
            InvalidateCountCache();
        }

        public async Task<int> DeleteAsync(List<Dictionary<string, object>> keyValuesList, CancellationToken ct = default)
        {
            if (!HasValidKey)
                throw new System.Exception("Не удалось определить идентификатор строки.");

            int deleted;
            if (UsesValueIdentity)
                deleted = await _db.DeleteRowsByValuesAsync(CurrentTable, keyValuesList, ct);
            else
                deleted = await _db.DeleteRowsAsync(CurrentTable, KeyColumns, keyValuesList, ct);

            if (deleted > 0) InvalidateCountCache();
            return deleted;
        }


    }
}
