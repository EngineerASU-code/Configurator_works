#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;

namespace Configurator;

public enum ChangeJournalOperationKind
{
    Insert,
    Update,
    Delete,
    Duplicate,
    Composite,
    LogicalRelation,
    ObjectChange,
    StructureChange,
    ConfigurationChange
}

public sealed class ChangeJournalValue
{
    public bool IsDbNull { get; init; }
    public string TypeName { get; init; } = "";
    public string Json { get; init; } = "";

    public static ChangeJournalValue FromObject(object value)
    {
        if (value == null || value == DBNull.Value)
            return new ChangeJournalValue { IsDbNull = true };

        return new ChangeJournalValue
        {
            TypeName = value.GetType().FullName ?? value.GetType().Name,
            Json = JsonConvert.SerializeObject(value)
        };
    }

    // Only these CLR types may be materialized from journal data. The type name is read from a
    // file (or from a shared .cfgproj), so it must never be passed to Type.GetType().
    private static readonly Dictionary<string, Type> AllowedTypes = BuildAllowedTypes();

    private static Dictionary<string, Type> BuildAllowedTypes()
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in new[]
        {
            typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(char), typeof(string),
            typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid), typeof(byte[])
        })
        {
            map[type.FullName ?? type.Name] = type;   // e.g. "System.Int32"
            map[type.Name] = type;                    // e.g. "Int32"
        }
        return map;
    }

    internal static Type ResolveAllowedType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        // Legacy entries stored AssemblyQualifiedName ("System.Int32, System.Private.CoreLib, ...").
        string key = typeName.Split(',')[0].Trim();
        return AllowedTypes.TryGetValue(key, out var type) ? type : null;
    }

    public object ToObject()
    {
        if (IsDbNull) return DBNull.Value;
        if (string.IsNullOrEmpty(Json)) return null;
        try
        {
            var type = ResolveAllowedType(TypeName);
            // Unknown type names are never instantiated: the value is read as a plain JSON token.
            return type == null ? JsonConvert.DeserializeObject(Json) : JsonConvert.DeserializeObject(Json, type);
        }
        catch
        {
            return Json;
        }
    }
}

