#nullable enable
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;

namespace Configurator.Core.Compare;

public static class CompareEngine
{
    private const int PageSize = 500;
    private static readonly JsonSerializerOptions RelationOptions = new() { PropertyNameCaseInsensitive = true };
    public static async Task<CompareResult> CompareAsync(
        CompareSource a,
        CompareSource b,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(a.Workspace);
        ArgumentNullException.ThrowIfNull(b.Workspace);

        var started = DateTimeOffset.UtcNow;
        var architecture = new List<CompareDifference>();
        var objects = new List<CompareDifference>();
        progress?.Report("Чтение метаданных проектов");

        var snapshotA = await ReadMetadataAsync(a, cancellationToken).ConfigureAwait(false);
        var snapshotB = await ReadMetadataAsync(b, cancellationToken).ConfigureAwait(false);

        var sameOfflineArchitecture = SameOfflineArchitecture(snapshotA, snapshotB);
        if (!sameOfflineArchitecture)
        {
            AddMetadataIssues(architecture, "Источник A", snapshotA);
            AddMetadataIssues(architecture, "Источник B", snapshotB);
            if (snapshotA.ClassReadFailed || snapshotB.ClassReadFailed ||
                !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(snapshotA.Classes),
                    JsonSerializer.SerializeToNode(snapshotB.Classes)))
                architecture.AddRange(CompareClasses(snapshotA, snapshotB));
            architecture.AddRange(CompareTables(snapshotA, snapshotB));
            architecture.AddRange(CompareRelations(snapshotA, snapshotB));
        }
        architecture.AddRange(await CompareProjectSettingsAsync(a, b, cancellationToken).ConfigureAwait(false));

