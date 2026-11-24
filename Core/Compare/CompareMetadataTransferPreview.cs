#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Themes;

namespace Configurator.Core.Compare;

/// <summary>A stale-guarded proposal to copy one class definition or manual logical relation.</summary>
public sealed class CompareMetadataTransferPreview
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private CompareMetadataTransferPreview(CompareSource source, CompareSource target, CompareMetadataKind kind,
        string key, string description, string? blockReason, string sourceFingerprint, string targetFingerprint,
        ClassDefinition? classDefinition, GraphRelationDefinition? relation)
    {
        Source = source; Target = target; Kind = kind; Key = key; Description = description;
        BlockReason = blockReason; SourceFingerprint = sourceFingerprint; TargetFingerprint = targetFingerprint;
        ClassDefinition = classDefinition; Relation = relation;
    }

    public bool IsAllowed => BlockReason is null;
    public string? BlockReason { get; }
    public string Description { get; }
    internal CompareSource Source { get; }
    internal CompareSource Target { get; }
    internal CompareMetadataKind Kind { get; }
    internal string Key { get; }
    internal string SourceFingerprint { get; }
    internal string TargetFingerprint { get; }
    internal ClassDefinition? ClassDefinition { get; }
    internal GraphRelationDefinition? Relation { get; }
    internal bool RemoveRelation { get; private init; }
    internal ProjectTextList? TextList { get; private init; }
    internal ThemePreset? ThemePreset { get; private init; }
    internal bool RemoveProjectSetting { get; private init; }

    public static async Task<CompareMetadataTransferPreview> EvaluateAsync(
        CompareSource a, CompareSource b, CompareDifference difference, bool aToB,
        CancellationToken cancellationToken = default)
    {
        var source = aToB ? a : b;
        var target = aToB ? b : a;
        var kind = difference.MetadataKind ?? CompareMetadataKind.ClassDefinition;
        string key = difference.MetadataKey ?? string.Empty;
        string name = kind == CompareMetadataKind.ClassDefinition ? "класс" : "логическую связь";
        string reason = "Для этого результата нет безопасной операции переноса.";
        string sourceFingerprint = "";
        string targetFingerprint = "";
        ClassDefinition? classDefinition = null;
        GraphRelationDefinition? relation = null;
        bool removeRelation = false;

        if (difference.MetadataKind is null || key.Length == 0 ||
            difference.Status is not (CompareStatus.Changed or CompareStatus.OnlyA or CompareStatus.OnlyB))
            return Block(reason);
        if (kind is not (CompareMetadataKind.ProjectTextList or CompareMetadataKind.ThemePreset))
            return Block("Перенос этого типа настроек отключён: архитектура проекта доступна только для просмотра.");
        if (!target.CanWrite || target.Workspace.IsReadOnly)
            return Block("Целевой источник открыт только для чтения.");
        if (kind == CompareMetadataKind.ClassDefinition && difference.Column is not null)
            return Block("Перенос класса доступен только на строке класса целиком.");
        if (kind == CompareMetadataKind.ManualRelation && difference.Issue is not null)
            return Block("Связь неполная или не разрешена в схеме.");

        if (kind is CompareMetadataKind.ProjectTextList or CompareMetadataKind.ThemePreset)
        {
            var sourceSettings = await ReadProjectSettingsAsync(source, cancellationToken).ConfigureAwait(false);
            var targetSettings = await ReadProjectSettingsAsync(target, cancellationToken).ConfigureAwait(false);
            if (sourceSettings is null || targetSettings is null)
                return Block("Проектные текстовые списки или темы недоступны для сравнения.");
            if (target.OfflineProject is null && target.SaveProjectSettingsAsync is null)
                return Block("Целевой источник не поддерживает сохранение проектных настроек.");
            sourceFingerprint = FingerprintProjectSettings(sourceSettings);
            targetFingerprint = FingerprintProjectSettings(targetSettings);
            if (!MatchesComparison()) return Block("Проектные настройки изменились. Повторите сравнение.");
            if (kind == CompareMetadataKind.ProjectTextList)
            {
                var sourceMatches = sourceSettings.TextLists.Where(item => TextListKey(item).Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
                var targetMatches = targetSettings.TextLists.Where(item => TextListKey(item).Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (sourceMatches.Length > 1 || targetMatches.Length > 1) return Block("Идентификатор текстового списка повторяется.");
                if (sourceMatches.Length == 0 && targetMatches.Length == 0) return Block("Текстовый список уже отсутствует в целевом проекте.");
                var list = sourceMatches.SingleOrDefault();
                return new CompareMetadataTransferPreview(source, target, kind, key,
                    list is null ? $"{(aToB ? "A → B" : "B → A")}: удалить текстовый список «{key}»." :
                        $"{(aToB ? "A → B" : "B → A")}: перенести текстовый список «{list.Name}» целиком.", null,
                    sourceFingerprint, targetFingerprint,
                    null, null) { TextList = list, RemoveProjectSetting = list is null };
            }

            var sourceThemes = sourceSettings.ThemePresets.Where(item => item.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            var targetThemes = targetSettings.ThemePresets.Where(item => item.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sourceThemes.Length > 1 || targetThemes.Length > 1) return Block("Название темы проекта повторяется.");
            if (sourceThemes.Length == 0 && targetThemes.Length == 0) return Block("Тема уже отсутствует в целевом проекте.");
            var theme = sourceThemes.SingleOrDefault();
            return new CompareMetadataTransferPreview(source, target, kind, key,
                theme is null ? $"{(aToB ? "A → B" : "B → A")}: удалить тему проекта «{key}»." :
                    $"{(aToB ? "A → B" : "B → A")}: перенести тему проекта «{theme.Name}».", null,
                sourceFingerprint, targetFingerprint,
                null, null) { ThemePreset = theme?.Clone(), RemoveProjectSetting = theme is null };
        }

        var sourceData = await ReadAsync(source, cancellationToken).ConfigureAwait(false);
        var targetData = await ReadAsync(target, cancellationToken).ConfigureAwait(false);
        sourceFingerprint = Fingerprint(sourceData.Classes, sourceData.Relations);
        targetFingerprint = Fingerprint(targetData.Classes, targetData.Relations);
        if (!MatchesComparison()) return Block("Классы или связи изменились. Повторите сравнение.");

        if (kind == CompareMetadataKind.ClassDefinition)
        {
            classDefinition = sourceData.Classes.FirstOrDefault(item => item.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (classDefinition is null)
                return Block("Удаление класса через сравнение не поддерживается.");
            if (classDefinition.IsSystem)
                return Block("Системный класс нельзя переносить из сравнения.");
            var targetClass = targetData.Classes.FirstOrDefault(item => item.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
            var destinationTables = await target.Workspace.GetTablesAsync(cancellationToken).ConfigureAwait(false);
            if (FindInvalidClassReference(classDefinition, destinationTables) is { } invalidReference)
                return Block($"Настройки класса ссылаются на отсутствующую таблицу или поле целевого проекта: {invalidReference}.");
            string action = targetClass is null ? "создать" : "заменить настройки";
            if (targetClass?.IsSystem == true)
                return Block("Нельзя заменять системный класс в целевом проекте.");
            if (targetData.Classes.Any(item => !item.Id.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(classDefinition.Name, StringComparison.OrdinalIgnoreCase)))
                return Block("В целевом проекте уже есть другой класс с таким именем.");
            if (classDefinition.ClassNumber != 0 && targetData.Classes.Any(item => !item.Id.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                item.ClassNumber == classDefinition.ClassNumber))
                return Block("Номер класса уже занят другим классом в целевом проекте.");
            return New($"{(aToB ? "A → B" : "B → A")}: {action} класс «{classDefinition.Name}» целиком. Дочерние поля и типы класса также будут перенесены.");
        }

        var sourceDocument = DeserializeRelations(sourceData.Relations);
        var targetDocument = DeserializeRelations(targetData.Relations);
        var sourceTables = await source.Workspace.GetTablesAsync(cancellationToken).ConfigureAwait(false);
        var targetTables = await target.Workspace.GetTablesAsync(cancellationToken).ConfigureAwait(false);
        relation = sourceDocument.ManualRelations.FirstOrDefault(item => RelationId(item, sourceTables).Equals(key, StringComparison.OrdinalIgnoreCase));
        removeRelation = relation is null;
        if (removeRelation && !targetDocument.ManualRelations.Any(item => RelationId(item, targetTables).Equals(key, StringComparison.OrdinalIgnoreCase)))
            return Block("Ручная логическая связь уже отсутствует в целевом проекте.");
        if (relation is not null && FindInvalidRelationReference(relation, targetTables) is { } invalidRelationReference)
            return Block($"Конечная таблица или поле ручной связи отсутствует в целевом проекте: {invalidRelationReference}.");
        string relationDescription = relation is null ? "удалить ручную логическую связь" : "перенести ручную логическую связь";
        return New($"{(aToB ? "A → B" : "B → A")}: {relationDescription} «{difference.Title}».");

        bool MatchesComparison() => difference.FingerprintA == (aToB ? sourceFingerprint : targetFingerprint) &&
            difference.FingerprintB == (aToB ? targetFingerprint : sourceFingerprint);
        CompareMetadataTransferPreview New(string description) => new(source, target, kind, key, description, null,
            sourceFingerprint, targetFingerprint, classDefinition, relation) { RemoveRelation = removeRelation };
        CompareMetadataTransferPreview Block(string why) => new(source, target, kind, key, why, why,
            sourceFingerprint, targetFingerprint, classDefinition, relation) { RemoveRelation = removeRelation };
    }

    internal static string TextListKey(ProjectTextList item) => $"{item.SourceTable.Trim()}|{item.Name.Trim()}";

    internal static async Task<(IReadOnlyList<ClassDefinition> Classes, JsonObject Relations)> ReadAsync(
        CompareSource source, CancellationToken cancellationToken)
    {
        var classes = await source.Workspace.GetClassDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var relations = await source.Workspace.GetGraphRelationsAsync(cancellationToken).ConfigureAwait(false);
        return (classes, relations);
    }

    internal static async Task<ProjectCompareSettings?> ReadProjectSettingsAsync(CompareSource source, CancellationToken cancellationToken)
    {
        if (source.OfflineProject is { } project)
            return new ProjectCompareSettings(project.TextLists, project.ThemePresets);
        if (source.ReadProjectSettingsAsync is { } read)
            return await read(cancellationToken).ConfigureAwait(false);
        return null;
    }

    internal static string FingerprintProjectSettings(ProjectCompareSettings settings) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));

    internal static string Fingerprint(IReadOnlyList<ClassDefinition> classes, JsonObject relations) =>
        CompareEngine.FingerprintMetadata(classes, relations);

    internal static GraphRelationProjectDocument DeserializeRelations(JsonObject relations) =>
        relations.Deserialize<GraphRelationProjectDocument>(JsonOptions) ?? new GraphRelationProjectDocument();

    internal static string RelationId(GraphRelationDefinition relation, IReadOnlyList<ProjectTableSchema> tables)
    {
        var names = tables.Select(table => table.TableName).ToArray();
        var source = GraphRelationDefinition.ResolveTableName(names, relation.Source) ?? relation.Source;
        var target = GraphRelationDefinition.ResolveTableName(names, relation.Target) ?? relation.Target;
        return DatabaseGraphRelationMaterializer.AutomaticId(source, target, [relation.SourceColumn], [relation.TargetColumn]);
    }

    private static string? FindInvalidRelationReference(GraphRelationDefinition relation, IReadOnlyList<ProjectTableSchema> tables)
    {
        var names = tables.Select(table => table.TableName).ToArray();
        var source = GraphRelationDefinition.ResolveTableName(names, relation.Source);
        var target = GraphRelationDefinition.ResolveTableName(names, relation.Target);
        if (source is null) return relation.Source;
        if (target is null) return relation.Target;
        if (!HasColumn(tables, source, relation.SourceColumn)) return $"{source}.{relation.SourceColumn}";
        if (!HasColumn(tables, target, relation.TargetColumn)) return $"{target}.{relation.TargetColumn}";
        return null;
    }

    private static string? FindInvalidClassReference(ClassDefinition definition, IReadOnlyList<ProjectTableSchema> tables)
    {
        foreach (var mapping in definition.StorageMappings ?? [])
        {
            if (string.IsNullOrWhiteSpace(mapping.TableName)) continue;
            var table = GraphRelationDefinition.ResolveTableName(tables.Select(item => item.TableName), mapping.TableName);
            if (table is null) return mapping.TableName;
            foreach (var key in mapping.KeyColumns ?? [])
                if (!HasColumn(tables, table, key)) return $"{table}.{key}";
        }
        if (!string.IsNullOrWhiteSpace(definition.TypeStorageTable) &&
            GraphRelationDefinition.ResolveTableName(tables.Select(item => item.TableName), definition.TypeStorageTable) is null)
            return definition.TypeStorageTable;
        foreach (var complex in definition.ComplexTables ?? [])
            if (!string.IsNullOrWhiteSpace(complex.TableName) &&
                GraphRelationDefinition.ResolveTableName(tables.Select(item => item.TableName), complex.TableName) is null)
                return complex.TableName;
        foreach (var field in (definition.Fields ?? []).Concat((definition.ComplexTables ?? []).SelectMany(item => item.Fields ?? [])))
        {
            if (string.IsNullOrWhiteSpace(field.LookupTable)) continue;
            var table = GraphRelationDefinition.ResolveTableName(tables.Select(item => item.TableName), field.LookupTable);
            if (table is null) return field.LookupTable;
            if (!string.IsNullOrWhiteSpace(field.LookupKeyField) && !HasColumn(tables, table, field.LookupKeyField)) return $"{table}.{field.LookupKeyField}";
            if (!string.IsNullOrWhiteSpace(field.LookupDisplayField) && !HasColumn(tables, table, field.LookupDisplayField)) return $"{table}.{field.LookupDisplayField}";
        }
        return null;
    }

    private static bool HasColumn(IReadOnlyList<ProjectTableSchema> tables, string table, string column) =>
        tables.FirstOrDefault(item => item.TableName.Equals(table, StringComparison.OrdinalIgnoreCase))?.Columns
            .Any(item => item.Name.Equals(column, StringComparison.OrdinalIgnoreCase)) == true;
}
