using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;

namespace Configurator.Infrastructure.Projects;

/// <summary>Stores lookup rules only inside the owning .cfgproj architecture payload.</summary>
public static class LookupGenerationSettingsStore
{
    private const long MaxImportBytes = 1024 * 1024;
    private const int MaxRules = 1000;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static LookupGenerationSettings Load(ConfiguratorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            if (project.Architecture?["lookupGeneration"] is JsonObject node)
            {
                var loaded = node.Deserialize<LookupGenerationSettings>(Options);
                if (loaded != null)
                {
                    Normalize(loaded);
                    // Preserve legacy automatic generation for rules not yet customized.
                    var defaults = LookupGenerationDefaults.Create();
                    foreach (var rule in defaults.Rules)
                        if (!loaded.Rules.Any(x => SameRule(x, rule))) loaded.Rules.Add(rule);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // A malformed optional node must not break opening a project, but the fallback to defaults
            // must be visible in the diagnostic log instead of silent.
            Configurator.SessionTestLog.Trace("LOOKUP-RULES", "lookupGeneration не прочитан, применены значения по умолчанию: " + ex.Message);
        }
        return LookupGenerationDefaults.Create();
    }

    public static void Save(ConfiguratorProject project, LookupGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        project.Architecture ??= new JsonObject();
        project.Architecture["lookupGeneration"] = JsonSerializer.SerializeToNode(settings, Options);
        project.Manifest.ModifiedUtc = DateTimeOffset.UtcNow;
    }

    public static LookupGenerationSettings Import(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxImportBytes)
            throw new InvalidDataException($"Файл настроек слишком большой ({info.Length / 1024} КБ). Максимум {MaxImportBytes / 1024} КБ.");
        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<LookupGenerationSettings>(json, Options)
            ?? throw new InvalidDataException("Файл настроек справочников пуст или имеет неверный формат.");
        Normalize(settings);
        Validate(settings);
        return settings;
    }

    public static void Export(string path, LookupGenerationSettings settings)
    {
        Validate(settings);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }

    public static void Validate(LookupGenerationSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException($"Неподдерживаемая версия настроек справочников: {settings.SchemaVersion}.");
        if (settings.Rules == null) throw new InvalidDataException("В настройках отсутствует список правил.");
        if (settings.Rules.Count > MaxRules) throw new InvalidDataException($"Слишком много правил: {settings.Rules.Count} (максимум {MaxRules}).");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in settings.Rules)
        {
            if (rule == null) throw new InvalidDataException("Список правил содержит пустую запись.");
            if (!string.IsNullOrWhiteSpace(rule.Id) && !ids.Add(rule.Id))
                throw new InvalidDataException($"Повторяющийся идентификатор правила: {rule.Id}.");
            CheckIdentifier(rule.SourceTable, "исходная таблица", rule, allowSchema: true);
            CheckIdentifier(rule.SourceField, "исходное поле", rule, allowSchema: false);
            CheckIdentifier(rule.LookupTable, "таблица справочника", rule, allowSchema: true);
            CheckIdentifier(rule.KeyField, "ключевое поле", rule, allowSchema: false);
            CheckIdentifier(rule.DisplayField, "отображаемое поле", rule, allowSchema: false);
            if (string.IsNullOrWhiteSpace(rule.ClassName) || string.IsNullOrWhiteSpace(rule.SourceField))
                throw new InvalidDataException("Для каждого правила обязательны класс и исходное поле.");
            if (rule.Enabled && (string.IsNullOrWhiteSpace(rule.LookupTable) || string.IsNullOrWhiteSpace(rule.KeyField)))
                throw new InvalidDataException($"Для активного правила «{rule.ClassName}.{rule.SourceField}» обязательны таблица справочника и поле ключа.");
        }
        var duplicates = settings.Rules.Where(x => x.Enabled)
            .GroupBy(x => $"{x.ClassName}|{x.SourceTable}|{x.SourceField}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicates != null) throw new InvalidDataException($"Обнаружены повторяющиеся активные правила: {duplicates.Key}.");
    }

    private static bool SameRule(LookupGenerationRule a, LookupGenerationRule b) =>
        a.ClassName.Equals(b.ClassName, StringComparison.OrdinalIgnoreCase) &&
        a.SourceField.Equals(b.SourceField, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(a.SourceTable) || string.IsNullOrWhiteSpace(b.SourceTable) || a.SourceTable.Equals(b.SourceTable, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces JSON nulls with empty values and guarantees unique, non-empty rule ids.</summary>
    private static void Normalize(LookupGenerationSettings settings)
    {
        settings.Rules ??= new();
        settings.Rules.RemoveAll(r => r == null);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in settings.Rules)
        {
            rule.ClassName ??= string.Empty;
            rule.SourceTable ??= string.Empty;
            rule.SourceField ??= string.Empty;
            rule.LookupTable ??= string.Empty;
            rule.KeyField ??= string.Empty;
            rule.DisplayField ??= string.Empty;
            if (string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N");
                ids.Add(rule.Id);
            }
        }
    }

    // Names are always compared against the real schema before use, so this is a sanity check
    // (garbage / control characters / absurd length), not a security boundary.
    private static void CheckIdentifier(string value, string what, LookupGenerationRule rule, bool allowSchema)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        string[] parts = value.Split('.');
        bool tooManyParts = parts.Length > (allowSchema ? 2 : 1);
        if (tooManyParts || value.Length > 256 || parts.Any(p => string.IsNullOrWhiteSpace(p)) || value.Any(char.IsControl))
            throw new InvalidDataException($"Правило «{rule.ClassName}.{rule.SourceField}»: некорректное значение поля «{what}»: {value}.");
    }
}