public sealed class ChangeJournalRowChange
{
    public string TableName { get; init; } = "";
    public ChangeJournalOperationKind Kind { get; init; }
    public Dictionary<string, ChangeJournalValue> Before { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ChangeJournalValue> After { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ChangeJournalValue> IdentityBefore { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ChangeJournalValue> IdentityAfter { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True when After contains the complete persisted Offline row, including its exact property set.</summary>
    public bool AfterIsCompleteOfflineRecord { get; init; }
    public string CaptureWarning { get; init; } = "";
    [Newtonsoft.Json.JsonIgnore]
    public string KindDisplay => Kind switch
    {
        ChangeJournalOperationKind.Insert => "Добавление",
        ChangeJournalOperationKind.Update => "Изменение",
        ChangeJournalOperationKind.Delete => "Удаление",
        ChangeJournalOperationKind.Duplicate => "Дублирование",
        ChangeJournalOperationKind.Composite => "Составная операция",
        _ => Kind.ToString()
    };
    [Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, ChangeJournalValue> ChangedBefore => FilterChanged(Before, After);
    [Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, ChangeJournalValue> ChangedAfter => FilterChanged(After, Before);
    [Newtonsoft.Json.JsonIgnore]
    public string BeforeText => Format(ChangedBefore);
    [Newtonsoft.Json.JsonIgnore]
    public string AfterText => Format(ChangedAfter);
    [Newtonsoft.Json.JsonIgnore]
    public string ChangeSummary => Kind == ChangeJournalOperationKind.Update
        ? string.Join(", ", ChangedBefore.Keys.Union(ChangedAfter.Keys, StringComparer.OrdinalIgnoreCase).Select(k => $"{k}: {ValueText(Before, k)} → {ValueText(After, k)}"))
        : Kind == ChangeJournalOperationKind.Delete ? FormatIdentity(IdentityBefore)
        : Kind == ChangeJournalOperationKind.Duplicate ? $"Источник: {FormatIdentity(IdentityBefore)} → новый объект: {FormatIdentity(IdentityAfter)}"
        : Kind == ChangeJournalOperationKind.Insert ? FormatIdentity(IdentityAfter) : KindDisplay;
    private static string ValueText(Dictionary<string, ChangeJournalValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) return "—";
        return value?.ToObject() switch
        {
            null or DBNull => "NULL",
            string text when text.Length == 0 => "«»",
            byte[] bytes => Convert.ToHexString(bytes),
            var item => Convert.ToString(item, System.Globalization.CultureInfo.CurrentCulture)
        };
    }
    private static Dictionary<string, ChangeJournalValue> FilterChanged(Dictionary<string, ChangeJournalValue> source, Dictionary<string, ChangeJournalValue> other)
    {
        source ??= new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase);
        other ??= new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase);
        return source.Where(kv => !other.TryGetValue(kv.Key, out var ov) || !ValuesEqual(kv.Value, ov))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }
    private static bool ValuesEqual(ChangeJournalValue a, ChangeJournalValue b)
    {
        var av = a?.ToObject(); var bv = b?.ToObject();
        if (av == null || av == DBNull.Value) return bv == null || bv == DBNull.Value;
        if (bv == null || bv == DBNull.Value) return false;
        if (av is byte[] left && bv is byte[] right) return left.AsSpan().SequenceEqual(right);
        return string.Equals(Convert.ToString(av, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(bv, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
    private static string Format(Dictionary<string, ChangeJournalValue> values)
        => values == null || values.Count == 0 ? "—" : string.Join(", ", values.Keys.Select(key => $"{key}={ValueText(values, key)}"));
    private static string FormatIdentity(Dictionary<string, ChangeJournalValue> values)
        => values == null || values.Count == 0 ? "не определён" : string.Join(", ", values.Keys.Select(key => $"{key}={ValueText(values, key)}"));
}

public sealed class ChangeJournalEntry
{
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string User { get; init; } = Environment.UserName;
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Category { get; init; } = "Действие";
    public string Source { get; init; } = "";
    public string Description { get; init; } = "";
    public string Details { get; init; } = "";
    public string RestoreKind { get; init; } = "";
    public string RestorePayloadJson { get; init; } = "";
    public string UserRole { get; set; } = "";
    public int Mode { get; set; }
    public string ProjectType { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityIdentity { get; set; } = "";
    public string Result { get; set; } = "Успешно";
    public ChangeJournalOperationKind OperationKind { get; init; }
    /// <summary>False when a mutation updated related rows that are not fully represented in Changes.</summary>
    public bool RestoreIncludesAllChanges { get; init; } = true;
    public DateTime? RestoredUtc { get; set; }
    public string RestoredBy { get; set; } = "";
    [Newtonsoft.Json.JsonIgnore]
    public string OperationKindDisplay => OperationKind switch
    {
        ChangeJournalOperationKind.Insert => "Добавление",
        ChangeJournalOperationKind.Update => "Изменение",
        ChangeJournalOperationKind.Delete => "Удаление",
        ChangeJournalOperationKind.Duplicate => "Дублирование",
        ChangeJournalOperationKind.Composite => "Составная операция",
        ChangeJournalOperationKind.LogicalRelation => "Логическая связь",
        ChangeJournalOperationKind.ObjectChange => "Изменение объекта",
        ChangeJournalOperationKind.StructureChange => "Изменение структуры",
        ChangeJournalOperationKind.ConfigurationChange => "Изменение конфигурации",
        _ => OperationKind.ToString()
    };
    [Newtonsoft.Json.JsonIgnore]
    public string DisplayDescription
    {
        get
        {
            var description = (Description ?? "").Trim();
            var changes = Changes ?? new List<ChangeJournalRowChange>();
            var meaningful = !string.IsNullOrWhiteSpace(description) &&
                !string.Equals(description, Category, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(description, "Действие", StringComparison.OrdinalIgnoreCase);
            if (!meaningful)
                description = OperationKindDisplay;

            if (changes.Count == 0) return description;

            var parts = new List<string>();
            var tables = changes.Select(change => change?.TableName?.Trim())
                .Where(table => !string.IsNullOrWhiteSpace(table))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missingTables = tables.Where(table => description.IndexOf(table, StringComparison.OrdinalIgnoreCase) < 0).ToList();
            if (missingTables.Count > 0)
                parts.Add(string.Join(", ", missingTables.Take(2)) + (missingTables.Count > 2 ? $" и ещё {missingTables.Count - 2}" : ""));
            if (changes.Count > 1 && !description.Contains("запис", StringComparison.OrdinalIgnoreCase) && !description.Contains("строк", StringComparison.OrdinalIgnoreCase))
                parts.Add($"{changes.Count} {PluralizeRecords(changes.Count)}");
            else if (changes.Count == 1 && !description.Contains("запис", StringComparison.OrdinalIgnoreCase) && !description.Contains("строк", StringComparison.OrdinalIgnoreCase))
                parts.Add("1 запись");

            return parts.Count == 0 ? description : description + " · " + string.Join(" · ", parts);
        }
    }
    private static string PluralizeRecords(int count)
    {
        var lastTwo = count % 100;
        if (lastTwo is >= 11 and <= 14) return "записей";
        return (count % 10) switch { 1 => "запись", >= 2 and <= 4 => "записи", _ => "записей" };
    }
    [Newtonsoft.Json.JsonIgnore]
    public bool IsRestored => RestoredUtc.HasValue;
    [Newtonsoft.Json.JsonIgnore]
    public string RestorationStatus => RestoredUtc is DateTime restored
        ? $"Отменено {restored.ToLocalTime():dd.MM.yyyy HH:mm:ss}" + (string.IsNullOrWhiteSpace(RestoredBy) ? "" : $" · {RestoredBy}")
        : "";
    [Newtonsoft.Json.JsonIgnore]
    public bool CanRestore
    {
        get
        {
            if (!RestoreIncludesAllChanges || RestoredUtc.HasValue || !string.Equals(Result, "Успешно", StringComparison.OrdinalIgnoreCase)) return false;
            if (RestoreKind == Infrastructure.Database.SavedSqlQueryStore.JournalKind)
                return Infrastructure.Database.SavedSqlQueryStore.ReadRevision(this) != null;
            if (RestoreKind == Infrastructure.Projects.CompareProjectSettingsRestore.RestoreKind)
                return Infrastructure.Projects.CompareProjectSettingsRestore.CanRestore(this);
            if ((Changes?.Count ?? 0) > 0)
                return Changes.All(change => change != null && string.IsNullOrEmpty(change.CaptureWarning) && ProjectWriteGuard.IsDataTableWritable(change.TableName) && (change.Kind switch
                {
                    ChangeJournalOperationKind.Insert or ChangeJournalOperationKind.Duplicate => change.After.Count > 0 && change.IdentityAfter.Count > 0,
                    ChangeJournalOperationKind.Update => change.Before.Count > 0 && change.After.Count > 0 && (change.IdentityAfter.Count > 0 || change.IdentityBefore.Count > 0),
                    ChangeJournalOperationKind.Delete => change.Before.Count > 0 && change.IdentityBefore.Count > 0,
                    _ => false
                }));
            return false;
        }
    }
    public List<ChangeJournalRowChange> Changes { get; init; } = new();
    [Newtonsoft.Json.JsonIgnore]
    public DateTime LocalTime => TimestampUtc.ToLocalTime();
}

/// <summary>
/// Persistent change journal. Files are intentionally kept outside the database so
/// the journal can be archived/cleaned independently from engineering data.
/// </summary>
public sealed partial class ChangeJournalService
{
    private readonly string _baseDirectory;
    private string _rootDirectory;
    private readonly object _sync = new();
    private ConfiguratorProject _offlineProject;
    private Action _offlineChanged;
    private string _contextType = "Online";
    private string _contextServer = "";
    private string _contextDatabase = "";
    private string _contextProjectId = "";
    private string _contextProjectName = "";
    private int _contextMode;

    public ChangeJournalService(string rootDirectory = null)
    {
        bool useDefault = string.IsNullOrWhiteSpace(rootDirectory);
        _baseDirectory = useDefault
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Configurator", "ChangeJournal")
            : rootDirectory;
        _rootDirectory = _baseDirectory;
        if (useDefault) TryMigrateLegacyJournal(Path.Combine(AppContext.BaseDirectory, "ChangeJournal"), _baseDirectory);
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>Journals used to live next to the executable (often read-only under Program Files).</summary>
    private static void TryMigrateLegacyJournal(string legacy, string target)
    {
        try
        {
            if (!Directory.Exists(legacy) || Directory.Exists(target)) return;
            foreach (var file in Directory.EnumerateFiles(legacy, "*.json", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(legacy, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, overwrite: false);
            }
        }
        catch { /* Migration is best-effort; the old files stay in place. */ }
    }

    public event EventHandler Changed;

    public string RootDirectory => _rootDirectory;

    public void BindOnlineProject(string server, string database, string projectName, int mode)
    {
        lock (_sync)
        {
            _offlineProject = null;
            _offlineChanged = null;
            _contextType = "Online";
            _contextServer = server ?? "";
            _contextDatabase = database ?? "";
            _contextProjectName = string.IsNullOrWhiteSpace(projectName) ? (database ?? "") : projectName;
            var storageServer = ConnectionSettings.ProjectServerName(server);
            _contextProjectId = "online:" + storageServer.ToLowerInvariant() + "/" + _contextDatabase.Trim().ToLowerInvariant();
            _contextMode = mode;
            _rootDirectory = Path.Combine(_baseDirectory, "Online", SafePath(storageServer), SafePath(_contextDatabase));
            Directory.CreateDirectory(_rootDirectory);
        }
    }

    public void BindOfflineProject(ConfiguratorProject project, Action changed, int mode)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));
        lock (_sync)
        {
            _offlineProject = project;
            _offlineChanged = changed;
            _contextType = "Offline";
            _contextServer = "";
            _contextDatabase = "";
            _contextProjectId = project.Manifest?.ProjectId ?? "";
            _contextProjectName = project.Manifest?.Name ?? "";
            _contextMode = mode;
            _rootDirectory = _baseDirectory;
        }
    }

    private static string SafePath(string value)
    {
        var safe = string.Concat((value ?? "").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(safe) ? "_unknown" : safe;
    }

    private ChangeJournalEntry WithContext(ChangeJournalEntry entry)
    {
        entry.ProjectType = string.IsNullOrWhiteSpace(entry.ProjectType) ? _contextType : entry.ProjectType;
        entry.ProjectId = string.IsNullOrWhiteSpace(entry.ProjectId) ? _contextProjectId : entry.ProjectId;
        entry.ProjectName = string.IsNullOrWhiteSpace(entry.ProjectName) ? _contextProjectName : entry.ProjectName;
        entry.Mode = entry.Mode == 0 ? _contextMode : entry.Mode;
        entry.Server = string.IsNullOrWhiteSpace(entry.Server) ? _contextServer : entry.Server;
        entry.Database = string.IsNullOrWhiteSpace(entry.Database) ? _contextDatabase : entry.Database;
        return entry;
    }

    private static JsonArray GetOfflineJournal(ConfiguratorProject project, bool create)
    {
        if (project.Architecture == null) project.Architecture = new JsonObject();
        if (project.Architecture["changeJournal"] is JsonArray array) return array;
        if (!create) return new JsonArray();
        var created = new JsonArray();
        project.Architecture["changeJournal"] = created;
        return created;
    }

    public bool Record(ChangeJournalEntry entry)
    {
        if (entry == null) return false;
        try
        {
            lock (_sync)
            {
                WithContext(entry);
                var json = JsonConvert.SerializeObject(entry, Formatting.Indented);
                if (_offlineProject != null)
                {
                    var journal = GetOfflineJournal(_offlineProject, true);
                    if (journal.Any(node => string.Equals(node?["OperationId"]?.ToString(), entry.OperationId, StringComparison.Ordinal)))
                        return true;
                    journal.Add(JsonNode.Parse(json));
                    _offlineChanged?.Invoke();
                    Changed?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                Directory.CreateDirectory(_rootDirectory);
                var stamp = entry.TimestampUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");
                var path = Path.Combine(_rootDirectory, $"{stamp}_{entry.OperationId}.json");
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }
        catch (Exception ex)
        {
            // The data mutation may already be committed; report journal failure to the caller.
            System.Diagnostics.Debug.WriteLine($"[ChangeJournal] write failed: {ex}");
            return false;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_offlineProject != null)
            {
                GetOfflineJournal(_offlineProject, false).Clear();
                _offlineChanged?.Invoke();
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    ChangeJournalEntry entry;
                    try { entry = JsonConvert.DeserializeObject<ChangeJournalEntry>(File.ReadAllText(file)); }
                    catch (JsonException) { continue; }
                    if (!string.IsNullOrEmpty(_contextProjectId) &&
                        string.Equals(entry?.ProjectId, _contextProjectId, StringComparison.OrdinalIgnoreCase))
                        File.Delete(file);
                }
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ChangeJournalEntry> LoadRecent(int maxCount = 500)
    {
        try
        {
            lock (_sync)
            {
                if (_offlineProject != null)
                {
                    return GetOfflineJournal(_offlineProject, false)
                        .Select(node => { try { return JsonConvert.DeserializeObject<ChangeJournalEntry>(node?.ToJsonString() ?? ""); } catch { return null; } })
                        .Where(entry => entry != null)
                        .OrderByDescending(entry => entry.TimestampUtc)
                        .Take(Math.Max(1, maxCount)).ToList();
                }
            }
            if (!Directory.Exists(_rootDirectory)) return Array.Empty<ChangeJournalEntry>();
            var files = Directory.EnumerateFiles(_rootDirectory, "*.json", SearchOption.TopDirectoryOnly);
            var result = new List<ChangeJournalEntry>();
            foreach (var file in files)
            {
                try
                {
                    var entry = JsonConvert.DeserializeObject<ChangeJournalEntry>(File.ReadAllText(file));
                    if (entry != null) result.Add(entry);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChangeJournal] read failed '{file}': {ex.Message}");
                }
            }
            return result.OrderByDescending(entry => entry.TimestampUtc)
                .Take(Math.Max(1, maxCount)).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChangeJournal] enumerate failed: {ex.Message}");
            return Array.Empty<ChangeJournalEntry>();
        }
    }

    /// <summary>Returns the newest persisted operation that has not already been undone, regardless of mode.</summary>
    public ChangeJournalEntry LoadLatestPending()
        => LoadRecent(int.MaxValue).FirstOrDefault(entry => !entry.RestoredUtc.HasValue);

    /// <summary>
    /// Deletes old journal files and optionally trims by total size. This is intentionally
    /// explicit: the application never silently purges history.
    /// </summary>
    public int Cleanup(TimeSpan? olderThan = null, long? maxBytes = null)
    {
        if (!Directory.Exists(_rootDirectory)) return 0;
        var files = Directory.EnumerateFiles(_rootDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(x => x.LastWriteTimeUtc)
            .ToList();
        var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (olderThan.HasValue)
        {
            var cutoff = DateTime.UtcNow - olderThan.Value;
            foreach (var file in files.Where(x => x.LastWriteTimeUtc < cutoff)) remove.Add(file.FullName);
        }
        if (maxBytes.HasValue && maxBytes.Value >= 0)
        {
            long total = files.Sum(x => x.Length);
            foreach (var file in files)
            {
                if (total <= maxBytes.Value) break;
                if (remove.Contains(file.FullName)) { total -= file.Length; continue; }
                remove.Add(file.FullName);
                total -= file.Length;
            }
        }
        var deleted = 0;
        foreach (var path in remove)
        {
            try { File.Delete(path); deleted++; } catch { }
        }
        return deleted;
    }

    public void MarkRestored(ChangeJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            entry.RestoredUtc = DateTime.UtcNow;
            entry.RestoredBy = UserAuthorizationService.CurrentUser?.UserName ?? Environment.UserName;
            var json = JsonConvert.SerializeObject(entry, Formatting.Indented);
            if (_offlineProject != null)
            {
                var journal = GetOfflineJournal(_offlineProject, false);
                var node = journal.FirstOrDefault(item => string.Equals(item?["OperationId"]?.ToString(), entry.OperationId, StringComparison.Ordinal));
                if (node == null) throw new InvalidOperationException("Исходная операция отсутствует в журнале проекта.");
                journal[journal.IndexOf(node)] = JsonNode.Parse(json);
                _offlineChanged?.Invoke();
            }
            else
            {
                var file = Directory.EnumerateFiles(_rootDirectory, "*.json")
                    .SingleOrDefault(path => Path.GetFileNameWithoutExtension(path).EndsWith("_" + entry.OperationId, StringComparison.Ordinal));
                if (file == null) throw new InvalidOperationException("Изменения БД отменены, но исходная запись журнала не найдена.");
                try
                {
                    File.WriteAllText(file + ".tmp", json);
                    File.Move(file + ".tmp", file, true);
                }
                catch (IOException ex)
                {
                    throw new IOException("Изменения БД отменены, но сохранить отметку об отмене в журнале не удалось.", ex);
                }
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreOffline(ChangeJournalEntry entry, ConfiguratorProject project, Action changed)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (project == null) throw new ArgumentNullException(nameof(project));
        if (!string.Equals(entry.ProjectType, "Offline", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Запись журнала не относится к Offline-проекту.");
        if (string.IsNullOrWhiteSpace(entry.ProjectId) || !string.Equals(entry.ProjectId, project.Manifest?.ProjectId, StringComparison.Ordinal))
            throw new InvalidOperationException("Запись принадлежит другому Offline-проекту. Восстановление отменено.");
        if (!entry.CanRestore) throw new InvalidOperationException("Для операции нет полного снимка для безопасной отмены.");
        if (entry.Changes == null || entry.Changes.Count == 0)
            throw new InvalidOperationException("В записи отсутствуют данные, достаточные для локального восстановления.");

        lock (_sync)
        {
            var working = project.LocalRecords.Select(CloneRecord).ToList();
            if (entry.RestoreKind == ChangeJournalRestoreKind.ProgramStepMove)
                RestoreOfflineProgramStepMove(entry, working, project.TableSchemas);
            else
            {
                var staged = new ConfiguratorProject { Manifest = project.Manifest, TableSchemas = project.TableSchemas,
                    Architecture = project.Architecture, LocalRecords = working };
                foreach (var change in entry.Changes.AsEnumerable().Reverse())
                    RestoreOfflineChange(staged, change);
            }
            project.LocalRecords = working;
            changed?.Invoke();
        }
    }

    private static Configurator.Core.Projects.ProjectRecord CloneRecord(Configurator.Core.Projects.ProjectRecord record)
        => new()
        {
            TableName = record.TableName,
            Key = record.Key,
            RowVersion = record.RowVersion,
            Values = (JsonObject)(record.Values?.DeepClone() ?? new JsonObject())
        };

    private static void RestoreOfflineChange(
        ConfiguratorProject project, ChangeJournalRowChange change)
    {
        var records = project.LocalRecords;
        var schemas = project.TableSchemas;
        if (string.IsNullOrWhiteSpace(change.TableName))
            throw new InvalidOperationException("В записи отсутствует таблица изменённой сущности.");
        var table = ResolveOfflineTable(records, schemas, change.TableName);
        ProjectWriteGuard.EnsureDataTableWritable(table);
        var tableRows = records.Where(r => r.TableName.Equals(table, StringComparison.OrdinalIgnoreCase)).ToList();
        var identityAfter = Deserialize(change.IdentityAfter ?? new());
        var identityBefore = Deserialize(change.IdentityBefore ?? new());
        var before = Deserialize(change.Before ?? new());
        var after = Deserialize(change.After ?? new());

        switch (change.Kind)
        {
            case ChangeJournalOperationKind.Insert:
            case ChangeJournalOperationKind.Duplicate:
            {
                var matches = FindOfflineRows(tableRows, identityAfter, after);
                if (matches.Count != 1)
                    throw new InvalidOperationException(matches.Count == 0
                        ? $"Созданная запись в {table} не найдена. Возможно, она уже изменена или удалена."
                        : $"В {table} найдено несколько подходящих записей; безопасное восстановление невозможно.");
                EnsureOfflineValuesMatch(matches[0], after, table, "Созданная запись изменилась после исходной операции");
                if (change.AfterIsCompleteOfflineRecord &&
                    !matches[0].Values.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(after.Keys))
                    throw new InvalidOperationException($"Набор полей созданной записи в {table} изменился после операции. Восстановление отменено.");
                var target = matches[0].Values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value,
                    StringComparer.OrdinalIgnoreCase);
                OfflineObjectDeletionGuard.Check(new OfflineProjectDataSource(project), table, new[] { target });
                records.Remove(matches[0]);
                break;
            }
            case ChangeJournalOperationKind.Delete:
            {
                if (before.Count == 0)
                    throw new InvalidOperationException($"В журнале нет снимка удалённой строки {table}.");
                var matches = FindOfflineRows(tableRows, identityBefore, before);
                if (matches.Count > 0)
                    throw new InvalidOperationException($"В {table} уже существует запись с исходным идентификатором. Возможен конфликт восстановления.");
                var schema = schemas.FirstOrDefault(x => x.TableName.Equals(table, StringComparison.OrdinalIgnoreCase));
                var keyColumns = schema?.KeyColumns ?? new List<string>();
                var key = identityBefore.TryGetValue("__ProjectRecordKey", out var projectKey)
                    ? Convert.ToString(projectKey, System.Globalization.CultureInfo.InvariantCulture) ?? ""
                    : BuildOfflineRecordKey(schema, keyColumns, before);
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException($"Не удалось восстановить устойчивый ключ строки {table}.");
                var values = new JsonObject();
                foreach (var pair in before)
                    values[pair.Key] = pair.Value == null || pair.Value == DBNull.Value
                        ? null : System.Text.Json.JsonSerializer.SerializeToNode(pair.Value);
                records.Add(new Configurator.Core.Projects.ProjectRecord { TableName = table, Key = key, Values = values });
                break;
            }
            case ChangeJournalOperationKind.Update:
            {
                var lookup = identityAfter.Count > 0 ? identityAfter : identityBefore;
                var matches = FindOfflineRows(tableRows, lookup, after);
                if (matches.Count != 1)
                    throw new InvalidOperationException(matches.Count == 0
                        ? $"Изменённая запись в {table} не найдена. Восстановление отменено."
                        : $"В {table} найдено несколько подходящих записей. Восстановление отменено.");
                var row = matches[0];
                var changedFields = change.ChangedAfter.Keys.ToList();
                if (change.AfterIsCompleteOfflineRecord)
                    changedFields = changedFields.Union(change.ChangedBefore.Keys, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var field in changedFields)
                {
                    var expected = after.TryGetValue(field, out var value) ? value : null;
                    var actual = row.Values[field]?.ToString();
                    if ((change.AfterIsCompleteOfflineRecord && row.Values.ContainsKey(field) != after.ContainsKey(field)) ||
                        !OfflineValueEquals(actual, expected))
                        throw new InvalidOperationException($"Конфликт восстановления: поле «{field}» записи {table} изменилось после исходной операции.");
                }
                foreach (var field in changedFields)
                {
                    if (change.AfterIsCompleteOfflineRecord && !before.ContainsKey(field))
                    {
                        row.Values.Remove(field);
                        continue;
                    }
                    if (!before.TryGetValue(field, out var oldValue))
                        throw new InvalidOperationException($"В журнале нет прежнего значения поля «{field}».");
                    row.Values[field] = oldValue == null || oldValue == DBNull.Value
                        ? null : System.Text.Json.JsonSerializer.SerializeToNode(oldValue);
                }
                var schema = schemas.FirstOrDefault(x => x.TableName.Equals(table, StringComparison.OrdinalIgnoreCase));
                if (!identityAfter.ContainsKey("__ProjectRecordKey") && !identityBefore.ContainsKey("__ProjectRecordKey"))
                {
                    row.Key = BuildOfflineRecordKey(schema,
                        schema?.KeyColumns ?? new List<string>(),
                        row.Values.ToDictionary(k => k.Key, v => (object)v.Value?.ToString(), StringComparer.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(row.Key))
                        throw new InvalidOperationException($"Не удалось пересчитать ключ строки {table} после восстановления.");
                }
                break;
            }
            default:
                throw new InvalidOperationException($"Операция {change.Kind} пока не поддерживает безопасное Offline-восстановление.");
        }
    }

    private static string ResolveOfflineTable(
        List<Configurator.Core.Projects.ProjectRecord> records,
        List<Configurator.Core.Projects.ProjectTableSchema> schemas,
        string requested)
    {
        var known = records.Select(r => r.TableName).Concat(schemas.Select(s => s.TableName))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var exact = known.FirstOrDefault(x => x.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var leaf = requested.Split('.').Last().Trim('[', ']');
        var matches = known.Where(x => x.Split('.').Last().Trim('[', ']').Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) throw new InvalidOperationException($"Таблица «{requested}» не разрешается однозначно в локальном проекте.");
        return matches[0];
    }

    private static List<Configurator.Core.Projects.ProjectRecord> FindOfflineRows(
        List<Configurator.Core.Projects.ProjectRecord> rows,
        Dictionary<string, object> identity,
        Dictionary<string, object> snapshot)
    {
        if (identity.TryGetValue("__ProjectRecordKey", out var key))
        {
            var byKey = rows.Where(r => r.Key.Equals(Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToList();
            if (byKey.Count > 0) return byKey;
            // Legacy journal keys may predate the versioned codec. Fall back to the
            // captured row values, which are still checked before any restore commit.
            if (snapshot.Count == 0) return new();
            return rows.Where(row => snapshot.All(pair => row.Values.TryGetPropertyValue(pair.Key, out var node) &&
                OfflineValueEquals(node?.ToString(), pair.Value))).ToList();
        }
        var criteria = identity.Count > 0 ? identity : snapshot;
        if (criteria.Count == 0) return new();
        return rows.Where(row => criteria.All(pair => row.Values.TryGetPropertyValue(pair.Key, out var node) &&
            OfflineValueEquals(node?.ToString(), pair.Value))).ToList();
    }

    private static void EnsureOfflineValuesMatch(
        Configurator.Core.Projects.ProjectRecord row, Dictionary<string, object> expected, string table, string message)
    {
        foreach (var pair in expected)
            if (!row.Values.TryGetPropertyValue(pair.Key, out var node) || !OfflineValueEquals(node?.ToString(), pair.Value))
                throw new InvalidOperationException($"{message}: {table}.{pair.Key}. Восстановление отменено.");
    }

    private static bool OfflineValueEquals(string actual, object expected)
    {
        var expectedText = expected == null || expected == DBNull.Value ? null : Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture);
        if (actual == null || expectedText == null) return actual == null && expectedText == null;
        if (expected is bool expectedBoolean && bool.TryParse(actual, out var actualBoolean))
            return actualBoolean == expectedBoolean;
        return string.Equals(actual, expectedText, StringComparison.Ordinal);
    }

    private static string BuildOfflineRecordKey(Configurator.Core.Projects.ProjectTableSchema? schema,
        IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object> values)
    {
        if (schema == null || keyColumns == null || keyColumns.Count == 0 || keyColumns.Any(k => !values.ContainsKey(k))) return "";
        return Configurator.Core.Projects.ProjectKeyCodec.Encode(schema, keyColumns,
            name => Configurator.Core.Projects.ProjectKeyCodec.ToNode(values.TryGetValue(name, out var value) ? value : null));
    }

    public async Task RestoreAsync(ChangeJournalEntry entry, string connectionString, CancellationToken ct = default)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (!entry.CanRestore) throw new InvalidOperationException("Для операции нет полного снимка для безопасной отмены.");
        if (entry.RestoreKind == Infrastructure.Database.SavedSqlQueryStore.JournalKind)
        {
            await new Infrastructure.Database.SavedSqlQueryStore(connectionString, entry.Database).RestoreAsync(entry, ct);
            return;
        }
        if (entry.RestoreKind == ChangeJournalRestoreKind.ProgramStepMove)
        {
            await RestoreProgramStepMoveAsync(entry, connectionString, ct);
            return;
        }
        if (entry.Changes.Count == 0) throw new InvalidOperationException("В журнале нет данных для восстановления.");
        foreach (var change in entry.Changes) ProjectWriteGuard.EnsureDataTableWritable(change.TableName);
        var schema = await new DatabaseArchitectureAnalyzer(connectionString).AnalyzeAsync(ct);
        var protection = new DeletionProtectionService(connectionString, schema);
        await using var connection = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(connectionString);
        await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(connection, ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            foreach (var change in entry.Changes.AsEnumerable().Reverse())
                await RestoreRowAsync(connection, transaction, protection, change, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task RestoreRowAsync(SqlConnection connection, SqlTransaction transaction, DeletionProtectionService protection, ChangeJournalRowChange change, CancellationToken ct)
    {
        switch (change.Kind)
        {
            case ChangeJournalOperationKind.Insert:
            case ChangeJournalOperationKind.Duplicate:
                await protection.DeleteUnreferencedInTransactionAsync(connection, transaction, change.TableName, Deserialize(change.IdentityAfter), Deserialize(change.After), ct);
                break;
            case ChangeJournalOperationKind.Delete:
                if (await ExistsByIdentityAsync(connection, transaction, change.TableName, Deserialize(change.IdentityBefore), ct))
                    throw new InvalidOperationException($"Конфликт восстановления {change.TableName}: запись с исходным ключом уже существует.");
                await InsertAsync(connection, transaction, change.TableName, Deserialize(change.Before), ct);
                break;
            case ChangeJournalOperationKind.Update:
                await UpdateByIdentityAsync(connection, transaction, change.TableName,
                    Deserialize(change.IdentityAfter), Deserialize(change.Before), Deserialize(change.After), ct);
                break;
            default:
                throw new InvalidOperationException($"Неподдерживаемый тип восстановления: {change.Kind}.");
        }
    }

    private static Dictionary<string, object> Deserialize(Dictionary<string, ChangeJournalValue> values)
        => values.ToDictionary(x => x.Key, x => x.Value?.ToObject() ?? DBNull.Value, StringComparer.OrdinalIgnoreCase);

    private static string QuoteTable(string tableName)
    {
        var parts = (tableName ?? "").Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];
        return $"[{schema.Replace("]", "]]" )}].[{table.Replace("]", "]]" )}]";
    }

    private static string QuoteColumn(string name) => $"[{(name ?? "").Replace("]", "]]" )}]";

    private static async Task<bool> ExistsByIdentityAsync(SqlConnection c, SqlTransaction t, string table, Dictionary<string, object> identity, CancellationToken ct)
    {
        if (identity.Count == 0) throw new InvalidOperationException($"Для {table} отсутствует идентификатор строки.");
        var where = new List<string>();
        await using var cmd = new SqlCommand { Connection = c, Transaction = t };
        int i = 0;
        foreach (var pair in identity)
        {
            if (pair.Value == null || pair.Value == DBNull.Value) where.Add($"{QuoteColumn(pair.Key)} IS NULL");
            else { var p = "@p" + i++; where.Add($"{QuoteColumn(pair.Key)} = {p}"); cmd.Parameters.AddWithValue(p, pair.Value); }
        }
        cmd.CommandText = $"SELECT COUNT_BIG(1) FROM {QuoteTable(table)} WHERE {string.Join(" AND ", where)}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task InsertAsync(SqlConnection c, SqlTransaction t, string table, Dictionary<string, object> values, CancellationToken ct)
    {
        if (values.Count == 0) throw new InvalidOperationException($"Нет снимка строки для восстановления {table}.");
        var writable = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
            if (!await IsComputedColumnAsync(c, t, table, pair.Key, ct)) writable[pair.Key] = pair.Value;
        if (writable.Count == 0) throw new InvalidOperationException($"Нет сохраняемых полей для восстановления {table}: все поля вычисляемые.");
        var identityColumns = new List<string>();
        foreach (var column in writable.Keys)
            if (await IsIdentityColumnAsync(c, t, table, column, ct)) identityColumns.Add(column);
        var cols = writable.Keys.ToList();
        await using var cmd = new SqlCommand { Connection = c, Transaction = t };
        var names = new List<string>(); var pars = new List<string>();
        for (int i = 0; i < cols.Count; i++) { names.Add(QuoteColumn(cols[i])); var p = "@p" + i; pars.Add(p); cmd.Parameters.AddWithValue(p, writable[cols[i]] ?? DBNull.Value); }
        cmd.CommandText = $"INSERT INTO {QuoteTable(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", pars)})";
        var identityInsertEnabled = identityColumns.Count > 0;
        if (identityInsertEnabled)
            await ExecuteJournalCommandAsync(c, t, $"SET IDENTITY_INSERT {QuoteTable(table)} ON", ct);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (identityInsertEnabled)
                await ExecuteJournalCommandAsync(c, t, $"SET IDENTITY_INSERT {QuoteTable(table)} OFF", CancellationToken.None);
        }
    }

    private static async Task ExecuteJournalCommandAsync(SqlConnection c, SqlTransaction t, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, c, t);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool JournalValuesEqual(object a, object b)
    {
        if (a == null || a == DBNull.Value) return b == null || b == DBNull.Value;
        if (b == null || b == DBNull.Value) return false;
        if (a is byte[] left && b is byte[] right) return left.AsSpan().SequenceEqual(right);
        return string.Equals(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(b, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static async Task<bool> IsComputedColumnAsync(SqlConnection c, SqlTransaction t, string table, string column, CancellationToken ct)
    {
        var parts = (table ?? "").Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var name = parts.Length == 2 ? parts[1] : parts[0];
        await using var cmd = new SqlCommand(@"SELECT CASE WHEN OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table)) IS NULL THEN 0
            WHEN EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table)) AND c.name = @column AND (c.is_computed = 1 OR c.system_type_id = 189 OR c.generated_always_type <> 0)) THEN 1 ELSE 0 END", c, t);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", name);
        cmd.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task EnsureCurrentValuesMatchAsync(SqlConnection c, SqlTransaction t, string table,
        Dictionary<string, object> identity, Dictionary<string, object> expected, IReadOnlyList<string> fields, CancellationToken ct)
    {
        if (identity.Count == 0 || fields.Count == 0)
            throw new InvalidOperationException($"Недостаточно идентификаторов для проверки конфликта восстановления {table}.");
        var where = new List<string>();
        await using var cmd = new SqlCommand { Connection = c, Transaction = t };
        int parameterIndex = 0;
        foreach (var pair in identity)
        {
            if (pair.Value == null || pair.Value == DBNull.Value) where.Add($"{QuoteColumn(pair.Key)} IS NULL");
            else
            {
                var parameter = "@k" + parameterIndex++;
                where.Add($"{QuoteColumn(pair.Key)} = {parameter}");
                cmd.Parameters.AddWithValue(parameter, pair.Value);
            }
        }
        var selected = fields.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        cmd.CommandText = $"SELECT TOP (2) {string.Join(", ", selected.Select(QuoteColumn))} FROM {QuoteTable(table)} WHERE {string.Join(" AND ", where)}";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException($"Конфликт восстановления {table}: исходная запись больше не существует.");
        var actual = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < selected.Count; i++) actual[selected[i]] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
        if (await reader.ReadAsync(ct)) throw new InvalidOperationException($"Конфликт восстановления {table}: идентификатор соответствует нескольким строкам.");
        foreach (var field in selected)
        {
            if (!expected.TryGetValue(field, out var expectedValue) || !actual.TryGetValue(field, out var actualValue) || !JournalValuesEqual(actualValue, expectedValue))
                throw new InvalidOperationException($"Конфликт восстановления {table}: поле «{field}» изменилось после исходной операции. Данные не перезаписаны.");
        }
    }

    private static async Task<bool> IsIdentityColumnAsync(SqlConnection c, SqlTransaction t, string table, string column, CancellationToken ct)
    {
        var parts = (table ?? "").Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var name = parts.Length == 2 ? parts[1] : parts[0];
        await using var cmd = new SqlCommand(@"SELECT CASE WHEN OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table)) IS NULL THEN 0
            WHEN EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table)) AND c.name = @column AND c.is_identity = 1) THEN 1 ELSE 0 END", c, t);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", name);
        cmd.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task UpdateByIdentityAsync(SqlConnection c, SqlTransaction t, string table, Dictionary<string, object> identity, Dictionary<string, object> before, Dictionary<string, object> after, CancellationToken ct)
    {
        if (identity.Count == 0 || before.Count == 0) throw new InvalidOperationException($"Недостаточно данных для восстановления {table}.");
        var set = new List<string>(); var where = new List<string>();
        await using var cmd = new SqlCommand { Connection = c, Transaction = t };
        // Restore only fields that differ between snapshots. This avoids writing untouched computed
        // columns (for example Area on a projection/view) during Ctrl+Z.
        var changed = before.Where(pair => !after.TryGetValue(pair.Key, out var current) || !JournalValuesEqual(pair.Value, current)).ToList();
        if (changed.Count == 0) return;
        // SQL Server rejects writes to computed columns. Filter them before conflict checks
        // so derived fields do not block restoration of the actual writable changes.
        var writable = new List<KeyValuePair<string, object>>();
        foreach (var pair in changed)
            if (!await IsComputedColumnAsync(c, t, table, pair.Key, ct)) writable.Add(pair);
        changed = writable;
        if (changed.Count == 0) return;
        var expectedCurrent = changed.Where(pair => after.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => after[pair.Key], StringComparer.OrdinalIgnoreCase);
        if (expectedCurrent.Count != changed.Count)
            throw new InvalidOperationException($"В журнале недостаточно значений после изменения для безопасного восстановления {table}.");
        await EnsureCurrentValuesMatchAsync(c, t, table, identity, expectedCurrent, expectedCurrent.Keys.ToList(), ct);
        int i = 0;
        foreach (var pair in changed)
        {
            var p = "@v" + i++;
            set.Add($"{QuoteColumn(pair.Key)} = {p}");
            cmd.Parameters.AddWithValue(p, pair.Value ?? DBNull.Value);
        }
        i = 0;
        foreach (var pair in identity)
        {
            if (pair.Value == null || pair.Value == DBNull.Value) where.Add($"{QuoteColumn(pair.Key)} IS NULL");
            else { var p = "@k" + i++; where.Add($"{QuoteColumn(pair.Key)} = {p}"); cmd.Parameters.AddWithValue(p, pair.Value); }
        }
        cmd.CommandText = $"UPDATE {QuoteTable(table)} SET {string.Join(", ", set)} WHERE {string.Join(" AND ", where)}";
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        if (affected != 1) throw new InvalidOperationException($"Восстановление {table} затронуло {affected} строк вместо одной.");
    }
}