        progress?.Report("Сравнение данных объектов");
        if (!sameOfflineArchitecture ||
            !SameSnapshotRows(a.OfflineProject!.LocalRecords, b.OfflineProject!.LocalRecords, cancellationToken))
        {
            await CompareObjectsAsync(a, b, snapshotA, snapshotB, objects, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return new CompareResult(architecture, objects, started, DateTimeOffset.UtcNow);
    }

    private static async Task<IReadOnlyList<CompareDifference>> CompareProjectSettingsAsync(
        CompareSource a, CompareSource b, CancellationToken cancellationToken)
    {
        ProjectCompareSettings? settingsA;
        ProjectCompareSettings? settingsB;
        try
        {
            settingsA = await CompareMetadataTransferPreview.ReadProjectSettingsAsync(a, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [new CompareDifference("Проектные текстовые списки и темы · источник A", null, null,
                CompareStatus.Error, Issue: "Не удалось прочитать локальные настройки проекта.")];
        }
        try
        {
            settingsB = await CompareMetadataTransferPreview.ReadProjectSettingsAsync(b, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [new CompareDifference("Проектные текстовые списки и темы · источник B", null, null,
                CompareStatus.Error, Issue: "Не удалось прочитать локальные настройки проекта.")];
        }
        if (settingsA is null && settingsB is null) return Array.Empty<CompareDifference>();
        if (settingsA is null || settingsB is null)
            return [new CompareDifference("Проектные текстовые списки и темы", null, null,
                CompareStatus.NotComparable, Issue: "Не удалось прочитать настройки обеих сторон сравнения.")];

        var differences = new List<CompareDifference>();
        differences.AddRange(CompareProjectItems("Текстовые списки", settingsA.TextLists, settingsB.TextLists,
            item => $"{item.SourceTable.Trim()}|{item.Name.Trim()}", SerializeTextListForCompare,
            item => $"{item.SourceTable} · {item.Items.Count} записей · {item.KeyColumn} → {item.DisplayColumn}", CompareMetadataKind.ProjectTextList));
        differences.AddRange(CompareProjectItems("Темы проекта", settingsA.ThemePresets, settingsB.ThemePresets,
            item => item.Name.Trim(), item => JsonSerializer.Serialize(item),
            item => $"{item.Name} · фон {item.Base} · акцент {item.Accent}", CompareMetadataKind.ThemePreset));
        var fingerprintA = CompareMetadataTransferPreview.FingerprintProjectSettings(settingsA);
        var fingerprintB = CompareMetadataTransferPreview.FingerprintProjectSettings(settingsB);
        return differences.Select(row => row with { FingerprintA = fingerprintA, FingerprintB = fingerprintB }).ToArray();
    }

    private static string SerializeTextListForCompare(ProjectTextList item) => JsonSerializer.Serialize(new
    {
        item.Name,
        item.SourceTable,
        item.KeyColumn,
        item.DisplayColumn,
        Items = item.Items.OrderBy(value => value.Key, StringComparer.Ordinal).ThenBy(value => value.Text, StringComparer.Ordinal)
    });

    private static IReadOnlyList<CompareDifference> CompareProjectItems<T>(
        string title, IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, string> keyOf,
        Func<T, string> serialize, Func<T, string> display, CompareMetadataKind kind)
    {
        var groupsA = a.GroupBy(keyOf, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var groupsB = b.GroupBy(keyOf, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new List<CompareDifference>();
        foreach (var key in groupsA.Keys.Union(groupsB.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            groupsA.TryGetValue(key, out var itemsA);
            groupsB.TryGetValue(key, out var itemsB);
            var status = itemsA?.Length > 1 || itemsB?.Length > 1 ? CompareStatus.Ambiguous
                : itemsA is null ? CompareStatus.OnlyB
                : itemsB is null ? CompareStatus.OnlyA
                : string.Equals(serialize(itemsA[0]), serialize(itemsB[0]), StringComparison.Ordinal)
                    ? CompareStatus.Equal : CompareStatus.Changed;
            result.Add(new CompareDifference($"{title} · {key}", itemsA is { Length: > 0 } ? display(itemsA[0]) : null,
                itemsB is { Length: > 0 } ? display(itemsB[0]) : null, status,
                Issue: status == CompareStatus.Ambiguous ? "Идентификатор настройки повторяется." : null,
                MetadataKind: kind, MetadataKey: key,
                DetailAValue: itemsA is { Length: > 0 } ? serialize(itemsA[0]) : null,
                DetailBValue: itemsB is { Length: > 0 } ? serialize(itemsB[0]) : null));
        }
        return result;
    }

    private static bool SameOfflineArchitecture(SourceSnapshot a, SourceSnapshot b)
    {
        if (a.Source.OfflineProject is not { } projectA || b.Source.OfflineProject is not { } projectB ||
            a.Source.PhysicalSchema is null || b.Source.PhysicalSchema is null ||
            a.Source.MetadataIssue is not null || b.Source.MetadataIssue is not null ||
            a.Issues.Count != 0 || b.Issues.Count != 0 || a.MetadataFingerprint != b.MetadataFingerprint)
            return false;

        return JsonNode.DeepEquals(ComparableArchitecture(projectA), ComparableArchitecture(projectB)) &&
            JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a.Schemas), JsonSerializer.SerializeToNode(b.Schemas));
    }

    private static JsonObject ComparableArchitecture(ConfiguratorProject project)
    {
        var architecture = (JsonObject)project.Architecture.DeepClone();
        architecture.Remove("capturedUtc");
        architecture.Remove("physicalSchemaCapturedUtc");
        if (architecture["physicalSchema"] is JsonObject schema)
            schema.Remove(nameof(DatabaseSchema.AnalyzedAtUtc));
        return architecture;
    }

    private static bool SameSnapshotRows(
        IReadOnlyList<ProjectRecord> a, IReadOnlyList<ProjectRecord> b, CancellationToken cancellationToken)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(a[i].TableName, b[i].TableName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a[i].Key, b[i].Key, StringComparison.Ordinal) ||
                !JsonNode.DeepEquals(a[i].Values, b[i].Values))
                return false;
        }
        return true;
    }

    private static async Task<SourceSnapshot> ReadMetadataAsync(CompareSource source, CancellationToken cancellationToken)
    {
        var schemas = Array.Empty<ProjectTableSchema>();
        var classes = new List<ClassDefinition>();
        IReadOnlyList<ClassDefinition> rawClasses = Array.Empty<ClassDefinition>();
        JsonObject? graph = null;
        var issues = new List<CompareDifference>();
        var tableReadFailed = false;
        var classReadFailed = false;
        var relationReadFailed = false;

        try
        {
            schemas = (await source.Workspace.GetTablesAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            tableReadFailed = true;
            issues.Add(Error("Метаданные таблиц", ex));
        }

        try
        {
            var projectClasses = await source.Workspace.GetClassDefinitionsAsync(cancellationToken).ConfigureAwait(false);
            rawClasses = projectClasses.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            classReadFailed = true;
            issues.Add(Error("Метаданные классов", ex));
        }

        try
        {
            graph = await source.Workspace.GetGraphRelationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            relationReadFailed = true;
            issues.Add(Error("Метаданные логических связей", ex));
        }

        var metadataFingerprint = classReadFailed || relationReadFailed || graph is null
            ? null
            : FingerprintMetadata(rawClasses, graph);
        classes = classReadFailed ? SystemClassCatalog.CreateDefaults() : MergeSystemClasses(rawClasses);
        return new SourceSnapshot(source, schemas, classes, graph, metadataFingerprint, issues,
            tableReadFailed, classReadFailed, relationReadFailed);
    }

    internal static string FingerprintMetadata(IReadOnlyList<ClassDefinition> classes, JsonObject graph)
    {
        var document = JsonSerializer.SerializeToUtf8Bytes(new MetadataFingerprintDocument(classes, graph));
        return Convert.ToHexString(SHA256.HashData(document));
    }

    private static List<ClassDefinition> MergeSystemClasses(IReadOnlyList<ClassDefinition> projectClasses)
    {
        var result = SystemClassCatalog.CreateDefaults();
        var overlays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in projectClasses)
        {
            if (definition is null || string.IsNullOrWhiteSpace(definition.Id)) continue;
            var system = result.FirstOrDefault(x => x.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
            if (system is null || !definition.IsSystem || !overlays.Add(definition.Id))
            {
                result.Add(definition);
                continue;
            }

            system.Name = string.IsNullOrWhiteSpace(definition.Name) ? system.Name : definition.Name;
            system.Category = string.IsNullOrWhiteSpace(definition.Category) ? system.Category : definition.Category;
            system.Available = definition.Available;
            system.ClassNumber = definition.ClassNumber > 0 ? definition.ClassNumber : system.ClassNumber;
            system.Description = string.IsNullOrWhiteSpace(definition.Description) ? system.Description : definition.Description;
            system.BaseClassId = definition.BaseClassId;
            system.Fields = definition.Fields ?? new List<FieldDefinition>();
            system.Types = definition.Types ?? new List<ClassTypeDefinition>();
            system.StorageMappings = definition.StorageMappings ?? new List<StorageMapping>();
            system.ComplexTables = definition.ComplexTables ?? new List<ComplexTableDefinition>();
        }
        return result;
    }

    private static void AddMetadataIssues(List<CompareDifference> destination, string side, SourceSnapshot snapshot)
    {
        destination.AddRange(snapshot.Issues.Select(x => x with { Title = $"{side} · {x.Title}" }));
        if (!string.IsNullOrWhiteSpace(snapshot.Source.MetadataIssue))
        {
            destination.Add(new CompareDifference(
                $"{side} · Физические метаданные",
                snapshot.Source.MetadataIssue,
                null,
                CompareStatus.NotComparable,
                Issue: "Сведения о ключах и связях могут быть неполными."));
        }
    }

    private static IReadOnlyList<CompareDifference> CompareClasses(SourceSnapshot snapshotA, SourceSnapshot snapshotB)
    {
        var classesA = snapshotA.Classes;
        var classesB = snapshotB.Classes;
        var matchedB = new HashSet<ClassDefinition>();
        var groups = new List<CompareDifference>();
        var byIdA = classesA.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var byIdB = classesB.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        if (snapshotA.ClassReadFailed || snapshotB.ClassReadFailed)
        {
            if (!snapshotA.ClassReadFailed)
                groups.AddRange(classesA.Select(x => ClassDifference(x, null, CompareStatus.NotComparable,
                    "Не удалось прочитать метаданные классов другого источника.")));
            if (!snapshotB.ClassReadFailed)
                groups.AddRange(classesB.Select(x => ClassDifference(null, x, CompareStatus.NotComparable,
                    "Не удалось прочитать метаданные классов другого источника.")));
            return [new CompareDifference("Классы", null, null, CompareStatus.NotComparable, groups,
                Issue: "Метаданные классов неполные.")];
        }

        foreach (var classA in classesA)
        {
            byIdA.TryGetValue(classA.Id, out var sameIdA);
            byIdB.TryGetValue(classA.Id, out var sameIdB);
            if (sameIdA is null || sameIdA.Length != 1 || sameIdB is null || sameIdB.Length != 1)
            {
                var sameName = classesB.Where(x => x.Name.Equals(classA.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var status = sameIdA?.Length > 1 || sameIdB?.Length > 1 || sameName.Length > 0
                    ? CompareStatus.Ambiguous
                    : CompareStatus.OnlyA;
                groups.Add(ClassDifference(classA, null, status,
                    status == CompareStatus.Ambiguous ? "Идентификатор класса отсутствует, повторяется или различается в проектах." : null));
                continue;
            }

            var classB = sameIdB[0];
            matchedB.Add(classB);
            groups.Add(ClassDifference(classA, classB, CompareStatus.Equal, null));
        }

        foreach (var classB in classesB.Where(x => !matchedB.Contains(x)))
        {
            var sameId = byIdB[classB.Id].Length > 1;
            var nameOnA = classesA.Any(x => x.Name.Equals(classB.Name, StringComparison.OrdinalIgnoreCase));
            var status = sameId || nameOnA ? CompareStatus.Ambiguous : CompareStatus.OnlyB;
            groups.Add(ClassDifference(null, classB, status,
                status == CompareStatus.Ambiguous ? "Идентификатор класса отсутствует, повторяется или различается в проектах." : null));
        }

        return [new CompareDifference("Классы", null, null, Aggregate(groups), groups.Select(row => row with
        {
            FingerprintA = snapshotA.MetadataFingerprint, FingerprintB = snapshotB.MetadataFingerprint
        }).ToArray())];
    }

    private static CompareDifference ClassDifference(
        ClassDefinition? a,
        ClassDefinition? b,
        CompareStatus presenceStatus,
        string? issue)
    {
        if (a is null || b is null)
            return new CompareDifference(
                $"Класс · {a?.Name ?? b?.Name}",
                a?.Id,
                b?.Id,
                presenceStatus,
                Fields(a, b).Concat(ClassTypes(a, b)).Concat(ComplexTables(a, b)).ToArray(),
                Issue: issue,
                MetadataKind: CompareMetadataKind.ClassDefinition,
                MetadataKey: a?.Id ?? b?.Id);

        var children = new List<CompareDifference>
        {
            ValueDifference("Название", a.Name, b.Name),
            ValueDifference("Категория", a.Category, b.Category),
            ValueDifference("Доступен", YesNo(a.Available), YesNo(b.Available)),
            ValueDifference("Номер класса", a.ClassNumber.ToString(CultureInfo.InvariantCulture), b.ClassNumber.ToString(CultureInfo.InvariantCulture)),
            ValueDifference("Базовый класс", a.BaseClassId, b.BaseClassId),
            ValueDifference("Описание", a.Description, b.Description),
            CompareStorageMappings(a, b)
        };
        children.AddRange(Fields(a, b));
        children.AddRange(ClassTypes(a, b));
        children.AddRange(ComplexTables(a, b));
        return new CompareDifference($"Класс · {a.Name}", a.Id, b.Id, Aggregate(children), children,
            Issue: issue, MetadataKind: CompareMetadataKind.ClassDefinition, MetadataKey: a.Id);
    }

    private static IReadOnlyList<CompareDifference> Fields(ClassDefinition? a, ClassDefinition? b)
    {
        var fieldsA = a?.Fields ?? new List<FieldDefinition>();
        var fieldsB = b?.Fields ?? new List<FieldDefinition>();
        var output = new List<CompareDifference>();
        var matched = new HashSet<FieldDefinition>();
        var idsA = fieldsA.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var idsB = fieldsB.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var fieldA in fieldsA)
        {
            var sameNameA = fieldsA.Count(x => x.Name.Equals(fieldA.Name, StringComparison.OrdinalIgnoreCase)) > 1;
            var sameNameB = fieldsB.Where(x => x.Name.Equals(fieldA.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameNameA || sameNameB.Length > 1)
            {
                output.Add(FieldDifference(fieldA, sameNameB.FirstOrDefault(), CompareStatus.Ambiguous));
                continue;
            }

            if (!idsA.TryGetValue(fieldA.Id, out var sameIdA) || sameIdA.Length != 1 ||
                !idsB.TryGetValue(fieldA.Id, out var sameIdB) || sameIdB.Length != 1)
            {
                var fieldB = sameNameB.SingleOrDefault();
                output.Add(FieldDifference(fieldA, fieldB,
                    fieldB is null && sameNameB.Length == 0 ? CompareStatus.OnlyA : CompareStatus.Ambiguous));
                if (fieldB is not null) matched.Add(fieldB);
                continue;
            }

            var match = sameIdB[0];
            matched.Add(match);
            output.Add(FieldDifference(fieldA, match, CompareStatus.Equal));
        }

        foreach (var fieldB in fieldsB.Where(x => !matched.Contains(x)))
        {
            var status = fieldsA.Any(x => x.Name.Equals(fieldB.Name, StringComparison.OrdinalIgnoreCase))
                ? CompareStatus.Ambiguous
                : CompareStatus.OnlyB;
            output.Add(FieldDifference(null, fieldB, status));
        }
        return output;
    }

    private static CompareDifference FieldDifference(FieldDefinition? a, FieldDefinition? b, CompareStatus presenceStatus)
    {
        if (a is null || b is null)
            return new CompareDifference($"Поле · {a?.Name ?? b?.Name}", a?.Id, b?.Id, presenceStatus);
        var children = new[]
        {
            ValueDifference("Название", a.Name, b.Name),
            ValueDifference("Тип данных", a.DataType, b.DataType),
            ValueDifference("Размер", a.Size.ToString(CultureInfo.InvariantCulture), b.Size.ToString(CultureInfo.InvariantCulture)),
            ValueDifference("Смещение", a.Offset.ToString(CultureInfo.InvariantCulture), b.Offset.ToString(CultureInfo.InvariantCulture)),
            ValueDifference("Битовое смещение", a.BitOffset.ToString(CultureInfo.InvariantCulture), b.BitOffset.ToString(CultureInfo.InvariantCulture)),
            ValueDifference("Длина в битах", a.BitLength.ToString(CultureInfo.InvariantCulture), b.BitLength.ToString(CultureInfo.InvariantCulture)),
            ValueDifference("Допускает NULL", YesNo(a.Nullable), YesNo(b.Nullable)),
            ValueDifference("Значение по умолчанию", a.DefaultValue, b.DefaultValue),
            ValueDifference("Описание", a.Description, b.Description),
            ValueDifference("Группа", a.Group, b.Group),
            ValueDifference("Справочник", LookupSignature(a), LookupSignature(b))
        };
        return new CompareDifference($"Поле · {a.Name}", a.Id, b.Id, Aggregate(children), children);
    }

    private static IReadOnlyList<CompareDifference> ClassTypes(ClassDefinition? a, ClassDefinition? b)
    {
        var typesA = a?.Types ?? new List<ClassTypeDefinition>();
        var typesB = b?.Types ?? new List<ClassTypeDefinition>();
        var output = new List<CompareDifference>();
        var matchedB = new HashSet<ClassTypeDefinition>();
        foreach (var typeA in typesA)
        {
            var matches = typesB.Where(x => x.Id.Equals(typeA.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                var sameName = typesB.Any(x => x.Name.Equals(typeA.Name, StringComparison.OrdinalIgnoreCase));
                output.Add(new CompareDifference($"Тип · {typeA.Name}", typeA.Id, null,
                    matches.Length > 1 || sameName ? CompareStatus.Ambiguous : CompareStatus.OnlyA));
                continue;
            }

            var typeB = matches[0];
            matchedB.Add(typeB);
            var valuesA = typeA.Values ?? new Dictionary<string, string>();
            var valuesB = typeB.Values ?? new Dictionary<string, string>();
            var children = new List<CompareDifference>
            {
                ValueDifference("Название", typeA.Name, typeB.Name),
                ValueDifference("Описание", typeA.Description, typeB.Description)
            };
            foreach (var key in valuesA.Keys.Union(valuesB.Keys, StringComparer.OrdinalIgnoreCase))
            {
                valuesA.TryGetValue(key, out var valueA);
                valuesB.TryGetValue(key, out var valueB);
                children.Add(ValueDifference($"Значение · {key}", valueA, valueB));
            }
            output.Add(new CompareDifference($"Тип · {typeA.Name}", typeA.Id, typeB.Id, Aggregate(children), children));
        }

        foreach (var typeB in typesB.Where(x => !matchedB.Contains(x)))
        {
            var sameName = typesA.Any(x => x.Name.Equals(typeB.Name, StringComparison.OrdinalIgnoreCase));
            output.Add(new CompareDifference($"Тип · {typeB.Name}", null, typeB.Id,
                sameName ? CompareStatus.Ambiguous : CompareStatus.OnlyB));
        }
        return output;
    }

    private static IReadOnlyList<CompareDifference> ComplexTables(ClassDefinition? a, ClassDefinition? b)
    {
        var definitionsA = a?.ComplexTables ?? new List<ComplexTableDefinition>();
        var definitionsB = b?.ComplexTables ?? new List<ComplexTableDefinition>();
        var output = new List<CompareDifference>();
        var matchedB = new HashSet<ComplexTableDefinition>();
        foreach (var definitionA in definitionsA)
        {
            var role = string.IsNullOrWhiteSpace(definitionA.Role) ? definitionA.TableName : definitionA.Role;
            var matches = definitionsB.Where(x =>
                string.Equals(string.IsNullOrWhiteSpace(x.Role) ? x.TableName : x.Role, role,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                output.Add(new CompareDifference($"Составная таблица · {definitionA.TableName}", definitionA.TableName, null,
                    matches.Length > 1 ? CompareStatus.Ambiguous : CompareStatus.OnlyA));
                continue;
            }

            var definitionB = matches[0];
            matchedB.Add(definitionB);
            var details = new List<CompareDifference>
            {
                ValueDifference("Таблица", definitionA.TableName, definitionB.TableName),
                ValueDifference("Роль", definitionA.Role, definitionB.Role),
                ValueDifference("Описание", definitionA.Description, definitionB.Description)
            };
            details.AddRange(CompareFieldLists(definitionA.Fields, definitionB.Fields));
            output.Add(new CompareDifference($"Составная таблица · {role}", definitionA.TableName, definitionB.TableName,
                Aggregate(details), details));
        }

        foreach (var definitionB in definitionsB.Where(x => !matchedB.Contains(x)))
            output.Add(new CompareDifference($"Составная таблица · {definitionB.TableName}", null, definitionB.TableName,
                CompareStatus.OnlyB));
        return output;
    }

    private static IReadOnlyList<CompareDifference> CompareFieldLists(
        IReadOnlyList<FieldDefinition>? a,
        IReadOnlyList<FieldDefinition>? b)
    {
        var classA = a is null ? null : new ClassDefinition { Fields = a.ToList() };
        var classB = b is null ? null : new ClassDefinition { Fields = b.ToList() };
        return Fields(classA, classB);
    }

    private static CompareDifference CompareStorageMappings(ClassDefinition a, ClassDefinition b)
    {
        var mappingsA = a.StorageMappings ?? new List<StorageMapping>();
        var mappingsB = b.StorageMappings ?? new List<StorageMapping>();
        var signaturesA = mappingsA.Select(MappingSignature).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var signaturesB = mappingsB.Select(MappingSignature).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var displayA = mappingsA.Select(MappingDisplay).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var displayB = mappingsB.Select(MappingDisplay).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return new CompareDifference("Сопоставления с таблицами", string.Join("; ", displayA), string.Join("; ", displayB),
            signaturesA.SequenceEqual(signaturesB, StringComparer.Ordinal)
                ? CompareStatus.Equal
                : CompareStatus.Changed);
    }

    private static string MappingSignature(StorageMapping mapping) =>
        $"{mapping.Role}|{NormalizeTable(mapping.TableName)}|{string.Join(",", mapping.KeyColumns ?? new List<string>())}";

    private static string MappingDisplay(StorageMapping mapping) =>
        $"{RoleLabel(mapping.Role)} · {NormalizeTable(mapping.TableName)} · {string.Join(", ", mapping.KeyColumns ?? new List<string>())}";

    private static string RoleLabel(string role) => role.ToLowerInvariant() switch
    {
        "primary" => "основная",
        "child" => "дочерняя",
        "complex" => "составная",
        _ => role
    };

    private static string LookupSignature(FieldDefinition field) =>
        $"{NormalizeTable(field.LookupTable)}|{field.LookupKeyField}|{field.LookupDisplayField}";

    private static IReadOnlyList<CompareDifference> CompareTables(SourceSnapshot a, SourceSnapshot b)
    {
        var tablesA = BuildCompareTables(a);
        var tablesB = BuildCompareTables(b);
        if (a.TableReadFailed || b.TableReadFailed)
        {
            var available = new List<CompareDifference>();
            if (!a.TableReadFailed)
                available.AddRange(tablesA.Select(x => TableDifference(x, null, CompareStatus.NotComparable, a, b,
                    "Не удалось прочитать метаданные таблиц другого источника.")));
            if (!b.TableReadFailed)
                available.AddRange(tablesB.Select(x => TableDifference(null, x, CompareStatus.NotComparable, a, b,
                    "Не удалось прочитать метаданные таблиц другого источника.")));
            return [new CompareDifference("Таблицы", null, null, CompareStatus.NotComparable, available,
                Issue: "Метаданные таблиц неполные.")];
        }
        var output = new List<CompareDifference>();
        var matchedB = new HashSet<CompareTable>();

        foreach (var tableA in tablesA)
        {
            var matches = FindTableMatches(tableA, tablesB);
            if (matches.Count == 1)
            {
                var tableB = matches[0];
                matchedB.Add(tableB);
                output.Add(TableDifference(tableA, tableB, CompareStatus.Equal, a, b));
            }
            else if (matches.Count > 1)
            {
                matchedB.UnionWith(matches);
                output.Add(TableDifference(tableA, null, CompareStatus.Ambiguous, a, b,
                    "Несколько таблиц соответствуют классу или имени таблицы."));
            }
            else
            {
                var similar = tablesB.Any(x => TableName(x).Equals(TableName(tableA), StringComparison.OrdinalIgnoreCase));
                output.Add(TableDifference(tableA, null, similar ? CompareStatus.Ambiguous : CompareStatus.OnlyA, a, b,
                    similar ? "Сопоставления таблиц различаются, безопасно объединить их нельзя." : null));
            }
        }

        foreach (var tableB in tablesB.Where(x => !matchedB.Contains(x)))
        {
            var similar = tablesA.Any(x => TableName(x).Equals(TableName(tableB), StringComparison.OrdinalIgnoreCase));
            output.Add(TableDifference(null, tableB, similar ? CompareStatus.Ambiguous : CompareStatus.OnlyB, a, b,
                similar ? "Сопоставления таблиц различаются, безопасно объединить их нельзя." : null));
        }

        return [new CompareDifference("Таблицы", null, null, Aggregate(output), output)];
    }

    private static List<CompareTable> BuildCompareTables(SourceSnapshot source)
    {
        var result = new List<CompareTable>();
        var explicitMappings = source.Classes
            .SelectMany(x => x.StorageMappings ?? new List<StorageMapping>())
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.TableName))
            .Select(x => x.TableName)
            .ToArray();
        foreach (var definition in source.Classes)
        {
            foreach (var mapping in GetMappedNames(definition, source.Schemas, explicitMappings))
            {
                var schema = FindSchema(source.Schemas, mapping.Name);
                if (schema is null) continue;
                result.Add(CreateCompareTable(source, definition, schema, mapping.Role));
            }
        }

        var mappedSchemas = new HashSet<ProjectTableSchema>(result.Select(x => x.Schema));
        foreach (var schema in source.Schemas.Where(x => !mappedSchemas.Contains(x)))
            result.Add(CreateCompareTable(source, null, schema, null));
        var duplicateSchemas = result.GroupBy(x => x.Schema).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        return result.Select(x => duplicateSchemas.Contains(x.Schema)
            ? x with { HasUnresolvedDependencies = true, MappingRole = "ambiguous" }
            : x).ToList();
    }

    private static IReadOnlyList<(string Name, string Role)> GetMappedNames(
        ClassDefinition definition,
        IReadOnlyList<ProjectTableSchema> schemas,
        IReadOnlyList<string> explicitMappings)
    {
        var mappings = definition.StorageMappings?
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Role) && !string.IsNullOrWhiteSpace(x.TableName))
            .Select(x => (Name: x.TableName, Role: x.Role))
            .ToList() ?? new List<(string Name, string Role)>();
        var result = mappings;
        if (result.Count == 0 && definition.IsSystem)
        {
            result.AddRange(schemas
                .Where(x => ProjectArchitectureResolver.ResolveSystemClassNumber(x.TableName) == definition.ClassNumber &&
                    !explicitMappings.Any(mapping => SameTable(mapping, x.TableName)))
                .Select(x => (x.TableName, "primary")));
        }
        foreach (var child in definition.ComplexTables ?? new List<ComplexTableDefinition>())
            if (!string.IsNullOrWhiteSpace(child.TableName))
                result.Add((child.TableName, string.IsNullOrWhiteSpace(child.Role) ? "child" : child.Role));
        return result;
    }

    private static CompareTable CreateCompareTable(
        SourceSnapshot source,
        ClassDefinition? definition,
        ProjectTableSchema schema,
        string? mappingRole)
    {
        var relations = source.Source.PhysicalSchema?.Relations ?? new List<DatabaseRelation>();
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = source.Source.PhysicalSchema is null ||
            !string.IsNullOrWhiteSpace(source.Source.MetadataIssue) || source.ClassReadFailed;
        foreach (var relation in relations)
        {
            if (SameTable(relation.SourceTable, schema.TableName))
                dependencies.UnionWith(relation.SourceColumns);
            if (SameTable(relation.TargetTable, schema.TableName))
                dependencies.UnionWith(relation.TargetColumns);

            if ((SameTable(relation.SourceTable, schema.TableName) || SameTable(relation.TargetTable, schema.TableName)) &&
                (relation.Confidence is RelationConfidence.Low or RelationConfidence.Medium ||
                 relation.Kind == DatabaseRelationKind.UnresolvedCandidate ||
                 FindSchema(source.Schemas, relation.SourceTable) is null ||
                 FindSchema(source.Schemas, relation.TargetTable) is null))
                unresolved = true;
        }

        foreach (var field in definition?.Fields ?? new List<FieldDefinition>())
        {
            if (string.IsNullOrWhiteSpace(field.LookupTable) || string.IsNullOrWhiteSpace(field.LookupKeyField))
                continue;
            dependencies.Add(field.Name);
            if (FindSchema(source.Schemas, field.LookupTable) is null)
                unresolved = true;
        }

        foreach (var field in source.Classes.SelectMany(x => x.Fields ?? new List<FieldDefinition>()))
        {
            if (string.IsNullOrWhiteSpace(field.LookupKeyField) || !SameTable(field.LookupTable, schema.TableName))
                continue;
            dependencies.Add(field.LookupKeyField);
            if (!string.IsNullOrWhiteSpace(field.LookupDisplayField))
                dependencies.Add(field.LookupDisplayField);
        }

        if (source.GraphRelations is not null)
        {
            try
            {
                var document = source.GraphRelations.Deserialize<GraphRelationProjectDocument>(RelationOptions);
                foreach (var relation in document?.ManualRelations ?? new List<GraphRelationDefinition>())
                {
                    var sourceMatches = SameTable(relation.Source, schema.TableName);
                    var targetMatches = SameTable(relation.Target, schema.TableName);
                    if (sourceMatches) dependencies.Add(relation.SourceColumn);
                    if (targetMatches) dependencies.Add(relation.TargetColumn);
                    if ((sourceMatches || targetMatches) &&
                        (FindSchema(source.Schemas, relation.Source) is null ||
                         FindSchema(source.Schemas, relation.Target) is null))
                        unresolved = true;
                }
            }
            catch (JsonException)
            {
                unresolved = true;
            }
        }

        if (source.RelationReadFailed) unresolved = true;
        return new CompareTable(schema, definition, dependencies, unresolved, mappingRole, source.MetadataFingerprint);
    }

    private static List<CompareTable> FindTableMatches(CompareTable table, IReadOnlyList<CompareTable> candidates)
    {
        if (table.Class is not null)
        {
            var byClassId = candidates.Where(x => x.Class is not null &&
                x.Class.Id.Equals(table.Class.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.MappingRole, table.MappingRole, StringComparison.OrdinalIgnoreCase)).ToList();
            var exact = byClassId.Where(x => TableName(x).Equals(TableName(table), StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;
            if (byClassId.Count > 0) return byClassId;
        }
        return candidates.Where(x => x.Class is null &&
            TableName(x).Equals(TableName(table), StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static CompareDifference TableDifference(
        CompareTable? a,
        CompareTable? b,
        CompareStatus presenceStatus,
        SourceSnapshot sourceA,
        SourceSnapshot sourceB,
        string? issue = null)
    {
        if (a is null || b is null)
        {
            var table = a ?? b!;
            return new CompareDifference(
                $"Таблица · {table.Class?.Name ?? TableName(table)}",
                a is null ? null : TableName(a),
                b is null ? null : TableName(b),
                presenceStatus,
                TableDetails(a, b, presenceStatus),
                Issue: issue ?? (table.HasUnresolvedDependencies ? "Не все логические зависимости удалось определить." : null));
        }

        var details = new List<CompareDifference>();
        details.Add(ValueDifference("Сопоставление таблицы", TableName(a), TableName(b)));
        details.AddRange(CompareColumns(a, b, sourceA.Source.PhysicalSchema, sourceB.Source.PhysicalSchema));
        details.AddRange(CompareKeys(a, b, sourceA.Source.PhysicalSchema, sourceB.Source.PhysicalSchema));
        return new CompareDifference(
            $"Таблица · {a.Class?.Name ?? TableName(a)}",
            TableName(a),
            TableName(b),
            Aggregate(details),
            details,
            Issue: issue ?? (a.HasUnresolvedDependencies || b.HasUnresolvedDependencies
                ? "Не все логические зависимости удалось определить."
                : null));
    }

    private static IReadOnlyList<CompareDifference> TableDetails(
        CompareTable? a,
        CompareTable? b,
        CompareStatus presenceStatus)
    {
        var table = a ?? b!;
        var details = new List<CompareDifference>();
        foreach (var column in table.Schema.Columns)
            details.Add(new CompareDifference($"Столбец · {column.Name}", a is null ? null : column.DataType,
                b is null ? null : column.DataType, presenceStatus,
                Column: column.Name));
        return details;
    }

    private static IReadOnlyList<CompareDifference> CompareColumns(
        CompareTable a,
        CompareTable b,
        DatabaseSchema? physicalA,
        DatabaseSchema? physicalB)
    {
        var output = new List<CompareDifference>();
        var columnsA = a.Schema.Columns.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var columnsB = b.Schema.Columns.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var names = columnsA.Keys.Union(columnsB.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            columnsA.TryGetValue(name, out var columnA);
            columnsB.TryGetValue(name, out var columnB);
            if (columnA is null || columnB is null)
            {
                output.Add(new CompareDifference($"Столбец · {name}", columnA?.DataType, columnB?.DataType,
                    columnA is null ? CompareStatus.OnlyB : CompareStatus.OnlyA, Column: name));
                continue;
            }

            var physicalColumnA = FindPhysicalTable(physicalA, a.Schema.TableName)?.Columns
                .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var physicalColumnB = FindPhysicalTable(physicalB, b.Schema.TableName)?.Columns
                .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var properties = new List<CompareDifference>
            {
                ValueDifference("Тип", columnA.DataType, columnB.DataType),
                ValueDifference("Допускает NULL", YesNo(columnA.IsNullable), YesNo(columnB.IsNullable)),
                ValueDifference("Максимальная длина", columnA.MaxLength.ToString(CultureInfo.InvariantCulture), columnB.MaxLength.ToString(CultureInfo.InvariantCulture)),
                ValueDifference("Точность", columnA.Precision.ToString(CultureInfo.InvariantCulture), columnB.Precision.ToString(CultureInfo.InvariantCulture)),
                ValueDifference("Масштаб", columnA.Scale.ToString(CultureInfo.InvariantCulture), columnB.Scale.ToString(CultureInfo.InvariantCulture)),
                ValueDifference("Идентификатор", YesNo(columnA.IsIdentity), YesNo(columnB.IsIdentity)),
                ValueDifference("Вычисляемый", YesNo(columnA.IsComputed), YesNo(columnB.IsComputed)),
                CompareDefaults(columnA, columnB, physicalColumnA, physicalColumnB)
            };
            output.Add(new CompareDifference($"Столбец · {name}", columnA.DataType, columnB.DataType,
                Aggregate(properties), properties, Column: name));
        }
        return output;
    }

    private static CompareDifference CompareDefaults(
        ProjectColumnSchema a,
        ProjectColumnSchema b,
        DatabaseColumn? physicalA,
        DatabaseColumn? physicalB)
    {
        if ((physicalA is null || physicalB is null) && a.HasDefault != b.HasDefault)
            return new CompareDifference("Значение по умолчанию", YesNo(a.HasDefault), YesNo(b.HasDefault),
                CompareStatus.NotComparable, Issue: "В сохранённых метаданных нет выражения значения по умолчанию.");

        var defaultA = physicalA?.DefaultDefinition ?? (a.HasDefault ? "<задано>" : "");
        var defaultB = physicalB?.DefaultDefinition ?? (b.HasDefault ? "<задано>" : "");
        return ValueDifference("Значение по умолчанию", defaultA, defaultB);
    }

    private static IReadOnlyList<CompareDifference> CompareKeys(
        CompareTable a,
        CompareTable b,
        DatabaseSchema? physicalA,
        DatabaseSchema? physicalB)
    {
        var hasPhysicalKeys = FindPhysicalTable(physicalA, TableName(a)) is not null &&
            FindPhysicalTable(physicalB, TableName(b)) is not null;
        var keysA = KeySignatures(a, hasPhysicalKeys ? physicalA : null);
        var keysB = KeySignatures(b, hasPhysicalKeys ? physicalB : null);
        return [ValueDifference("Первичный и уникальные ключи", string.Join("; ", keysA), string.Join("; ", keysB))];
    }

    private static string[] KeySignatures(CompareTable table, DatabaseSchema? physical)
    {
        var keys = new List<string>();
        var actual = FindPhysicalTable(physical, table.Schema.TableName);
        if (actual is null && table.Schema.HasStableKey && table.Schema.KeyColumns.Count > 0)
            keys.Add("Ключ сопоставления: " + string.Join(",", table.Schema.KeyColumns));
        if (actual is not null)
            keys.AddRange(actual.Keys.Where(x => x.IsPrimary || x.IsUnique)
                .Select(x => (x.IsPrimary ? "Первичный ключ: " : "Уникальный ключ: ") + string.Join(",", x.Columns)));
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<CompareDifference> CompareRelations(SourceSnapshot a, SourceSnapshot b)
    {
        var relationsA = GetRelationSignatures(a);
        var relationsB = GetRelationSignatures(b);
        var incomplete = a.RelationReadFailed || b.RelationReadFailed || a.TableReadFailed || b.TableReadFailed ||
            a.Source.PhysicalSchema is null || b.Source.PhysicalSchema is null ||
            !string.IsNullOrWhiteSpace(a.Source.MetadataIssue) || !string.IsNullOrWhiteSpace(b.Source.MetadataIssue);
        var all = relationsA.Keys.Union(relationsB.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var children = new List<CompareDifference>();
        foreach (var signature in all)
        {
            var inA = relationsA.TryGetValue(signature, out var relationA);
            var inB = relationsB.TryGetValue(signature, out var relationB);
            var unresolved = inA && !relationA!.Resolved || inB && !relationB!.Resolved;
            var status = incomplete || unresolved
                ? CompareStatus.NotComparable
                : !inA ? CompareStatus.OnlyB
                : !inB ? CompareStatus.OnlyA
                : string.Equals(relationA!.Value, relationB!.Value, StringComparison.Ordinal)
                    ? CompareStatus.Equal
                    : CompareStatus.Changed;
            children.Add(new CompareDifference(
                relationA?.Title ?? relationB?.Title ?? "Связь",
                inA ? relationA!.Value : null,
                inB ? relationB!.Value : null,
                status,
                Issue: incomplete ? "Метаданные связей неполные как минимум в одном источнике." :
                    unresolved ? "Конечная таблица связи отсутствует в сохранённой схеме." : null,
                MetadataKind: !incomplete && !unresolved && (relationA?.IsManual == true || relationB?.IsManual == true)
                    ? CompareMetadataKind.ManualRelation : null,
                MetadataKey: signature) { FingerprintA = a.MetadataFingerprint, FingerprintB = b.MetadataFingerprint });
        }

        if (incomplete)
            children.Add(new CompareDifference("Метаданные связей", null, null, CompareStatus.NotComparable,
                Issue: "Физические метаданные связей недоступны как минимум для одного источника."));
        return [new CompareDifference("Связи", null, null, Aggregate(children), children)];
    }

    private static Dictionary<string, RelationMetadata> GetRelationSignatures(SourceSnapshot source)
    {
        var result = new Dictionary<string, RelationMetadata>(StringComparer.OrdinalIgnoreCase);
        GraphRelationProjectDocument? document = null;
        if (source.GraphRelations is not null)
        {
            try
            {
                document = source.GraphRelations.Deserialize<GraphRelationProjectDocument>(RelationOptions);
            }
            catch (JsonException)
            {
                result["invalid-graph-relations"] = new RelationMetadata(
                    "Не удалось прочитать документ логических связей.", false, false, "Некорректный документ логических связей");
            }
        }

        if (source.Source.PhysicalSchema is { } schema)
        {
            var current = source.Source.Workspace.IsOffline
                ? DatabaseGraphRelationMaterializer.WithProjectTables(schema, source.Schemas)
                : schema;
            foreach (var relation in DatabaseGraphRelationMaterializer.Materialize(current, document))
            {
                var value = relation.Confidence is { } confidence
                    ? $"{ConfidenceLabel(confidence)} · {relation.Rule}"
                    : relation.Rule;
                var title = relation.IsManual ? "Логическая связь" : "Связь";
                result[relation.Id] = new RelationMetadata(value, true, relation.IsManual,
                    $"{title} · {RelationLabel(relation.Source, relation.SourceColumns, relation.Target, relation.TargetColumns)}");
            }
        }
        return result;
    }

    private static string RelationLabel(string source, IEnumerable<string> sourceColumns, string target, IEnumerable<string> targetColumns) =>
        $"{NormalizeTable(source)} ({string.Join(", ", sourceColumns)}) → {NormalizeTable(target)} ({string.Join(", ", targetColumns)})";

    private static string ConfidenceLabel(RelationConfidence confidence) => confidence switch
    {
        RelationConfidence.Confirmed => "Подтверждена",
        RelationConfidence.High => "Высокая уверенность",
        RelationConfidence.Medium => "Средняя уверенность",
        RelationConfidence.Low => "Низкая уверенность",
        _ => "Уверенность не определена"
    };

    private static async Task CompareObjectsAsync(
        CompareSource a,
        CompareSource b,
        SourceSnapshot snapshotA,
        SourceSnapshot snapshotB,
        List<CompareDifference> output,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var classPairs = MatchClasses(snapshotA.Classes, snapshotB.Classes);
        var mappedA = BuildCompareTables(snapshotA);
        var mappedB = BuildCompareTables(snapshotB);
        var handledA = new HashSet<CompareTable>();
        var handledB = new HashSet<CompareTable>();

        if (snapshotA.TableReadFailed || snapshotB.TableReadFailed)
        {
            if (snapshotA.TableReadFailed)
                output.Add(Error("Объекты · Источник A · Метаданные таблиц", new InvalidDataException("Не удалось прочитать метаданные таблиц.")));
            if (snapshotB.TableReadFailed)
                output.Add(Error("Объекты · Источник B · Метаданные таблиц", new InvalidDataException("Не удалось прочитать метаданные таблиц.")));
            var readableTables = snapshotA.TableReadFailed ? mappedB : mappedA;
            foreach (var table in readableTables.Where(x => x.Class is not null))
            {
                try
                {
                    var source = snapshotA.TableReadFailed ? b : a;
                    var sideA = !snapshotA.TableReadFailed;
                    var rows = await ReadTableRecordsAsync(source, table.Schema, cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var row in rows)
                        output.Add(ObjectDifference(sideA ? table : null, sideA ? null : table,
                            sideA ? row : null, sideA ? null : row, CompareStatus.NotComparable,
                            "Не удалось прочитать метаданные таблиц другого источника.", identityCertain: false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    output.Add(Error($"Объекты · {table.Class?.Name ?? TableName(table)}", ex));
                }
            }
            return;
        }

        if (snapshotA.ClassReadFailed || snapshotB.ClassReadFailed)
            classPairs = classPairs.Where(x => x.A.IsSystem && x.B.IsSystem).ToList();

        foreach (var pair in classPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tablesA = mappedA.Where(x => string.Equals(x.Class?.Id, pair.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var tablesB = mappedB.Where(x => string.Equals(x.Class?.Id, pair.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var tableA in tablesA)
            {
                var matches = FindTableMatches(tableA, tablesB);
                if (matches.Count == 1)
                {
                    var tableB = matches[0];
                    handledA.Add(tableA);
                    handledB.Add(tableB);
                    progress?.Report($"Сравнение {pair.Name}");
                    try
                    {
                        output.AddRange(await CompareTableObjectsAsync(a, b, tableA, tableB, cancellationToken)
                            .ConfigureAwait(false));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        output.Add(Error($"Объекты · {pair.Name}", ex));
                    }
                }
                else if (matches.Count == 0)
                {
                    handledA.Add(tableA);
                    try
                    {
                        output.AddRange(await ReadUnpairedObjectsAsync(a, tableA, true, CompareStatus.OnlyA, cancellationToken)
                            .ConfigureAwait(false));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        output.Add(Error($"Объекты · {pair.Name}", ex));
                    }
                }
                else
                {
                    handledA.Add(tableA);
                    handledB.UnionWith(matches);
                    output.Add(new CompareDifference($"Объекты · {pair.Name} · сопоставление таблицы", null, null,
                        CompareStatus.Ambiguous, Issue: "Несколько целевых таблиц соответствуют этому классу."));
                }
            }

            foreach (var tableB in tablesB.Where(x => !handledB.Contains(x)))
            {
                handledB.Add(tableB);
                try
                {
                    output.AddRange(await ReadUnpairedObjectsAsync(b, tableB, false, CompareStatus.OnlyB, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    output.Add(Error($"Объекты · {pair.Name}", ex));
                }
            }
        }

        foreach (var tableA in mappedA.Where(x => x.Class is not null && !handledA.Contains(x)))
        {
            var sameName = mappedB.Where(x => x.Class is not null &&
                x.Class.Name.Equals(tableA.Class!.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.MappingRole, tableA.MappingRole, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameName.Count == 1 && !snapshotA.ClassReadFailed && !snapshotB.ClassReadFailed)
            {
                var tableB = sameName[0];
                handledB.Add(tableB);
                try
                {
                    var rowsA = await ReadTableRecordsAsync(a, tableA.Schema, cancellationToken).ConfigureAwait(false);
                    var rowsB = await ReadTableRecordsAsync(b, tableB.Schema, cancellationToken).ConfigureAwait(false);
                    output.AddRange(rowsA.Select(x => ObjectDifference(tableA, null, x, null, CompareStatus.Ambiguous,
                        "Идентификаторы классов различаются, хотя их названия совпадают.", identityCertain: false)));
                    output.AddRange(rowsB.Select(x => ObjectDifference(null, tableB, null, x, CompareStatus.Ambiguous,
                        "Идентификаторы классов различаются, хотя их названия совпадают.", identityCertain: false)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    output.Add(Error($"Объекты · {tableA.Class?.Name ?? TableName(tableA)}", ex));
                }
                handledA.Add(tableA);
                continue;
            }

            handledA.Add(tableA);
            try
            {
                var status = snapshotA.ClassReadFailed || snapshotB.ClassReadFailed
                    ? CompareStatus.NotComparable
                    : sameName.Count > 0 ? CompareStatus.Ambiguous : CompareStatus.OnlyA;
                var issue = snapshotA.ClassReadFailed || snapshotB.ClassReadFailed
                    ? "Не удалось прочитать метаданные классов одного или обоих источников."
                    : sameName.Count > 0 ? "Для таблицы найдено несколько возможных классов." : null;
                output.AddRange(await ReadUnpairedObjectsAsync(a, tableA, true, status, cancellationToken, issue)
                    .ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.Add(Error($"Объекты · {tableA.Class?.Name ?? TableName(tableA)}", ex));
            }
        }

        foreach (var tableB in mappedB.Where(x => x.Class is not null && !handledB.Contains(x)))
        {
            handledB.Add(tableB);
            try
            {
                var status = snapshotA.ClassReadFailed || snapshotB.ClassReadFailed
                    ? CompareStatus.NotComparable
                    : CompareStatus.OnlyB;
                var issue = snapshotA.ClassReadFailed || snapshotB.ClassReadFailed
                    ? "Не удалось прочитать метаданные классов одного или обоих источников."
                    : null;
                output.AddRange(await ReadUnpairedObjectsAsync(b, tableB, false, status, cancellationToken, issue)
                    .ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.Add(Error($"Объекты · {tableB.Class?.Name ?? TableName(tableB)}", ex));
            }
        }

        foreach (var table in mappedA.Where(x => x.Class is null && !handledA.Contains(x)))
        {
            var matches = FindTableMatches(table, mappedB).Where(x => !handledB.Contains(x)).ToList();
            if (matches.Count == 1 && SameLineage(a, b) && !snapshotA.ClassReadFailed && !snapshotB.ClassReadFailed)
            {
                handledB.Add(matches[0]);
                try
                {
                    output.AddRange(await CompareTableObjectsAsync(a, b, table, matches[0], cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    output.Add(Error($"Объекты · {TableName(table)}", ex));
                }
                continue;
            }

            output.Add(new CompareDifference($"Объекты · {TableName(table)}", null, null, CompareStatus.NotComparable,
                Issue: "Таблица не сопоставлена с известным классом."));
        }
        foreach (var table in mappedB.Where(x => x.Class is null && !handledB.Contains(x)))
            output.Add(new CompareDifference($"Объекты · {TableName(table)}", null, null, CompareStatus.NotComparable,
                Issue: "Таблица не сопоставлена с известным классом."));

    }

    private static List<ClassPair> MatchClasses(
        IReadOnlyList<ClassDefinition> classesA,
        IReadOnlyList<ClassDefinition> classesB)
    {
        var result = new List<ClassPair>();
        var idCountsA = classesA.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var idCountsB = classesB.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var classA in classesA)
        {
            var classB = classesB.FirstOrDefault(x => x.Id.Equals(classA.Id, StringComparison.OrdinalIgnoreCase));
            if (classB is null || idCountsA[classA.Id] != 1 || idCountsB[classA.Id] != 1) continue;
            result.Add(new ClassPair(classA.Id, classA.Name, classA, classB));
        }
        return result;
    }

    private static async Task<IReadOnlyList<CompareDifference>> CompareTableObjectsAsync(
        CompareSource sourceA,
        CompareSource sourceB,
        CompareTable tableA,
        CompareTable tableB,
        CancellationToken cancellationToken)
    {
        var lineageMatches = SameLineage(sourceA, sourceB);
        var schemaIssue = MatchKeyIssue(tableA.Schema, tableB.Schema);
        var recordsA = await ReadTableRecordsAsync(sourceA, tableA.Schema, cancellationToken)
            .ConfigureAwait(false);
        var recordsB = await ReadTableRecordsAsync(sourceB, tableB.Schema, cancellationToken)
            .ConfigureAwait(false);

        if (lineageMatches && sourceA.OfflineProject is not null && sourceB.OfflineProject is not null &&
            SameSnapshotRows(recordsA, recordsB, cancellationToken) &&
            JsonNode.DeepEquals(JsonSerializer.SerializeToNode(tableA.Schema), JsonSerializer.SerializeToNode(tableB.Schema)))
            return [];

        if (!lineageMatches)
        {
            return recordsA.Select(x => ObjectDifference(tableA, tableB, x, null, CompareStatus.Ambiguous,
                    "Не совпадают идентификатор проекта или сохранённое происхождение источника.", identityCertain: false))
                .Concat(recordsB.Select(x => ObjectDifference(tableA, tableB, null, x, CompareStatus.Ambiguous,
                    "Не совпадают идентификатор проекта или сохранённое происхождение источника.", identityCertain: false)))
                .ToArray();
        }

        string label = $"Объекты · {tableA.Class?.Name ?? TableName(tableA)} · {TableName(tableA)}";
        if (schemaIssue is not null)
            return CompareUnkeyedRows(label, recordsA, recordsB, schemaIssue, cancellationToken);

        var keysA = IndexRecords(tableA.Schema, recordsA);
        var keysB = IndexRecords(tableA.Schema, recordsB);
        var results = new List<CompareDifference>();
        if (keysA.Invalid.Count > 0 || keysB.Invalid.Count > 0)
        {
            // An invalid key on one side can belong to a keyed row on the other side.
            return CompareUnkeyedRows(label, recordsA, recordsB,
                "В одной или нескольких записях ключ отсутствует или непригоден для сопоставления.", cancellationToken);
        }

        foreach (var key in keysA.Rows.Keys.Union(keysB.Rows.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            keysA.Rows.TryGetValue(key, out var rowsA);
            keysB.Rows.TryGetValue(key, out var rowsB);
            if (rowsA is null || rowsB is null)
            {
                var rows = rowsA ?? rowsB!;
                var status = rows.Count > 1 ? CompareStatus.Ambiguous :
                    rowsA is null ? CompareStatus.OnlyB : CompareStatus.OnlyA;
                foreach (var row in rows)
                    results.Add(ObjectDifference(tableA, tableB, rowsA is null ? null : row,
                        rowsB is null ? null : row, status,
                        status == CompareStatus.Ambiguous ? "В этом источнике ключ повторяется." : null,
                        identityCertain: status != CompareStatus.Ambiguous));
                continue;
            }

            if (rowsA.Count != 1 || rowsB.Count != 1)
            {
                var keyLabel = $"{label} · {DisplayKey(tableA.Schema, rowsA[0])}";
                results.AddRange(CompareUnkeyedRows(keyLabel, rowsA, rowsB,
                    "Ключ повторяется в одном или обоих источниках.", cancellationToken));
                continue;
            }

            results.Add(CompareRecord(tableA, tableB, rowsA[0], rowsB[0]));
        }
        return results;
    }

    private static IReadOnlyList<CompareDifference> CompareUnkeyedRows(
        string title, IReadOnlyList<ProjectRecord> a, IReadOnlyList<ProjectRecord> b, string issue,
        CancellationToken cancellationToken)
    {
        // Match exact row contents with multiplicity. Snapshot keys and read order are immaterial.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in a)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = RowContents(record);
            counts[signature] = counts.GetValueOrDefault(signature) + 1;
        }

        var matches = 0;
        foreach (var record in b)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = RowContents(record);
            if (counts.GetValueOrDefault(signature) == 0) continue;
            counts[signature]--;
            matches++;
        }

        return matches == a.Count && matches == b.Count
            ? []
            : [new CompareDifference(title, $"Строк без совпадения: {a.Count - matches}",
                $"Строк без совпадения: {b.Count - matches}", CompareStatus.Ambiguous, Issue: issue)];
    }

    private static string RowContents(ProjectRecord record) =>
        JsonSerializer.Serialize(record.Values.OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

    private static string? MatchKeyIssue(ProjectTableSchema a, ProjectTableSchema b)
    {
        if (!a.HasStableKey || !b.HasStableKey || a.KeyColumns.Count == 0 || b.KeyColumns.Count == 0)
            return "В схеме одного или обоих источников нет стабильного ключа.";
        if (!a.KeyColumns.SequenceEqual(b.KeyColumns, StringComparer.OrdinalIgnoreCase))
            return "Названия или порядок столбцов ключа различаются.";
        foreach (var name in a.KeyColumns)
        {
            var columnA = a.Columns.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var columnB = b.Columns.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (columnA is null || columnB is null)
                return "В сохранённой схеме одного или обоих источников отсутствуют столбцы ключа.";
            if (!string.Equals(columnA.DataType, columnB.DataType, StringComparison.OrdinalIgnoreCase))
                return $"Столбец ключа {name} имеет несовместимые типы SQL.";
        }
        return null;
    }

    private static RecordIndex IndexRecords(
        ProjectTableSchema keySchema,
        IReadOnlyList<ProjectRecord> records)
    {
        var result = new Dictionary<string, List<ProjectRecord>>(StringComparer.Ordinal);
        var invalid = new List<ProjectRecord>();
        foreach (var record in records)
        {
            if (keySchema.KeyColumns.Any(column => !TryGetValue(record.Values, column, out var value) || value is null))
            {
                invalid.Add(record);
                continue;
            }

            string encoded;
            try
            {
                encoded = ProjectKeyCodec.Encode(keySchema, keySchema.KeyColumns,
                    column => TryGetValue(record.Values, column, out var value) ? value : null);
            }
            catch (InvalidOperationException)
            {
                invalid.Add(record);
                continue;
            }

            var scope = $"{NormalizeTable(keySchema.TableName)}|{keySchema.KeyColumns.Count}|{string.Join(",", keySchema.KeyColumns)}";
            var scoped = $"{scope}\u001f{encoded}";
            if (!result.TryGetValue(scoped, out var rows)) result[scoped] = rows = new List<ProjectRecord>();
            rows.Add(record);
        }
        return new RecordIndex(result, invalid);
    }

    private static CompareDifference CompareRecord(
        CompareTable tableA,
        CompareTable tableB,
        ProjectRecord recordA,
        ProjectRecord recordB)
    {
        var names = recordA.Values.Select(x => x.Key).Union(recordB.Values.Select(x => x.Key), StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var children = new List<CompareDifference>();
        foreach (var name in names)
        {
            var hasA = TryGetValue(recordA.Values, name, out var valueA);
            var hasB = TryGetValue(recordB.Values, name, out var valueB);
            if (!hasA || !hasB)
            {
                children.Add(new CompareDifference($"Ячейка · {name}", hasA ? ValueText(valueA) : null, hasB ? ValueText(valueB) : null,
                    hasA ? CompareStatus.OnlyA : CompareStatus.OnlyB, Pair: new CompareRecordPair(tableA, tableB, recordA, recordB), Column: name));
                continue;
            }

            var typeA = ColumnType(tableA.Schema, name);
            var typeB = ColumnType(tableB.Schema, name);
            var status = CompareValues.Compare(valueA, valueB, typeA, typeB);
            children.Add(new CompareDifference($"Ячейка · {name}", ValueText(valueA), ValueText(valueB), status,
                Pair: new CompareRecordPair(tableA, tableB, recordA, recordB), Column: name));
        }

        var key = DisplayKey(tableA.Schema, recordA);
        var title = $"{tableA.Class?.Name ?? TableName(tableA)} · {TableName(tableA)} · {key}";
        var issue = tableA.HasUnresolvedDependencies || tableB.HasUnresolvedDependencies
            ? "Не все логические зависимости удалось определить."
            : null;
        return new CompareDifference(title, tableA.Schema.TableName, tableB.Schema.TableName,
            Aggregate(children), children, new CompareRecordPair(tableA, tableB, recordA, recordB), Issue: issue);
    }

    private static async Task<IReadOnlyList<CompareDifference>> ReadUnpairedObjectsAsync(
        CompareSource source,
        CompareTable table,
        bool isA,
        CompareStatus status,
        CancellationToken cancellationToken,
        string? issue = null)
    {
        var rows = await ReadTableRecordsAsync(source, table.Schema, cancellationToken)
            .ConfigureAwait(false);
        var result = new List<CompareDifference>(rows.Count);
        foreach (var row in rows)
            result.Add(ObjectDifference(isA ? table : null, isA ? null : table,
                isA ? row : null, isA ? null : row, status,
                issue ?? (status == CompareStatus.Ambiguous ? "Для класса найдено несколько возможных соответствий." : null),
                identityCertain: status is CompareStatus.OnlyA or CompareStatus.OnlyB));
        return result;
    }

    private static CompareDifference ObjectDifference(
        CompareTable? tableA,
        CompareTable? tableB,
        ProjectRecord? recordA,
        ProjectRecord? recordB,
        CompareStatus status,
        string? issue,
        bool identityCertain = true)
    {
        var record = recordA ?? recordB;
        bool wholeObjectMissing = identityCertain &&
            status is CompareStatus.OnlyA or CompareStatus.OnlyB &&
            (recordA is null) != (recordB is null);
        var children = wholeObjectMissing ? null : record?.Values.Select(pair =>
            new CompareDifference($"Ячейка · {pair.Key}",
                recordA is null ? null : ValueText(pair.Value),
                recordB is null ? null : ValueText(pair.Value),
                status,
                Pair: identityCertain ? new CompareRecordPair(tableA, tableB, recordA, recordB) : null,
                Column: pair.Key)).ToArray();
        var table = recordA is null ? tableB ?? tableA : tableA;
        var displayKey = record is null || table is null ? "ключ не определён" : DisplayKey(table.Schema, record);
        return new CompareDifference(
            $"{table?.Class?.Name ?? (table is null ? "Объект" : TableName(table))} · {table?.Schema.TableName} · {displayKey}",
            recordA is null ? null : tableA?.Schema.TableName,
            recordB is null ? null : tableB?.Schema.TableName,
            status,
            children,
            identityCertain ? new CompareRecordPair(tableA, tableB, recordA, recordB) : null,
            Issue: issue);
    }

    private static Task<IReadOnlyList<ProjectRecord>> ReadTableRecordsAsync(
        CompareSource source, ProjectTableSchema table, CancellationToken cancellationToken) =>
        !table.HasStableKey && source.ReadUnkeyedTableAsync is { } readUnkeyed
            ? readUnkeyed(table.TableName, cancellationToken)
            : ReadAllRecordsAsync(source.Workspace, table.TableName, cancellationToken);

    private static async Task<IReadOnlyList<ProjectRecord>> ReadAllRecordsAsync(
        IProjectWorkspace workspace,
        string tableName,
        CancellationToken cancellationToken)
    {
        var initialRevision = workspace.Revision;
        var records = new List<ProjectRecord>();
        var pageNumber = 1;
        int? expectedCount = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await workspace.ReadPageAsync(new ProjectReadQuery
            {
                TableName = tableName,
                PageNumber = pageNumber,
                PageSize = PageSize
            }, cancellationToken).ConfigureAwait(false);

            expectedCount ??= page.TotalCount;
            if (page.TotalCount != expectedCount || page.PageNumber != pageNumber || page.PageSize != PageSize)
                throw new InvalidDataException($"Paged read for {tableName} returned inconsistent totals or page metadata.");
            records.AddRange(page.Items);
            if (records.Count > expectedCount)
                throw new InvalidDataException($"Paged read for {tableName} returned more rows than its total count.");
            if (records.Count == expectedCount) break;
            if (page.Items.Count == 0)
                throw new InvalidDataException($"Paged read for {tableName} ended before all rows were read.");
            pageNumber++;
        }

        if (workspace.Revision != initialRevision)
            throw new InvalidOperationException($"{tableName} changed while its records were being read.");
        return records;
    }

    private static bool SameLineage(CompareSource a, CompareSource b)
    {
        if (!string.IsNullOrWhiteSpace(a.ProjectId) && !string.IsNullOrWhiteSpace(b.ProjectId) &&
            string.Equals(a.ProjectId.Trim(), b.ProjectId.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(a.Origin) && !string.IsNullOrWhiteSpace(b.Origin) &&
            string.Equals(a.Origin.Trim(), b.Origin.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ColumnType(ProjectTableSchema schema, string name) =>
        schema.Columns.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.DataType;

    private static string ValueText(JsonNode? value) => value?.ToJsonString() ?? "NULL";

    private static string DisplayKey(ProjectTableSchema schema, ProjectRecord record) =>
        string.Join(", ", schema.KeyColumns.Select(name =>
            TryGetValue(record.Values, name, out var value) ? $"{name}={ValueText(value)}" : $"{name}=<отсутствует>"));

    private static bool TryGetValue(JsonObject values, string name, out JsonNode? value)
    {
        if (values.TryGetPropertyValue(name, out value)) return true;
        var actual = values.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        value = actual.Value;
        return actual.Key is not null;
    }

    private static CompareDifference ValueDifference(string title, string? a, string? b)
    {
        var status = string.Equals(a, b, StringComparison.Ordinal) ? CompareStatus.Equal : CompareStatus.Changed;
        return new CompareDifference(title, a, b, status);
    }

    private static string YesNo(bool value) => value ? "Да" : "Нет";

    private static CompareDifference Error(string title, Exception exception) =>
        new(title, null, null, CompareStatus.Error, Issue: "Не удалось прочитать данные источника.");

    private static CompareStatus Aggregate(IEnumerable<CompareDifference> differences)
    {
        var statuses = differences.Select(x => x.Status).ToArray();
        if (statuses.Length == 0 || statuses.All(x => x == CompareStatus.Equal)) return CompareStatus.Equal;
        if (statuses.Contains(CompareStatus.Error)) return CompareStatus.Error;
        if (statuses.Contains(CompareStatus.Ambiguous)) return CompareStatus.Ambiguous;
        if (statuses.Contains(CompareStatus.NotComparable)) return CompareStatus.NotComparable;
        if (statuses.Contains(CompareStatus.Changed)) return CompareStatus.Changed;
        if (statuses.Contains(CompareStatus.OnlyA) || statuses.Contains(CompareStatus.OnlyB)) return CompareStatus.Changed;
        return CompareStatus.Equal;
    }

    private static string TableName(CompareTable table) => table.Schema.TableName;

    private static string NormalizeTable(string? value) =>
        string.Join(".", (value ?? string.Empty).Replace("[", "").Replace("]", "")
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()))
            .ToLowerInvariant();

    private static bool SameTable(string a, string b)
    {
        var normalizedA = NormalizeTable(a);
        var normalizedB = NormalizeTable(b);
        if (normalizedA == normalizedB) return true;
        if (a.Contains('.') && b.Contains('.')) return false;
        return normalizedA.Split('.').LastOrDefault() == normalizedB.Split('.').LastOrDefault();
    }

    private static DatabaseTable? FindPhysicalTable(DatabaseSchema? schema, string tableName)
    {
        if (schema is null) return null;
        var normalized = NormalizeTable(tableName);
        var exact = schema.Tables.FirstOrDefault(x => NormalizeTable(x.FullName) == normalized);
        if (exact is not null || normalized.Contains('.')) return exact;
        var leaf = normalized.Split('.').LastOrDefault();
        var matches = schema.Tables.Where(x => NormalizeTable(x.FullName).Split('.').LastOrDefault() == leaf).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static ProjectTableSchema? FindSchema(IReadOnlyList<ProjectTableSchema> schemas, string name)
    {
        var exact = schemas.FirstOrDefault(x => NormalizeTable(x.TableName) == NormalizeTable(name));
        if (exact is not null) return exact;
        if (NormalizeTable(name).Contains('.')) return null;
        var leaf = NormalizeTable(name).Split('.').LastOrDefault();
        var matches = schemas.Where(x => NormalizeTable(x.TableName).Split('.').LastOrDefault() == leaf).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private sealed record SourceSnapshot(
        CompareSource Source,
        IReadOnlyList<ProjectTableSchema> Schemas,
        IReadOnlyList<ClassDefinition> Classes,
        JsonObject? GraphRelations,
        string? MetadataFingerprint,
        IReadOnlyList<CompareDifference> Issues,
        bool TableReadFailed,
        bool ClassReadFailed,
        bool RelationReadFailed);

    private sealed record ClassPair(string Id, string Name, ClassDefinition A, ClassDefinition B);

    private sealed record RecordIndex(Dictionary<string, List<ProjectRecord>> Rows, List<ProjectRecord> Invalid);

    private sealed record RelationMetadata(string Value, bool Resolved, bool IsManual, string Title);

    private sealed record MetadataFingerprintDocument(
        IReadOnlyList<ClassDefinition> Classes,
        JsonObject GraphRelations);
}
