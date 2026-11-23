#nullable enable
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
        if (!preview.IsAllowed) throw new CompareTransferException(preview.BlockReason ?? "Перенос заблокирован.");
        if (!preview.Target.CanWrite || preview.Target.Workspace.IsReadOnly)
            throw new CompareTransferException("Целевой источник недоступен для записи.");
        if (checkUserPermission)
        {
            var permission = preview.Kind switch
            {
                CompareMetadataKind.ClassDefinition => Permission.ManageClasses,
                CompareMetadataKind.ThemePreset => Permission.ManageTextLists,
                CompareMetadataKind.ManualRelation => Permission.ManageRelations,
                _ => Permission.ManageTextLists
            };
            if (!UserAuthorizationService.Has(Permission.Mode2Manage) && !UserAuthorizationService.Has(permission))
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
                var currentTarget = new ProjectCompareSettings(project.TextLists, project.ThemePresets);
                if (!CompareMetadataTransferPreview.FingerprintProjectSettings(currentTarget).Equals(preview.TargetFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new CompareTransferException("Целевые проектные настройки изменились после сравнения. Повторите сравнение.");
                ApplyProjectSettings(project.TextLists, project.ThemePresets, preview);
                await new ProjectPackageService().SaveAsync(path, project, cancellationToken, requireExisting: true, reservedFile: file).ConfigureAwait(false);
            }
            else
            {
                if (preview.Target.SaveProjectSettingsAsync is null)
                    throw new CompareTransferException("Целевой источник не поддерживает сохранение проектных настроек.");
                var targetSettings = await CompareMetadataTransferPreview.ReadProjectSettingsAsync(preview.Target, cancellationToken).ConfigureAwait(false)
                    ?? throw new CompareTransferException("Не удалось прочитать целевые проектные настройки.");
                if (!CompareMetadataTransferPreview.FingerprintProjectSettings(targetSettings).Equals(preview.TargetFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new CompareTransferException("Целевые проектные настройки изменились после сравнения. Повторите сравнение.");
                var lists = targetSettings.TextLists.ToList();
                var themes = targetSettings.ThemePresets.Select(item => item.Clone()).ToList();
                ApplyProjectSettings(lists, themes, preview);
                await preview.Target.SaveProjectSettingsAsync(new ProjectCompareSettings(lists, themes), cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var sourceNow = await CompareMetadataTransferPreview.ReadAsync(preview.Source, cancellationToken).ConfigureAwait(false);
        if (!preview.SourceFingerprint.Equals(CompareMetadataTransferPreview.Fingerprint(sourceNow.Classes, sourceNow.Relations), StringComparison.OrdinalIgnoreCase))
            throw new CompareTransferException("Исходные настройки изменились после сравнения. Повторите сравнение.");

        if (preview.Target.Workspace.IsOffline)
        {
            if (string.IsNullOrWhiteSpace(preview.Target.PackagePath) || string.IsNullOrWhiteSpace(preview.Target.PackageFingerprint))
                throw new CompareTransferException("Не удалось проверить целевой локальный проект. Повторите сравнение.");
            string path = Path.GetFullPath(preview.Target.PackagePath);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0)
                throw new CompareTransferException("Целевой локальный проект доступен только для чтения.");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 81920, useAsync: true);
            var (project, packageFingerprint) = await ProjectPackageService.ReadForCompareAsync(file, cancellationToken).ConfigureAwait(false);
            if (!packageFingerprint.Equals(preview.Target.PackageFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new CompareTransferException("Локальный проект изменился после сравнения. Повторите сравнение.");
            var architecture = new ProjectArchitectureDataSource(project, strictReads: true);
            await ApplyMetadataToStoreAsync(architecture.GetClassDefinitionsAsync, architecture.GetGraphRelationsAsync,
                architecture.SaveClassDefinitionsAsync, architecture.SaveGraphRelationsAsync, project.TableSchemas, preview, cancellationToken).ConfigureAwait(false);
            await new ProjectPackageService().SaveAsync(path, project, cancellationToken, requireExisting: true, reservedFile: file).ConfigureAwait(false);
            return;
        }

        var destinationTables = await preview.Target.Workspace.GetTablesAsync(cancellationToken).ConfigureAwait(false);
        await ApplyMetadataToStoreAsync(preview.Target.Workspace.GetClassDefinitionsAsync,
            preview.Target.Workspace.GetGraphRelationsAsync, preview.Target.Workspace.SaveClassDefinitionsAsync,
            preview.Target.Workspace.SaveGraphRelationsAsync, destinationTables, preview, cancellationToken).ConfigureAwait(false);
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
        var (_, currentFingerprint) = await ProjectPackageService.ReadForCompareAsync(file, cancellationToken).ConfigureAwait(false);
        if (!currentFingerprint.Equals(source.PackageFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new CompareTransferException("Исходный локальный проект изменился после сравнения. Повторите сравнение.");
    }

    private static async Task ApplyMetadataToStoreAsync(
        Func<CancellationToken, Task<IReadOnlyList<ClassDefinition>>> getClasses,
        Func<CancellationToken, Task<JsonObject>> getRelations,
        Func<IReadOnlyList<ClassDefinition>, CancellationToken, Task> saveClasses,
        Func<JsonObject, CancellationToken, Task> saveRelations,
        IReadOnlyList<ProjectTableSchema> tables,
        CompareMetadataTransferPreview preview, CancellationToken cancellationToken)
    {
        var classes = await getClasses(cancellationToken).ConfigureAwait(false);
        var relations = await getRelations(cancellationToken).ConfigureAwait(false);
        if (!preview.TargetFingerprint.Equals(CompareMetadataTransferPreview.Fingerprint(classes, relations), StringComparison.OrdinalIgnoreCase))
            throw new CompareTransferException("Целевые настройки изменились после сравнения. Повторите сравнение.");

        if (preview.Kind == CompareMetadataKind.ClassDefinition)
        {
            if (preview.ClassDefinition is null) throw new CompareTransferException("В источнике отсутствует класс.");
            var updated = classes.Where(item => !item.Id.Equals(preview.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            updated.Add(JsonSerializer.Deserialize<ClassDefinition>(JsonSerializer.Serialize(preview.ClassDefinition))!);
            await saveClasses(updated, cancellationToken).ConfigureAwait(false);
            return;
        }

        var document = CompareMetadataTransferPreview.DeserializeRelations(relations);
        document.ManualRelations.RemoveAll(item => CompareMetadataTransferPreview.RelationId(item, tables).Equals(preview.Key, StringComparison.OrdinalIgnoreCase));
        if (!preview.RemoveRelation && preview.Relation is not null)
            document.ManualRelations.Add(JsonSerializer.Deserialize<GraphRelationDefinition>(JsonSerializer.Serialize(preview.Relation))!);
        document.SuppressedAutomaticRelations.RemoveAll(id => id.Equals(preview.Key, StringComparison.OrdinalIgnoreCase));
        await saveRelations(JsonSerializer.SerializeToNode(document) as JsonObject ?? new JsonObject(), cancellationToken).ConfigureAwait(false);
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
        try
        {
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
        if (checkUserPermission) EnsureUserPermission(createsObject);

        using var workspace = new ProjectWorkspace(dataSource, dataSource);
        if (createsObject)
            await workspace.CreateRecordAsync(schema.TableName, preview.Values, cancellationToken).ConfigureAwait(false);
        else
            await workspace.UpdateRecordAsync(schema.TableName, schema.KeyColumns, preview.KeyValues, preview.Values, cancellationToken)
                .ConfigureAwait(false);

        await new ProjectPackageService().SaveAsync(path, project, cancellationToken, requireExisting: true, reservedFile: file)
            .ConfigureAwait(false);
    }

    private static async Task ApplyOnlineAsync(CompareTransferPreview preview, bool createsObject,
        bool checkUserPermission, CancellationToken cancellationToken)
    {
        if (createsObject)
            throw new CompareTransferException("Создание объектов в живой базе не поддерживается.");
        if (preview.ExpectedRecord is null || preview.TargetSchema is null)
            throw new CompareTransferException("Не удалось проверить целевую запись. Повторите сравнение.");
        if (preview.Values.Count != 1)
            throw new CompareTransferException("Можно изменить только одно выбранное поле.");
        if (preview.TargetMetadataFingerprint is null)
            throw new CompareTransferException("Не удалось подтвердить текущие классы и логические связи. Повторите сравнение.");

        if (preview.Target.Workspace is not ProjectWorkspace workspace)
            throw new CompareTransferException("Не удалось проверить целевой источник перед записью.");

        var currentClasses = await workspace.GetClassDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var currentRelations = await workspace.GetGraphRelationsAsync(cancellationToken).ConfigureAwait(false);
        var currentMetadata = CompareEngine.FingerprintMetadata(currentClasses, currentRelations);
        if (!preview.TargetMetadataFingerprint.Equals(currentMetadata, StringComparison.OrdinalIgnoreCase))
            throw new CompareTransferException("Классы или логические связи изменились после сравнения. Повторите сравнение.");
        if (checkUserPermission) EnsureUserPermission(createsObject);

        try
        {
            await workspace.UpdateCompareRecordAsync(preview.TargetSchema, preview.ExpectedRecord,
                preview.KeyValues, preview.Values, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new CompareTransferException(ex.Message);
        }
    }

    private static void EnsureUserPermission(bool createsObject)
    {
        var permission = createsObject ? Permission.AddData : Permission.EditData;
        if (!UserAuthorizationService.Has(Permission.Mode3Manage) || !UserAuthorizationService.Has(permission))
            throw new CompareTransferException("У текущего пользователя нет разрешения на выбранное действие.");
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
            if (matching.Count != 0)
                throw new CompareTransferException("Ключ объекта уже существует в целевом локальном проекте. Повторите сравнение.");

            EnsureNameIsAvailable(dataSource.GetRecords(schema.TableName), preview.Values);
            return;
        }

        if (preview.ExpectedRecord is null || matching.Count != 1 ||
            !JsonNode.DeepEquals(preview.ExpectedRecord.Values, matching[0].Values))
            throw new CompareTransferException("Целевая запись изменилась после сравнения. Повторите сравнение.");
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

    private static void EnsureNameIsAvailable(IReadOnlyList<ProjectRecord> records, IReadOnlyDictionary<string, object?> values)
    {
        var nameField = values.FirstOrDefault(pair => pair.Key.Equals("Name", StringComparison.OrdinalIgnoreCase));
        if (nameField.Key is null) return;
        if (nameField.Value is not string name || string.IsNullOrWhiteSpace(name))
            throw new CompareTransferException("Для создания объекта требуется непустое имя.");

        if (records.Any(record => record.Values.Any(pair => pair.Key.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ReadString(pair.Value)?.TrimEnd(), name.TrimEnd(), StringComparison.OrdinalIgnoreCase))))
            throw new CompareTransferException("В целевом локальном проекте уже есть объект с таким именем.");
    }

    private static string? ReadString(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
}

public sealed class CompareTransferException : InvalidOperationException
{
    public CompareTransferException(string safeMessage) : base(safeMessage) { }
}
