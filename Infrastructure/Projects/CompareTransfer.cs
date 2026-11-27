#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Compare;
using Configurator.Core.Projects;

namespace Configurator.Infrastructure.Projects;

public static class CompareTransfer
{
    public static Task ApplyMetadataAsync(CompareMetadataTransferPreview preview, CancellationToken cancellationToken = default) =>
        ApplyMetadataCoreAsync(preview, cancellationToken, checkUserPermission: true);

    internal static Task ApplyMetadataToTargetAsync(CompareMetadataTransferPreview preview, CancellationToken cancellationToken = default) =>
        ApplyMetadataCoreAsync(preview, cancellationToken, checkUserPermission: false);

    private static async Task ApplyMetadataCoreAsync(CompareMetadataTransferPreview preview, CancellationToken cancellationToken,
        bool checkUserPermission)
    {
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();
        if (preview.Kind is not (CompareMetadataKind.ProjectTextList or CompareMetadataKind.ThemePreset))
            throw new CompareTransferException("Перенос этого типа настроек отключён.");
        if (!preview.IsAllowed) throw new CompareTransferException(preview.BlockReason ?? "Перенос заблокирован.");
        if (!preview.Target.CanWrite || preview.Target.Workspace.IsReadOnly)
            throw new CompareTransferException("Целевой источник недоступен для записи.");
        if (checkUserPermission)
        {
            if (!UserAuthorizationService.Has(Permission.Mode2Manage) && !UserAuthorizationService.Has(Permission.ManageTextLists))
                throw new CompareTransferException("Нет разрешения изменять этот тип настроек.");
        }

        await VerifyOfflineSnapshotAsync(preview.Source, cancellationToken).ConfigureAwait(false);

        if (preview.Kind is CompareMetadataKind.ProjectTextList or CompareMetadataKind.ThemePreset)
        {
            var sourceSettings = await CompareMetadataTransferPreview.ReadProjectSettingsAsync(preview.Source, cancellationToken).ConfigureAwait(false)
                ?? throw new CompareTransferException("Не удалось прочитать исходные проектные настройки.");
            if (!CompareMetadataTransferPreview.FingerprintProjectSettings(sourceSettings).Equals(preview.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new CompareTransferException("Исходные проектные настройки изменились после сравнения. Повторите сравнение.");

            if (preview.Target.Workspace.IsOffline)
            {
                if (string.IsNullOrWhiteSpace(preview.Target.PackagePath) || string.IsNullOrWhiteSpace(preview.Target.PackageFingerprint))
                    throw new CompareTransferException("Не удалось проверить целевой локальный проект. Повторите сравнение.");
                string path = Path.GetFullPath(preview.Target.PackagePath);
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0)
                    throw new CompareTransferException("Целевой локальный проект доступен только для чтения.");
                await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 81920, useAsync: true);
                var (project, fingerprint) = await ProjectPackageService.ReadForCompareAsync(file, cancellationToken).ConfigureAwait(false);
                if (!fingerprint.Equals(preview.Target.PackageFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new CompareTransferException("Локальный проект изменился после сравнения. Повторите сравнение.");
                var currentTarget = CompareProjectSettingsRestore.CloneSettings(
                    new ProjectCompareSettings(project.TextLists, project.ThemePresets));
                if (!CompareMetadataTransferPreview.FingerprintProjectSettings(currentTarget).Equals(preview.TargetFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new CompareTransferException("Целевые проектные настройки изменились после сравнения. Повторите сравнение.");
                var lists = currentTarget.TextLists.ToList();
                var themes = currentTarget.ThemePresets.Select(item => item.Clone()).ToList();
                ApplyProjectSettings(lists, themes, preview);
                var after = new ProjectCompareSettings(lists, themes);
                var entry = CompareProjectSettingsRestore.CreateEntry(preview, currentTarget, after);
                project.TextLists = lists;
                project.ThemePresets = themes;
                var journal = new ChangeJournalService();
                journal.BindOfflineProject(project, changed: () => { }, mode: 2);
                if (!journal.Record(entry))
                    throw new CompareTransferException("Настройки обновлены, но запись журнала не подготовлена. Изменение локального проекта отменено.");
                string savedFingerprint = await new ProjectPackageService().SaveForCompareAsync(path, project, file, cancellationToken).ConfigureAwait(false);
                preview.SavedPackage = (project, savedFingerprint);
                preview.RecordedEntry = entry;
            }
            else
            {
                if (preview.Target.SaveProjectSettingsAsync is null)
                    throw new CompareTransferException("Целевой источник не поддерживает сохранение проектных настроек.");
                var targetSettings = await CompareMetadataTransferPreview.ReadProjectSettingsAsync(preview.Target, cancellationToken).ConfigureAwait(false)
                    ?? throw new CompareTransferException("Не удалось прочитать целевые проектные настройки.");
                if (!CompareMetadataTransferPreview.FingerprintProjectSettings(targetSettings).Equals(preview.TargetFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new CompareTransferException("Целевые проектные настройки изменились после сравнения. Повторите сравнение.");
                var before = CompareProjectSettingsRestore.CloneSettings(targetSettings);
                var lists = before.TextLists.ToList();
                var themes = before.ThemePresets.Select(item => item.Clone()).ToList();
                ApplyProjectSettings(lists, themes, preview);
                var after = new ProjectCompareSettings(lists, themes);
                var entry = CompareProjectSettingsRestore.CreateEntry(preview, before, after);
                await preview.Target.SaveProjectSettingsAsync(after, preview.TargetFingerprint, cancellationToken).ConfigureAwait(false);
                var journal = new ChangeJournalService();
                var (server, database) = CompareProjectSettingsRestore.ParseOnlineOrigin(preview.Target.Origin);
                journal.BindOnlineProject(server, database, preview.Target.Workspace.DisplayName, 2);
                if (!journal.Record(entry))
                    throw new CompareTransferException("Настройки перенесены, но запись журнала не сохранена.");
                preview.RecordedEntry = entry;
            }
            return;
        }

    }

    private static void ApplyProjectSettings(List<ProjectTextList> textLists, List<Configurator.Themes.ThemePreset> themes,
        CompareMetadataTransferPreview preview)
    {
        if (preview.Kind == CompareMetadataKind.ProjectTextList)
        {
            textLists.RemoveAll(item => CompareMetadataTransferPreview.TextListKey(item).Equals(preview.Key, StringComparison.OrdinalIgnoreCase));
            if (!preview.RemoveProjectSetting && preview.TextList is { } list)
                textLists.Add(JsonSerializer.Deserialize<ProjectTextList>(JsonSerializer.Serialize(list))!);
        }
        else
        {
            themes.RemoveAll(item => item.Name.Equals(preview.Key, StringComparison.OrdinalIgnoreCase));
            if (!preview.RemoveProjectSetting && preview.ThemePreset is { } theme)
                themes.Add(theme.Clone());
        }
    }

    private static async Task VerifyOfflineSnapshotAsync(CompareSource source, CancellationToken cancellationToken)
    {
        if (!source.Workspace.IsOffline) return;
        if (string.IsNullOrWhiteSpace(source.PackagePath) || string.IsNullOrWhiteSpace(source.PackageFingerprint))
            throw new CompareTransferException("Не удалось проверить исходный локальный проект. Повторите сравнение.");
        await using var file = new FileStream(Path.GetFullPath(source.PackagePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        var currentFingerprint = await ProjectPackageService.ReadFingerprintForCompareAsync(file, cancellationToken).ConfigureAwait(false);
        if (!currentFingerprint.Equals(source.PackageFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new CompareTransferException("Исходный локальный проект изменился после сравнения. Повторите сравнение.");
    }

    public static async Task ApplyAsync(CompareTransferPreview preview, CancellationToken cancellationToken = default)
    {
        await ApplyCoreAsync(preview, cancellationToken, checkUserPermission: true).ConfigureAwait(false);
    }

    internal static Task ApplyToTargetAsync(CompareTransferPreview preview, CancellationToken cancellationToken = default) =>
        ApplyCoreAsync(preview, cancellationToken, checkUserPermission: false);

    private static async Task ApplyCoreAsync(CompareTransferPreview preview, CancellationToken cancellationToken,
        bool checkUserPermission)
    {
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();
        if (!preview.IsAllowed)
            throw new CompareTransferException(preview.BlockReason ?? "Перенос заблокирован.");
        if (!preview.Target.CanWrite || preview.Target.Workspace.IsReadOnly)
            throw new CompareTransferException("Целевой источник недоступен для записи.");

        bool createsObject = preview.ExpectedRecord is null;
        if (preview.TargetSchema is null || !ProjectWriteGuard.IsDataTableWritable(preview.TargetSchema.TableName))
            throw new CompareTransferException("Изменение реестра классов отключено.");
        try
        {
            await VerifyOfflineSnapshotAsync(preview.Source, cancellationToken).ConfigureAwait(false);
            if (preview.Target.Workspace.IsOffline)
                await ApplyOfflineAsync(preview, createsObject, checkUserPermission, cancellationToken).ConfigureAwait(false);
            else
                await ApplyOnlineAsync(preview, createsObject, checkUserPermission, cancellationToken).ConfigureAwait(false);
        }
        catch (CompareTransferException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw new CompareTransferException("Текущее состояние целевого проекта не прошло повторную проверку. Повторите сравнение.");
        }
    }

    private static async Task ApplyOfflineAsync(CompareTransferPreview preview, bool createsObject,
        bool checkUserPermission, CancellationToken cancellationToken)
    {
        var target = preview.Target;
        if (string.IsNullOrWhiteSpace(target.PackagePath) || string.IsNullOrWhiteSpace(target.PackageFingerprint))
            throw new CompareTransferException("Не удалось проверить целевой локальный проект. Повторите сравнение.");

        string path = Path.GetFullPath(target.PackagePath);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0)
            throw new CompareTransferException("Целевой локальный проект доступен только для чтения.");

        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 81920, useAsync: true);
        var (project, fingerprint) = await ProjectPackageService.ReadForCompareAsync(file, cancellationToken).ConfigureAwait(false);
        if (!fingerprint.Equals(target.PackageFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new CompareTransferException("Локальный проект изменился после сравнения. Повторите сравнение.");

        var dataSource = new OfflineProjectDataSource(project);
        var schema = dataSource.GetSchema(preview.TargetSchema?.TableName ?? string.Empty);
        if (schema is null || preview.TargetSchema is null || !SameSchema(preview.TargetSchema, schema))
            throw new CompareTransferException("Схема целевого объекта изменилась после сравнения. Повторите сравнение.");

        ValidateOfflineTarget(dataSource, schema, preview, createsObject);
        if (checkUserPermission) EnsureUserPermission(createsObject, preview.DeletesRecord);
        await VerifyOnlineSourceAsync(preview, cancellationToken).ConfigureAwait(false);

        var originalRecords = project.LocalRecords.Select(CloneRecord).ToList();
        var originalArchitecture = (JsonObject)(project.Architecture?.DeepClone() ?? new JsonObject());
        using var workspace = new ProjectWorkspace(dataSource, dataSource);
        try
        {
            ProjectRecord? created = null;
            if (preview.DeletesRecord)
            {
                if (await workspace.DeleteRecordsAsync(schema.TableName, schema.KeyColumns, new[] { preview.KeyValues }, cancellationToken).ConfigureAwait(false) != 1)
                    throw new CompareTransferException("Целевая запись изменилась.");
            }
            else if (createsObject)
            {
                await workspace.CreateRecordAsync(schema.TableName, preview.Values, cancellationToken).ConfigureAwait(false);
                if (preview.CopiesComputedArea)
                {
                    string key = ProjectKeyCodec.Encode(schema, schema.KeyColumns, name => ProjectKeyCodec.ToNode(preview.KeyValues[name]));
                    created = dataSource.GetRecords(schema.TableName).Single(record => RecordKey(schema, record) == key);
                    created.Values["Area"] = preview.SourceRecord!.Values["Area"]?.DeepClone();
                }
            }
            else
                await workspace.UpdateRecordAsync(schema.TableName, schema.KeyColumns, preview.KeyValues, preview.Values, cancellationToken)
                    .ConfigureAwait(false);

            var entry = CreateRowJournalEntry(preview, created: created);
            var journal = new ChangeJournalService();
            journal.BindOfflineProject(project, changed: () => { }, mode: 3);
            if (!journal.Record(entry))
                throw new CompareTransferException("Перенос выполнен, но запись журнала не подготовлена. Изменение локального проекта отменено.");

            string savedFingerprint = await new ProjectPackageService().SaveForCompareAsync(path, project, file, cancellationToken).ConfigureAwait(false);
            preview.SavedPackage = (project, savedFingerprint);
            preview.RecordedEntry = entry;
        }
        catch
        {
            project.LocalRecords = originalRecords;
            project.Architecture = originalArchitecture;
            throw;
        }
    }

    private static async Task ApplyOnlineAsync(CompareTransferPreview preview, bool createsObject,
        bool checkUserPermission, CancellationToken cancellationToken)
    {
        if ((!createsObject && preview.ExpectedRecord is null) || preview.TargetSchema is null)
            throw new CompareTransferException("Не удалось проверить целевую запись. Повторите сравнение.");
        if (!preview.DeletesRecord && preview.Values.Count == 0)
            throw new CompareTransferException("Нет выбранных полей для переноса.");
        if (preview.TargetMetadataFingerprint is null)
            throw new CompareTransferException("Не удалось подтвердить текущие классы и логические связи. Повторите сравнение.");

        if (preview.Target.Workspace is not ProjectWorkspace workspace)
            throw new CompareTransferException("Не удалось проверить целевой источник перед записью.");

        var currentClasses = await workspace.GetClassDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var currentRelations = await workspace.GetGraphRelationsAsync(cancellationToken).ConfigureAwait(false);
        var currentMetadata = CompareEngine.FingerprintMetadata(currentClasses, currentRelations);
        if (!preview.TargetMetadataFingerprint.Equals(currentMetadata, StringComparison.OrdinalIgnoreCase))
            throw new CompareTransferException("Классы или логические связи изменились после сравнения. Повторите сравнение.");
        if (checkUserPermission) EnsureUserPermission(createsObject, preview.DeletesRecord);
        await VerifyOnlineSourceAsync(preview, cancellationToken).ConfigureAwait(false);

        try
        {
            var change = await workspace.UpdateCompareRecordAsync(preview.TargetSchema, preview.ExpectedRecord,
                preview.KeyValues, preview.Values, cancellationToken, preview.DeletesRecord).ConfigureAwait(false);
            RecordOnlineJournal(preview, change);
        }
        catch (CompareRecordConflictException) { throw; }
        catch (InvalidOperationException ex)
        {
            throw new CompareTransferException(ex.Message);
        }
    }

    private static void RecordOnlineJournal(CompareTransferPreview preview, ChangeJournalRowChange capturedChange)
    {
        var entry = CreateRowJournalEntry(preview, capturedChange);
        var origin = preview.Target.Origin ?? string.Empty;
        var separator = origin.IndexOf('\0');
        if (separator <= 0 || separator == origin.Length - 1)
            throw new CompareTransferException("Перенос выполнен, но не удалось определить целевую базу для журнала.");

        var server = origin[..separator];
        var database = origin[(separator + 1)..];
        var journal = new ChangeJournalService();
        journal.BindOnlineProject(server, database, preview.Target.Workspace.DisplayName, 3);
        if (!journal.Record(entry))
            throw new CompareTransferException("Перенос выполнен, но запись журнала не сохранена.");
        preview.RecordedEntry = entry;
    }

    internal static ChangeJournalEntry CreateRowJournalEntry(CompareTransferPreview preview, ChangeJournalRowChange? capturedChange = null,
        ProjectRecord? created = null)
    {
        var schema = preview.TargetSchema ?? throw new CompareTransferException("Не удалось сформировать журнал переноса без схемы целевой таблицы.");
        var before = preview.ExpectedRecord?.Values;
        var after = before is null ? new JsonObject() : (JsonObject)before.DeepClone();
        foreach (var pair in preview.Values)
            after[pair.Key] = ProjectKeyCodec.ToNode(pair.Value);

        var beforeValues = capturedChange?.Before ?? (before is null
            ? new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase)
            : JournalValues(before, schema));
        var afterValues = capturedChange?.After ?? JournalValues(
            created?.Values ?? (preview.DeletesRecord ? new JsonObject() : after), schema, includeComputed: created is not null);
        var identity = capturedChange?.IdentityAfter ?? JournalIdentity(preview.KeyValues, schema);
        var kind = capturedChange?.Kind ?? (preview.DeletesRecord ? ChangeJournalOperationKind.Delete : before is null ? ChangeJournalOperationKind.Insert : ChangeJournalOperationKind.Update);
        bool valuesAreRestorable = schema.KeyColumns.All(key => schema.Columns.Any(column =>
                column.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && IsSupportedJournalType(column.DataType))) &&
            (kind is ChangeJournalOperationKind.Insert or ChangeJournalOperationKind.Delete
                ? schema.Columns.All(column => IsSupportedJournalType(column.DataType))
                : preview.Values.Keys.All(name => schema.Columns.Any(column =>
                    column.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && IsSupportedJournalType(column.DataType))));
        var change = capturedChange ?? new ChangeJournalRowChange
        {
            TableName = schema.TableName,
            Kind = kind,
            Before = beforeValues,
            After = afterValues,
            IdentityBefore = before is null
                ? new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase)
                : identity,
            IdentityAfter = preview.DeletesRecord ? new() : identity,
            AfterIsCompleteOfflineRecord = created is not null
        };

        bool offline = preview.Target.Workspace.IsOffline;
        var server = string.Empty;
        var database = string.Empty;
        if (!offline)
        {
            var origin = preview.Target.Origin ?? string.Empty;
            var separator = origin.IndexOf('\0');
            if (separator > 0 && separator < origin.Length - 1)
            {
                server = origin[..separator];
                database = origin[(separator + 1)..];
            }
        }

        return new ChangeJournalEntry
        {
            Category = "Compare",
            Source = schema.TableName,
            Description = preview.DeletesRecord ? "Удаление объекта Compare" : before is null ? "Перенос объекта Compare" : "Изменение объекта Compare",
            Details = string.Join(", ", preview.KeyValues.Select(pair => $"{pair.Key}={pair.Value}")),
            OperationKind = kind,
            RestoreIncludesAllChanges = valuesAreRestorable,
            ProjectType = offline ? "Offline" : "Online",
            ProjectId = offline ? (preview.Target.ProjectId ?? string.Empty) : $"online:{server.ToLowerInvariant()}/{database.ToLowerInvariant()}",
            ProjectName = offline ? (preview.Target.OfflineProject?.Manifest?.Name ?? preview.Target.Workspace.DisplayName) : database,
            Server = server,
            Database = database,
            Mode = 3,
            UserRole = UserAuthorizationService.CurrentRole?.Name ?? string.Empty,
            EntityType = "Объект",
            EntityIdentity = string.Join("; ", preview.KeyValues.Select(pair => $"{pair.Key}={pair.Value}")),
            Result = "Успешно",
            Changes = new List<ChangeJournalRowChange> { change }
        };
    }

    private static Dictionary<string, ChangeJournalValue> JournalValues(JsonObject values, ProjectTableSchema schema, bool includeComputed = false)
    {
        var result = new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            var column = schema.Columns.FirstOrDefault(item => item.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            if (column is null || (!includeComputed && (column.IsComputed || column.IsReadOnly))) continue;
            result[pair.Key] = ChangeJournalValue.FromObject(ToJournalObject(pair.Value, column.DataType));
        }
        return result;
    }

    private static Dictionary<string, ChangeJournalValue> JournalIdentity(
        IReadOnlyDictionary<string, object?> values, ProjectTableSchema schema)
    {
        var result = new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in schema.KeyColumns)
        {
            var column = schema.Columns.FirstOrDefault(item => item.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (column is null || !values.TryGetValue(key, out var value)) continue;
            result[key] = ChangeJournalValue.FromObject(value ?? DBNull.Value);
        }
        return result;
    }

    private static object ToJournalObject(JsonNode? node, string sqlType)
    {
        if (node is null) return DBNull.Value;
        var type = sqlType.ToLowerInvariant() switch
        {
            "bit" => typeof(bool),
            "tinyint" => typeof(byte),
            "smallint" => typeof(short),
            "int" => typeof(int),
            "bigint" => typeof(long),
            "real" => typeof(float),
            "float" => typeof(double),
            "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
            "uniqueidentifier" => typeof(Guid),
            "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
            "datetimeoffset" => typeof(DateTimeOffset),
            "time" => typeof(TimeSpan),
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => typeof(byte[]),
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml" => typeof(string),
            _ => typeof(string)
        };
        return JsonSerializer.Deserialize(node.ToJsonString(), type) ?? DBNull.Value;
    }

    private static bool IsSupportedJournalType(string sqlType) => sqlType.ToLowerInvariant() is
        "bit" or "tinyint" or "smallint" or "int" or "bigint" or "real" or "float" or
        "decimal" or "numeric" or "money" or "smallmoney" or "uniqueidentifier" or
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" or
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" or
        "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml";

    private static ProjectRecord CloneRecord(ProjectRecord record) => new()
    {
        TableName = record.TableName,
        Key = record.Key,
        RowVersion = record.RowVersion,
        Values = (JsonObject)(record.Values?.DeepClone() ?? new JsonObject())
    };

    private static void EnsureUserPermission(bool createsObject, bool deletes)
    {
        var permission = deletes ? Permission.DeleteData : createsObject ? Permission.AddData : Permission.EditData;
        if (!PermissionPolicy.CanEnterMode(UserAuthorizationService.CurrentUser, UserAuthorizationService.CurrentRole, 3) ||
            !UserAuthorizationService.Has(permission))
            throw new CompareTransferException("У текущего пользователя нет разрешения на выбранное действие.");
    }

    private static async Task VerifyOnlineSourceAsync(CompareTransferPreview preview, CancellationToken cancellationToken)
    {
        if (preview.Source.Workspace.IsOffline) return;
        var expected = preview.SourceRecord;
        var schema = preview.SourceSchema;
        if (schema is null || preview.SourceKeyValues.Count != schema.KeyColumns.Count)
            throw new CompareTransferException("Не удалось проверить исходную запись. Повторите сравнение.");
        var currentSchema = await preview.Source.Workspace.RefreshSchemaAsync(schema.TableName, cancellationToken).ConfigureAwait(false);
        if (currentSchema is null || !SameSchema(schema, currentSchema))
            throw new CompareTransferException("Схема исходной записи изменилась. Повторите сравнение.");
        var page = await preview.Source.Workspace.ReadPageAsync(new ProjectReadQuery
        {
            TableName = schema.TableName, Filters = preview.SourceKeyValues, PageSize = 2
        }, cancellationToken).ConfigureAwait(false);
        if (expected is null && preview.DeletesRecord && page.TotalCount == 0) return;
        if (expected is null || page.TotalCount != 1 || page.Items.Count != 1 ||
            !JsonNode.DeepEquals(expected.Values, page.Items[0].Values) ||
            !string.Equals(expected.RowVersion, page.Items[0].RowVersion, StringComparison.Ordinal))
            throw new CompareTransferException("Исходная запись изменилась после сравнения. Повторите сравнение.");
    }

    private static void ValidateOfflineTarget(OfflineProjectDataSource dataSource, ProjectTableSchema schema,
        CompareTransferPreview preview, bool createsObject)
    {
        string expectedKey = ProjectKeyCodec.Encode(schema, schema.KeyColumns, name =>
            ProjectKeyCodec.ToNode(preview.KeyValues.TryGetValue(name, out var value) ? value : null));
        var matching = dataSource.GetRecords(schema.TableName)
            .Where(record => RecordKey(schema, record).Equals(expectedKey, StringComparison.Ordinal))
            .ToList();

        if (createsObject)
        {
            var physicalTable = preview.Target.PhysicalSchema?.FindTable(schema.TableName);
            if (preview.KeyValues.TryGetValue("Record", out var number))
            {
                bool uniqueRecord = physicalTable?.Keys.Any(key => key.IsUnique && key.Columns.Count == 1 &&
                    key.Columns.Contains("Record", StringComparer.OrdinalIgnoreCase)) == true;
                if (matching.Count != 0 || (uniqueRecord && dataSource.GetRecords(schema.TableName).Any(row =>
                    CompareValues.Compare(row.Values["Record"], ProjectKeyCodec.ToNode(number), null, null) == CompareStatus.Equal)))
                    throw new CompareRecordConflictException();
            }
            if (matching.Count != 0)
                throw new CompareTransferException("Ключ объекта уже существует в целевом локальном проекте. Повторите сравнение.");

            bool offlinePid = preview.Source.Workspace.IsOffline && CompareTransferPreview.IsEquipmentTable(schema) &&
                schema.TableName.Equals("dbo.Reglers", StringComparison.OrdinalIgnoreCase);
            bool uniqueName = !CompareTransferPreview.IsEquipmentTable(schema) || physicalTable is null || !physicalTable.Keys.Any(key => key.IsUnique) ||
                physicalTable.Keys.Any(key => key.IsUnique && key.Columns.Contains("Name", StringComparer.OrdinalIgnoreCase));
            EnsureNameIsAvailable(dataSource.GetRecords(schema.TableName), preview.Values, uniqueName);
            if (CompareTransferPreview.IsEquipmentTable(schema))
            {
                var lookupValues = new Dictionary<string, object?>(preview.Values, StringComparer.OrdinalIgnoreCase);
                if (preview.CopiesComputedArea)
                    lookupValues["Area"] = ReadString(preview.SourceRecord!.Values["Area"]);
                foreach (var (table, fields, values) in CompareTransferPreview.EquipmentLookups(schema, lookupValues))
                {
                    bool HasMatch(IReadOnlyList<ProjectRecord> rows) => rows.Any(record => fields.Select((field, index) =>
                        record.Values.TryGetPropertyValue(field, out var value) &&
                        CompareValues.Compare(value, ProjectKeyCodec.ToNode(values[index]), null, null) == CompareStatus.Equal).All(equal => equal));
                    if (!HasMatch(dataSource.GetRecords(table)) &&
                        !(offlinePid && CanKeepMissingPidLookup(preview, table, fields) &&
                          !HasMatch(new OfflineProjectDataSource(preview.Source.OfflineProject!).GetRecords(table))))
                        throw new CompareTransferException($"Для объекта отсутствует связанная запись в {table}.");
                }
            }
            return;
        }

        if (preview.ExpectedRecord is null || matching.Count != 1 ||
            !JsonNode.DeepEquals(preview.ExpectedRecord.Values, matching[0].Values))
            throw new CompareTransferException("Целевая запись изменилась после сравнения. Повторите сравнение.");
        if (preview.DeletesRecord)
        {
            try { OfflineObjectDeletionGuard.CheckCompareDeletion(dataSource, matching[0]); }
            catch (InvalidOperationException ex) { throw new CompareTransferException(ex.Message); }
        }
    }

    private static string RecordKey(ProjectTableSchema schema, ProjectRecord record) =>
        ProjectKeyCodec.Encode(schema, schema.KeyColumns, name =>
            record.Values.TryGetPropertyValue(name, out var value) ? value : null);

    private static bool SameSchema(ProjectTableSchema expected, ProjectTableSchema actual)
    {
        if (!expected.HasStableKey || !actual.HasStableKey ||
            !expected.TableName.Equals(actual.TableName, StringComparison.OrdinalIgnoreCase) ||
            !expected.KeyColumns.SequenceEqual(actual.KeyColumns, StringComparer.OrdinalIgnoreCase) ||
            expected.Columns.Count != actual.Columns.Count) return false;

        foreach (var column in expected.Columns)
        {
            var current = actual.Columns.FirstOrDefault(item =>
                item.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
            if (current is null || !current.DataType.Equals(column.DataType, StringComparison.OrdinalIgnoreCase) ||
                current.IsNullable != column.IsNullable || current.IsIdentity != column.IsIdentity ||
                current.IsComputed != column.IsComputed || current.IsReadOnly != column.IsReadOnly ||
                current.MaxLength != column.MaxLength || current.Precision != column.Precision ||
                current.Scale != column.Scale || current.HasDefault != column.HasDefault) return false;
        }

        return true;
    }

    private static bool CanKeepMissingPidLookup(CompareTransferPreview preview, string table, string[] fields)
    {
        if (table is not ("dbo.Reglers_Types" or "dbo.AnaIN" or "dbo.AnaOUT") ||
            preview.Source.OfflineProject is null) return false;
        bool IsCapturedLogicalLookup(CompareSource source)
        {
            var relations = source.PhysicalSchema?.Relations.Where(relation =>
                relation.SourceTable.Equals("dbo.Reglers", StringComparison.OrdinalIgnoreCase) &&
                relation.TargetTable.Equals(table, StringComparison.OrdinalIgnoreCase)).ToArray();
            return relations is not null && relations.Any(relation => relation.TargetColumns.SequenceEqual(fields, StringComparer.OrdinalIgnoreCase)) &&
                relations.All(relation => relation.Kind != DatabaseRelationKind.PhysicalForeignKey);
        }
        return IsCapturedLogicalLookup(preview.Source) && IsCapturedLogicalLookup(preview.Target);
    }

    private static void EnsureNameIsAvailable(IReadOnlyList<ProjectRecord> records, IReadOnlyDictionary<string, object?> values, bool uniqueName)
    {
        var nameField = values.FirstOrDefault(pair => pair.Key.Equals("Name", StringComparison.OrdinalIgnoreCase));
        if (nameField.Key is null) return;
        if (nameField.Value is not string name || string.IsNullOrWhiteSpace(name))
            throw new CompareTransferException("Для создания объекта требуется непустое имя.");

        if (uniqueName && records.Any(record => record.Values.Any(pair => pair.Key.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ReadString(pair.Value)?.TrimEnd(), name.TrimEnd(), StringComparison.OrdinalIgnoreCase))))
            throw new CompareTransferException("В целевом локальном проекте уже есть объект с таким именем.");
    }

    private static string? ReadString(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
}

public sealed class CompareRecordConflictException() : CompareTransferException("Record уже занят. Введите другой Record.") { }

public class CompareTransferException : InvalidOperationException
{
    public CompareTransferException(string safeMessage) : base(safeMessage) { }
}
