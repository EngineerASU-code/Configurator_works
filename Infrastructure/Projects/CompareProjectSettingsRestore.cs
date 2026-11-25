#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Compare;
using Configurator.Core.Projects;

namespace Configurator.Infrastructure.Projects;

/// <summary>Conflict-checked undo for a Compare change to project-scoped lists or themes.</summary>
public static class CompareProjectSettingsRestore
{
    public const string RestoreKind = "CompareProjectSettingsV1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex FingerprintPattern = new("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);

    private sealed class Payload
    {
        public Payload() { }
        public string AfterFingerprint { get; set; } = string.Empty;
        public ProjectCompareSettings? BeforeSettings { get; set; }
    }

    public static ChangeJournalEntry CreateEntry(CompareMetadataTransferPreview preview,
        ProjectCompareSettings before, ProjectCompareSettings after)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        bool offline = preview.Target.Workspace.IsOffline;
        var (server, database) = offline ? (string.Empty, string.Empty) : ParseOrigin(preview.Target.Origin);
        var payload = new Payload
        {
            AfterFingerprint = CompareMetadataTransferPreview.FingerprintProjectSettings(after),
            BeforeSettings = Clone(before)
        };
        return new ChangeJournalEntry
        {
            Category = "Compare",
            Source = preview.Kind.ToString(),
            Description = preview.Description,
            Details = preview.Key,
            RestoreKind = RestoreKind,
            RestorePayloadJson = JsonSerializer.Serialize(payload),
            OperationKind = ChangeJournalOperationKind.ConfigurationChange,
            RestoreIncludesAllChanges = true,
            ProjectType = offline ? "Offline" : "Online",
            ProjectId = offline ? (preview.Target.ProjectId ?? string.Empty) :
                $"online:{ConnectionSettings.ProjectServerName(server).ToLowerInvariant()}/{database.Trim().ToLowerInvariant()}",
            ProjectName = offline ? (preview.Target.OfflineProject?.Manifest?.Name ?? preview.Target.Workspace.DisplayName) : database,
            Server = server,
            Database = database,
            Mode = 2,
            UserRole = UserAuthorizationService.CurrentRole?.Name ?? string.Empty,
            EntityType = preview.Kind.ToString(),
            EntityIdentity = preview.Key,
            Result = "Успешно"
        };
    }

    public static bool CanRestore(ChangeJournalEntry? entry)
    {
        if (entry is null || !string.Equals(entry.RestoreKind, RestoreKind, StringComparison.Ordinal) ||
            entry.RestoredUtc.HasValue || !entry.RestoreIncludesAllChanges ||
            entry.OperationKind != ChangeJournalOperationKind.ConfigurationChange || (entry.Changes?.Count ?? 0) != 0 ||
            !string.Equals(entry.Result, "Успешно", StringComparison.OrdinalIgnoreCase) ||
            (entry.ProjectType != "Online" && entry.ProjectType != "Offline")) return false;
        if (entry.ProjectType == "Online" && (string.IsNullOrWhiteSpace(entry.Server) || string.IsNullOrWhiteSpace(entry.Database))) return false;
        if (entry.ProjectType == "Offline" && string.IsNullOrWhiteSpace(entry.ProjectId)) return false;
        return TryReadPayload(entry, out _);
    }

    internal static (string Server, string Database) ParseOnlineOrigin(string? origin) => ParseOrigin(origin);

    public static void RestoreOffline(ChangeJournalEntry entry, ConfiguratorProject project)
    {
        if (!CanRestore(entry)) throw new InvalidOperationException("Для этой записи Compare восстановление недоступно.");
        ArgumentNullException.ThrowIfNull(project);
        if (!string.Equals(entry.ProjectType, "Offline", StringComparison.Ordinal) ||
            !string.Equals(entry.ProjectId, project.Manifest?.ProjectId, StringComparison.Ordinal))
            throw new InvalidOperationException("Запись журнала относится к другому Offline-проекту.");
        var payload = ReadPayload(entry);
        var current = new ProjectCompareSettings(project.TextLists, project.ThemePresets);
        EnsureExpectedAfterState(current, payload);
        project.TextLists = payload.BeforeSettings!.TextLists.Select(CloneTextList).ToList();
        project.ThemePresets = payload.BeforeSettings.ThemePresets.Select(item => item.Clone()).ToList();
    }

    public static async Task RestoreOnlineAsync(ChangeJournalEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        await RestoreOnlineAsync(entry, new ProjectCompareSettingsStore(entry.Server, entry.Database), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task RestoreOnlineAsync(ChangeJournalEntry entry, ProjectCompareSettingsStore store,
        CancellationToken cancellationToken = default)
    {
        if (!CanRestore(entry)) throw new InvalidOperationException("Для этой записи Compare восстановление недоступно.");
        if (!string.Equals(entry.ProjectType, "Online", StringComparison.Ordinal))
            throw new InvalidOperationException("Запись журнала не относится к Online-проекту.");
        ArgumentNullException.ThrowIfNull(store);

        var payload = ReadPayload(entry);
        var current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        EnsureExpectedAfterState(current, payload);
        var expectedFingerprint = CompareMetadataTransferPreview.FingerprintProjectSettings(current);
        await store.SaveAsync(Clone(payload.BeforeSettings!), expectedFingerprint, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureExpectedAfterState(ProjectCompareSettings current, Payload payload)
    {
        if (!CompareMetadataTransferPreview.FingerprintProjectSettings(current)
                .Equals(payload.AfterFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Проектные настройки изменились после переноса Compare. Повторите сравнение; восстановление отменено.");
    }

    private static Payload ReadPayload(ChangeJournalEntry entry)
    {
        if (!TryReadPayload(entry, out var payload))
            throw new InvalidOperationException("Снимок восстановления Compare повреждён или неполон.");
        return payload!;
    }

    private static bool TryReadPayload(ChangeJournalEntry entry, out Payload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(entry.RestorePayloadJson) || entry.RestorePayloadJson.Length > 8 * 1024 * 1024)
            return false;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(entry.RestorePayloadJson, JsonOptions);
            return payload?.BeforeSettings?.TextLists is { } lists && lists.All(item => item is not null) &&
                payload.BeforeSettings.ThemePresets is { } themes && themes.All(item => item is not null) &&
                FingerprintPattern.IsMatch(payload.AfterFingerprint);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return false; }
    }

    internal static ProjectCompareSettings CloneSettings(ProjectCompareSettings settings) => new(
        settings.TextLists.Select(CloneTextList).ToList(),
        settings.ThemePresets.Select(item => item.Clone()).ToList());

    private static ProjectCompareSettings Clone(ProjectCompareSettings settings) => CloneSettings(settings);

    private static ProjectTextList CloneTextList(ProjectTextList item) => JsonSerializer.Deserialize<ProjectTextList>(JsonSerializer.Serialize(item))
        ?? throw new InvalidOperationException("Не удалось сформировать снимок текстового списка.");

    private static (string Server, string Database) ParseOrigin(string? origin)
    {
        var separator = (origin ?? string.Empty).IndexOf('\0');
        if (separator <= 0 || separator == origin!.Length - 1)
            throw new CompareTransferException("Не удалось определить целевой сервер и базу для журнала Compare.");
        return (origin[..separator], origin[(separator + 1)..]);
    }
}
