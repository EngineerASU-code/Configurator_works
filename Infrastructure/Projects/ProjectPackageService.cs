using System.IO;
#nullable enable
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;

namespace Configurator.Infrastructure.Projects;

/// <summary>Reads and writes portable .cfgproj packages. Opening a package never connects to a database.</summary>
public sealed class ProjectPackageService
{
    private const int CurrentFormatVersion = 3;
    private const long MaxEntryBytes = 128L * 1024 * 1024;
    private const long MaxPackageExpandedBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Test seam for deterministic failure-injection. Production leaves this unset.
    internal Action<string>? SaveCheckpoint { get; set; }
    internal sealed class SimulatedProcessCrashException : Exception
    {
        public SimulatedProcessCrashException(string checkpoint) : base($"Simulated process termination at {checkpoint}.") { }
    }

    public Task SaveAsync(string path, ConfiguratorProject project, CancellationToken cancellationToken = default, bool requireExisting = false, FileStream? reservedFile = null) =>
        SaveCoreAsync(path, project, cancellationToken, requireExisting, reservedFile);

    internal async Task<string> SaveForCompareAsync(string path, ConfiguratorProject project, FileStream reservedFile, CancellationToken cancellationToken) =>
        (await SaveCoreAsync(path, project, cancellationToken, requireExisting: true, reservedFile).ConfigureAwait(false))!;

    private async Task<string?> SaveCoreAsync(string path, ConfiguratorProject project, CancellationToken cancellationToken, bool requireExisting, FileStream? reservedFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);
        // Save an independent model snapshot synchronously before the first asynchronous file operation.
        // Callers that edit the live model can continue without racing JSON serialization.
        project = CloneProject(project);
        ValidateProject(project);

        var fullPath = Path.GetFullPath(path);
        RecoverInterruptedReservedSave(fullPath);
        if (requireExisting && !File.Exists(fullPath))
            throw new FileNotFoundException("Файл проекта не найден. Сохранение не будет создавать новый файл вместо удалённого.", fullPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Не удалось определить каталог проекта.");
        Directory.CreateDirectory(directory);
        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");

        project.Manifest.Format = "ConfiguratorProject";
        project.Manifest.FormatVersion = CurrentFormatVersion;
        project.Manifest.ConfiguratorVersion = string.Empty;
        project.Manifest.ModifiedUtc = DateTimeOffset.UtcNow;
        project.Settings.DisplayName = string.IsNullOrWhiteSpace(project.Settings.DisplayName)
            ? project.Manifest.Name : project.Settings.DisplayName;

        try
        {
            await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteJsonAsync(archive, "manifest.json", project.Manifest, cancellationToken);
                await WriteJsonAsync(archive, "project/settings.json", project.Settings, cancellationToken);
                await WriteJsonAsync(archive, "project/mappings.json", project.Mappings, cancellationToken);
                await WriteJsonAsync(archive, "project/architecture.json", project.Architecture, cancellationToken);
                await WriteJsonAsync(archive, "project/graph-relations.json", project.GraphRelations, cancellationToken);
                await WriteJsonAsync(archive, "project/filters.json", project.Filters, cancellationToken);
                await WriteJsonAsync(archive, "project/text-lists.json", project.TextLists, cancellationToken);
                await WriteJsonAsync(archive, "project/theme-presets.json", project.ThemePresets, cancellationToken);
                await WriteJsonAsync(archive, "project/users.json", project.Users, cancellationToken);
                await WriteJsonAsync(archive, "data/local-snapshot.json", project.LocalRecords, cancellationToken);
                await WriteJsonAsync(archive, "data/baseline.json", project.BaselineRecords, cancellationToken);
                await WriteJsonAsync(archive, "data/table-schemas.json", project.TableSchemas, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }

            SaveCheckpoint?.Invoke("after-temp-archive");
            cancellationToken.ThrowIfCancellationRequested();
            if (requireExisting && !File.Exists(fullPath))
                throw new FileNotFoundException("Файл проекта был удалён во время сохранения. Новый файл не создан.", fullPath);

            // When a persistent handle prevents rename/replace, use a deterministic recovery
            // journal. The previous valid package remains on disk until its backup is flushed
            // and validated; a marker is durable before the target is truncated.
            if (reservedFile is not null)
            {
                if (!reservedFile.CanWrite) throw new IOException("Нет доступа на запись в зарезервированный файл проекта.");
                var rollbackPath = GetRecoveryBackupPath(fullPath);
                var markerPath = GetRecoveryMarkerPath(fullPath);
                bool preserveRecovery = false;
                string candidateHash;
                try
                {
                    if (!File.Exists(fullPath))
                        throw new FileNotFoundException("Файл проекта был удалён во время сохранения. Новый файл не создан.", fullPath);

                    TryDelete(rollbackPath);
                    reservedFile.Flush();
                    reservedFile.Position = 0;
                    await using (var backup = new FileStream(rollbackPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await reservedFile.CopyToAsync(backup, 81920, cancellationToken);
                        await backup.FlushAsync(cancellationToken);
                        backup.Flush(flushToDisk: true);
                    }
                    if (!IsValidPackage(rollbackPath))
                        throw new InvalidDataException("Не удалось подтвердить целостность резервной копии Offline-проекта.");
                    SaveCheckpoint?.Invoke("after-backup");

                    string backupHash = ComputeSha256(rollbackPath);
                    candidateHash = ComputeSha256(tempPath);
                    using (var marker = new FileStream(markerPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(marker, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true))
                    {
                        writer.WriteLine("ConfiguratorOfflineSaveJournalV1");
                        writer.WriteLine("backupSha256=" + backupHash);
                        writer.WriteLine("candidateSha256=" + candidateHash);
                        writer.Flush();
                        marker.Flush(flushToDisk: true);
                    }
                    // Re-read the durable journal and authenticate the exact backup before touching the target.
                    var persistedJournal = ReadAndValidateJournal(markerPath);
                    if (!string.Equals(persistedJournal["backupSha256"], backupHash, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(persistedJournal["candidateSha256"], candidateHash, StringComparison.OrdinalIgnoreCase) ||
                        !ComputeSha256(rollbackPath).Equals(backupHash, StringComparison.OrdinalIgnoreCase) ||
                        !IsValidPackage(rollbackPath))
                        throw new InvalidDataException("Журнал или резервная копия Offline-проекта не прошли проверку перед записью; исходный файл не изменён.");
                    SaveCheckpoint?.Invoke("after-journal");

                    try
                    {
                        await using var source = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                        reservedFile.Position = 0;
                        reservedFile.SetLength(0);
                        SaveCheckpoint?.Invoke("after-truncate");
                        await source.CopyToAsync(reservedFile, 81920, cancellationToken);
                        await reservedFile.FlushAsync(cancellationToken);
                        reservedFile.Flush(flushToDisk: true);
                        SaveCheckpoint?.Invoke("after-write");

                        if (!IsValidPackage(fullPath))
                            throw new InvalidDataException("Сохранённый Offline-пакет не прошёл проверку целостности.");
                        string writtenHash = ComputeSha256(fullPath);
                        if (!writtenHash.Equals(candidateHash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("SHA-256 сохранённого Offline-пакета не совпадает с candidateSha256; операция не зафиксирована.");
                        SaveCheckpoint?.Invoke("after-candidate-hash");
                        SaveCheckpoint?.Invoke("before-commit");
                        // Removing the journal is the commit point. Do not roll back a committed target
                        // merely because best-effort cleanup of the now-orphaned backup fails.
                        if (File.Exists(markerPath)) File.Delete(markerPath);
                        TryDelete(rollbackPath);
                    }
                    catch (SimulatedProcessCrashException)
                    {
                        // Deliberately leave marker + backup + current target as a process crash would.
                        preserveRecovery = true;
                        throw;
                    }
                    catch (Exception writeError)
                    {
                        try
                        {
                            await RestoreFromBackupAsync(rollbackPath, fullPath, reservedFile, backupHash);
                            TryDelete(markerPath);
                        }
                        catch (Exception rollbackError)
                        {
                            preserveRecovery = true;
                            throw new IOException($"Не удалось сохранить Offline-проект и восстановить исходный файл. Журнал и резервная копия сохранены: {rollbackPath}", new AggregateException(writeError, rollbackError));
                        }
                        throw;
                    }
                }
                finally
                {
                    if (!preserveRecovery && !File.Exists(markerPath))
                        TryDelete(rollbackPath);
                }
                return candidateHash;
            }

            if (File.Exists(fullPath))
            {
                var backupPath = fullPath + ".replace-backup-" + Guid.NewGuid().ToString("N");
                TryDelete(backupPath);
                try
                {
                    File.Replace(tempPath, fullPath, backupPath, ignoreMetadataErrors: true);
                    TryDelete(backupPath);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, fullPath, overwrite: true);
                }
            }
            else
            {
                if (requireExisting)
                    throw new FileNotFoundException("Файл проекта был удалён во время сохранения. Новый файл не создан.", fullPath);
                File.Move(tempPath, fullPath);
            }
            return null;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public async Task<ConfiguratorProject> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        RecoverInterruptedReservedSave(fullPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Файл проекта не найден.", fullPath);

        await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await ReadPackageAsync(file, cancellationToken);
    }

    /// <summary>Reads a validated package without recovering or modifying it.</summary>
    public async Task<(ConfiguratorProject Project, string Fingerprint, bool HasWriter)> OpenForCompareAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        try
        {
            await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var (project, fingerprint) = await ReadForCompareAsync(file, cancellationToken);
            return (project, fingerprint, false);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            // The normal Offline workspace may retain a write handle. Compare the last
            // saved package only when both fingerprints agree and no save is in progress.
            await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
            var (project, fingerprint) = await ReadForCompareAsync(file, cancellationToken);
            file.Position = 0;
            var current = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (current != fingerprint || File.Exists(GetRecoveryMarkerPath(fullPath)))
                throw new IOException("Offline-проект сохраняется. Повторите сравнение после завершения сохранения.");
            return (project, fingerprint, true);
        }
    }

    internal static async Task<(ConfiguratorProject Project, string Fingerprint)> ReadForCompareAsync(
        FileStream file, CancellationToken cancellationToken)
    {
        var fingerprint = await ReadFingerprintForCompareAsync(file, cancellationToken);
        file.Position = 0;
        return (await ReadPackageAsync(file, cancellationToken), fingerprint);
    }

    internal static async Task<string> ReadFingerprintForCompareAsync(FileStream file, CancellationToken cancellationToken)
    {
        if (File.Exists(GetRecoveryMarkerPath(file.Name)))
            throw new InvalidDataException("Снимок содержит незавершённое сохранение. Для восстановления откройте его в Offline-режиме.");

        file.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
    }

    private static async Task<ConfiguratorProject> ReadPackageAsync(Stream file, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        ValidateArchive(archive);

        var manifest = await ReadJsonAsync<ConfiguratorProjectManifest>(archive, "manifest.json", cancellationToken)
            ?? throw new InvalidDataException("В проекте отсутствует manifest.json.");
        if (!string.Equals(manifest.Format, "ConfiguratorProject", StringComparison.Ordinal))
            throw new InvalidDataException("Файл не является проектом Configurator.");
        if (manifest.FormatVersion < 1 || manifest.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException($"Версия формата проекта {manifest.FormatVersion} не поддерживается этой версией Configurator.");
        // Format v2/v3 define these sections as required. Older packages remain readable;
        // project users were added in v3 and are optional when importing legacy packages.
        if (manifest.FormatVersion >= 2)
        {
            string[] requiredEntries = {
                "project/settings.json", "project/mappings.json", "project/architecture.json",
                "project/graph-relations.json", "project/filters.json", "project/text-lists.json",
                "data/local-snapshot.json", "data/baseline.json", "data/table-schemas.json"
            };
            if (manifest.FormatVersion >= 3)
                requiredEntries = requiredEntries.Append("project/users.json").ToArray();
            foreach (var required in requiredEntries)
            {
                if (archive.GetEntry(required) is null)
                    throw new InvalidDataException($"В пакете версии {manifest.FormatVersion} отсутствует обязательный раздел '{required}'.");
                var sectionNode = await ReadJsonAsync<JsonNode>(archive, required, cancellationToken);
                if (sectionNode is null)
                    throw new InvalidDataException($"Обязательный раздел '{required}' содержит JSON null вместо данных.");
            }
        }

        var project = new ConfiguratorProject
        {
            Manifest = manifest,
            Settings = await ReadJsonAsync<ProjectSettings>(archive, "project/settings.json", cancellationToken) ?? new(),
            Mappings = await ReadJsonAsync<List<ProjectMapping>>(archive, "project/mappings.json", cancellationToken) ?? new(),
            Architecture = await ReadJsonAsync<JsonObject>(archive, "project/architecture.json", cancellationToken) ?? new(),
            GraphRelations = await ReadJsonAsync<JsonObject>(archive, "project/graph-relations.json", cancellationToken) ?? new(),
            Filters = await ReadJsonAsync<JsonObject>(archive, "project/filters.json", cancellationToken) ?? new(),
            TextLists = await ReadJsonAsync<List<ProjectTextList>>(archive, "project/text-lists.json", cancellationToken) ?? new(),
            ThemePresets = await ReadJsonAsync<List<Configurator.Themes.ThemePreset>>(archive, "project/theme-presets.json", cancellationToken) ?? new(),
            Users = await ReadJsonAsync<List<ProjectUserAccount>>(archive, "project/users.json", cancellationToken) ?? new(),
            LocalRecords = await ReadJsonAsync<List<ProjectRecord>>(archive, "data/local-snapshot.json", cancellationToken) ?? new(),
            BaselineRecords = await ReadJsonAsync<List<ProjectRecord>>(archive, "data/baseline.json", cancellationToken) ?? new(),
            TableSchemas = await ReadJsonAsync<List<ProjectTableSchema>>(archive, "data/table-schemas.json", cancellationToken) ?? new()
        };
        ValidateProject(project);
        // Project opening is intentionally offline: no database adapter is called here.
        return project;
    }

    internal static string GetRecoveryBackupPath(string fullPath) => Path.GetFullPath(fullPath) + ".recovery-backup";
    internal static string GetRecoveryMarkerPath(string fullPath) => Path.GetFullPath(fullPath) + ".save-in-progress";

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Dictionary<string, string> ReadAndValidateJournal(string markerPath)
    {
        string[] lines;
        try { lines = File.ReadAllLines(markerPath); }
        catch (Exception ex) { throw new InvalidDataException($"Не удалось прочитать журнал восстановления '{markerPath}'. Все файлы сохранены для ручного восстановления.", ex); }

        if (lines.Length != 3 || !string.Equals(lines[0], "ConfiguratorOfflineSaveJournalV1", StringComparison.Ordinal))
            throw new InvalidDataException($"Журнал восстановления '{markerPath}' пуст, неполон или имеет неизвестный формат. Целевой файл и резервная копия сохранены.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in lines.Skip(1))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || !values.TryAdd(line[..separator], line[(separator + 1)..].Trim()))
                throw new InvalidDataException($"Журнал восстановления '{markerPath}' содержит некорректные или повторяющиеся поля. Все файлы сохранены.");
        }
        foreach (string key in new[] { "backupSha256", "candidateSha256" })
        {
            if (!values.TryGetValue(key, out string? hash) || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException($"Журнал восстановления '{markerPath}' не содержит корректный {key}. Автоматическое восстановление запрещено; файлы сохранены.");
        }
        return values;
    }

    internal static void RecoverInterruptedReservedSave(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        var markerPath = GetRecoveryMarkerPath(fullPath);
        if (!File.Exists(markerPath)) return;

        var backupPath = GetRecoveryBackupPath(fullPath);
        var journal = ReadAndValidateJournal(markerPath);
        string expectedBackupHash = journal["backupSha256"];
        string expectedCandidateHash = journal["candidateSha256"];
        bool targetExists = File.Exists(fullPath);
        bool backupExists = File.Exists(backupPath);
        bool targetValid = IsValidPackage(fullPath);
        bool backupValid = IsValidPackage(backupPath);
        string targetHash = targetExists ? ComputeSha256(fullPath) : string.Empty;
        string actualBackupHash = backupExists ? ComputeSha256(backupPath) : string.Empty;
        bool backupHashMatches = backupExists && actualBackupHash.Equals(expectedBackupHash, StringComparison.OrdinalIgnoreCase);

        // A backup that doesn't match the journal is untrusted even if it is a valid ZIP.
        // Never clean up the journal or replace a target when the recorded snapshot cannot be authenticated.
        if (backupExists && !backupHashMatches)
            throw new InvalidDataException($"SHA-256 резервной копии '{backupPath}' не совпадает с backupSha256 из журнала '{markerPath}'. Автоматическое восстановление запрещено; все артефакты сохранены.");
        if (!backupExists)
            throw new InvalidDataException($"Отсутствует обязательная резервная копия '{backupPath}' для журнала '{markerPath}'. Целевой файл не изменён.");
        if (!backupValid)
            throw new InvalidDataException($"Резервная копия '{backupPath}' совпадает с журналом по хешу, но не проходит проверку пакета. Все артефакты сохранены.");

        bool targetIsBackup = targetValid && targetHash.Equals(expectedBackupHash, StringComparison.OrdinalIgnoreCase);
        bool targetIsCandidate = targetValid && targetHash.Equals(expectedCandidateHash, StringComparison.OrdinalIgnoreCase);
        if (targetExists && targetValid && !targetIsBackup && !targetIsCandidate)
            throw new InvalidDataException($"Целевой файл '{fullPath}' не совпадает ни с исходной версией, ни с подготовленным архивом из журнала. Автоматическое восстановление остановлено; сохранены целевой файл, '{backupPath}' и '{markerPath}'.");

        // The original snapshot is already present: the interrupted save never changed the committed version.
        if (targetIsBackup)
        {
            if (File.Exists(markerPath)) File.Delete(markerPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
            return;
        }

        // A candidate matching candidateSha256 is not proof of commit: the process may have crashed
        // after writing it but before the commit point. While the journal exists, restore the last
        // confirmed snapshot rather than promoting an uncommitted candidate.
        // Missing, damaged, or uncommitted-candidate targets may only be recovered from the
        // authenticated original snapshot.
        if (!targetValid || targetIsCandidate)
        {
            var recoveryTemp = fullPath + ".recovery-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(backupPath, recoveryTemp, overwrite: false);
                if (!ComputeSha256(recoveryTemp).Equals(expectedBackupHash, StringComparison.OrdinalIgnoreCase) || !IsValidPackage(recoveryTemp))
                    throw new InvalidDataException($"Временная копия восстановления '{recoveryTemp}' не прошла проверку SHA-256/пакета. Исходные артефакты сохранены.");
                if (File.Exists(fullPath))
                {
                    try { File.Replace(recoveryTemp, fullPath, null, ignoreMetadataErrors: true); }
                    catch (PlatformNotSupportedException) { throw new IOException("Платформа не поддерживает атомарную замену файла; целевой файл и артефакты восстановления сохранены."); }
                }
                else File.Move(recoveryTemp, fullPath);
                if (!IsValidPackage(fullPath) || !ComputeSha256(fullPath).Equals(expectedBackupHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Восстановленный файл '{fullPath}' не совпал с подтверждённой резервной копией. Журнал и резервная копия сохранены.");
                if (File.Exists(markerPath)) File.Delete(markerPath);
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            catch
            {
                // On failure, retain the recovery temp as an additional diagnostic artifact.
                throw;
            }
        }
    }

    private async Task RestoreFromBackupAsync(string backupPath, string fullPath, FileStream reservedFile, string expectedBackupHash)
    {
        if (!File.Exists(backupPath) || !ComputeSha256(backupPath).Equals(expectedBackupHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 резервной копии Offline-проекта не совпадает с backupSha256; автоматическое восстановление остановлено.");
        if (!IsValidPackage(backupPath))
            throw new InvalidDataException("Резервная копия Offline-проекта повреждена; автоматическое восстановление остановлено.");
        SaveCheckpoint?.Invoke("before-restore");
        reservedFile.Position = 0;
        reservedFile.SetLength(0);
        SaveCheckpoint?.Invoke("during-restore-after-truncate");
        await using var backup = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await backup.CopyToAsync(reservedFile, 81920, CancellationToken.None);
        await reservedFile.FlushAsync(CancellationToken.None);
        SaveCheckpoint?.Invoke("after-restore-write");
        reservedFile.Flush(flushToDisk: true);
        if (!ComputeSha256(fullPath).Equals(expectedBackupHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 восстановленного Offline-проекта не совпадает с backupSha256; журнал и резервная копия должны быть сохранены.");
        if (!IsValidPackage(fullPath))
            throw new InvalidDataException("Восстановленный Offline-проект не прошёл проверку целостности.");
    }

    private static bool IsValidPackage(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            ValidateArchive(archive);
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry is null) return false;
            using var manifestStream = OpenLimited(manifestEntry);
            using var reader = new StreamReader(manifestStream);
            var manifest = JsonSerializer.Deserialize<ConfiguratorProjectManifest>(reader.ReadToEnd());
            if (manifest is null || manifest.Format != "ConfiguratorProject" || manifest.FormatVersion < 1 || manifest.FormatVersion > CurrentFormatVersion) return false;
            if (manifest.FormatVersion >= 2)
            {
                string[] requiredEntries = {
                    "project/settings.json", "project/mappings.json", "project/architecture.json",
                    "project/graph-relations.json", "project/filters.json", "project/text-lists.json",
                    "data/local-snapshot.json", "data/baseline.json", "data/table-schemas.json"
                };
                if (manifest.FormatVersion >= 3)
                    requiredEntries = requiredEntries.Append("project/users.json").ToArray();
                if (requiredEntries.Any(required => archive.GetEntry(required) is null)) return false;
            }
            foreach (var entry in archive.Entries)
            {
                using var stream = OpenLimited(entry);
                if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var document = JsonDocument.Parse(stream); // validate JSON as well as ZIP decompression/CRC
                    if (document.RootElement.ValueKind == JsonValueKind.Null) return false;
                }
                else stream.CopyTo(Stream.Null); // validate decompression/CRC for non-JSON extras
            }
            return true;
        }
        catch { return false; }
    }

    private static void ValidateProject(ConfiguratorProject project)
    {
        if (project.Manifest is null) throw new InvalidDataException("Отсутствуют метаданные проекта.");
        if (!string.Equals(project.Manifest.Format, "ConfiguratorProject", StringComparison.Ordinal))
            throw new InvalidDataException("Поле Manifest.Format должно быть 'ConfiguratorProject'.");
        if (project.Manifest.FormatVersion < 1 || project.Manifest.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException($"Версия формата проекта {project.Manifest.FormatVersion} не поддерживается.");
        if (string.IsNullOrWhiteSpace(project.Manifest.ProjectId))
            throw new InvalidDataException("Не задан идентификатор проекта.");
        if (project.Manifest.CreatedUtc == default || project.Manifest.ModifiedUtc == default)
            throw new InvalidDataException("В метаданных отсутствует дата создания или изменения проекта.");
        if (project.Settings is null || project.Mappings is null || project.Architecture is null ||
            project.GraphRelations is null || project.Filters is null || project.TextLists is null || project.ThemePresets is null || project.Users is null ||
            project.LocalRecords is null || project.BaselineRecords is null || project.TableSchemas is null)
            throw new InvalidDataException("Проект содержит отсутствующий обязательный раздел или раздел с null вместо структуры данных.");

        static string CanonicalTable(string name) => string.Join(".", name.Split('.').Select(part => part.Trim().Trim('[', ']')));
        static bool ValidIdentifierPath(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Split('.').Length <= 2 &&
            name.Split('.').All(part => !string.IsNullOrWhiteSpace(part.Trim().Trim('[', ']')) &&
                !part.Contains('/') && !part.Contains('\\'));

        var mappingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in project.Mappings)
        {
            if (mapping is null) throw new InvalidDataException("Список сопоставлений содержит пустую запись.");
            if (!ValidIdentifierPath(mapping.TableName) || string.IsNullOrWhiteSpace(mapping.KeyColumn))
                throw new InvalidDataException("В сопоставлении должны быть корректное имя таблицы и ключевое поле.");
            if (string.IsNullOrWhiteSpace(mapping.EntityName))
                throw new InvalidDataException($"Для таблицы '{mapping.TableName}' не задано имя сущности.");
            if (!mappingKeys.Add(mapping.EntityName.Trim()))
                throw new InvalidDataException($"Сущность '{mapping.EntityName}' сопоставлена более одного раза.");
            var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in mapping.FieldMappings ?? new Dictionary<string, string>())
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || !fieldNames.Add(pair.Key.Trim()))
                    throw new InvalidDataException($"Сопоставление полей сущности '{mapping.EntityName}' содержит пустые или повторяющиеся имена.");
            if (mapping.KeyColumns is not null && mapping.KeyColumns.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"Составной ключ сущности '{mapping.EntityName}' содержит пустое имя поля.");
            if (mapping.KeyColumns is not null && mapping.KeyColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.KeyColumns.Count)
                throw new InvalidDataException($"Составной ключ сущности '{mapping.EntityName}' содержит повторяющиеся поля.");
        }

        var schemas = new Dictionary<string, ProjectTableSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in project.TableSchemas)
        {
            if (schema is null || !ValidIdentifierPath(schema.TableName))
                throw new InvalidDataException("Метаданные схемы содержат пустое или некорректное имя таблицы.");
            var tableKey = CanonicalTable(schema.TableName);
            if (!schemas.TryAdd(tableKey, schema))
                throw new InvalidDataException($"Для таблицы '{schema.TableName}' определено несколько схем.");
            if (schema.Columns is null || schema.KeyColumns is null)
                throw new InvalidDataException($"Для таблицы '{schema.TableName}' отсутствует список колонок или ключевых полей.");
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in schema.Columns)
            {
                if (column is null || string.IsNullOrWhiteSpace(column.Name) || string.IsNullOrWhiteSpace(column.DataType))
                    throw new InvalidDataException($"Схема таблицы '{schema.TableName}' содержит колонку без имени или типа данных.");
                if (!columns.Add(column.Name.Trim()))
                    throw new InvalidDataException($"Схема таблицы '{schema.TableName}' содержит повторяющуюся колонку '{column.Name}'.");
            }
            if (schema.KeyColumns.Any(string.IsNullOrWhiteSpace) ||
                schema.KeyColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != schema.KeyColumns.Count)
                throw new InvalidDataException($"Список ключевых полей таблицы '{schema.TableName}' содержит пустые или повторяющиеся имена.");
            foreach (var keyColumn in schema.KeyColumns)
                if (!columns.Contains(keyColumn))
                    throw new InvalidDataException($"Ключевое поле '{keyColumn}' отсутствует в схеме таблицы '{schema.TableName}'.");
            if (schema.HasStableKey && schema.KeyColumns.Count == 0)
                throw new InvalidDataException($"Таблица '{schema.TableName}' помечена как имеющая стабильный ключ, но ключевые поля не заданы.");
        }

        foreach (var mapping in project.Mappings)
        {
            var tableKey = CanonicalTable(mapping.TableName);
            if (!schemas.TryGetValue(tableKey, out var mappedSchema))
            {
                // Uncaptured tables are valid for partial projects; validate only when
                // the package itself contains metadata for the mapped table.
                continue;
            }
            var schemaColumns = new HashSet<string>(mappedSchema.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            if (!schemaColumns.Contains(mapping.KeyColumn))
                throw new InvalidDataException($"Ключевое поле '{mapping.KeyColumn}' сопоставления '{mapping.EntityName}' отсутствует в схеме '{mapping.TableName}'.");
            foreach (var mappedColumn in (mapping.FieldMappings ?? new Dictionary<string, string>()).Values)
                if (!schemaColumns.Contains(mappedColumn))
                    throw new InvalidDataException($"Поле '{mappedColumn}' сопоставления '{mapping.EntityName}' отсутствует в схеме '{mapping.TableName}'.");
        }

        void ValidateRecords(List<ProjectRecord> records, string snapshotName)
        {
            var keys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (record is null || string.IsNullOrWhiteSpace(record.TableName) ||
                    string.IsNullOrWhiteSpace(record.Key) || record.Values is null)
                    throw new InvalidDataException($"В разделе '{snapshotName}' найдена запись без таблицы, ключа или значений.");
                if (!ValidIdentifierPath(record.TableName))
                    throw new InvalidDataException($"В разделе '{snapshotName}' запись содержит некорректное имя таблицы '{record.TableName}'.");
                var table = CanonicalTable(record.TableName);
                if (!keys.TryGetValue(table, out var tableKeys))
                    keys.Add(table, tableKeys = new HashSet<string>(StringComparer.Ordinal));
                if (!tableKeys.Add(record.Key))
                    throw new InvalidDataException($"В разделе '{snapshotName}' повторяется ключ '{record.Key}' таблицы '{record.TableName}'.");
                var valueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in record.Values)
                    if (string.IsNullOrWhiteSpace(pair.Key) || !valueNames.Add(pair.Key.Trim()))
                        throw new InvalidDataException($"Запись '{record.Key}' таблицы '{record.TableName}' содержит пустое или повторяющееся имя поля.");
            }
        }
        ValidateRecords(project.LocalRecords, "data/local-snapshot.json");
        ValidateRecords(project.BaselineRecords, "data/baseline.json");

        foreach (var list in project.TextLists)
        {
            if (list is null || string.IsNullOrWhiteSpace(list.Name))
                throw new InvalidDataException("Список текстовых значений не имеет имени.");
            if (list.Items is null) throw new InvalidDataException($"Список '{list.Name}' не содержит коллекцию элементов.");
            var itemKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.Items)
                if (item is null || string.IsNullOrWhiteSpace(item.Key) || !itemKeys.Add(item.Key))
                    throw new InvalidDataException($"Список '{list.Name}' содержит пустой или повторяющийся ключ элемента.");
        }

        foreach (var theme in project.ThemePresets)
            if (theme is null || string.IsNullOrWhiteSpace(theme.Name))
                throw new InvalidDataException("Пресет темы проекта не имеет имени.");

        var userNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in project.Users)
        {
            if (user is null || string.IsNullOrWhiteSpace(user.UserName) || string.IsNullOrWhiteSpace(user.RoleName))
                throw new InvalidDataException("Список пользователей проекта содержит запись без логина или роли.");
            string userName = user.UserName.Trim();
            if (!user.UserName.Equals(userName, StringComparison.Ordinal) || userName.Length > 128 || userName.Any(char.IsControl))
                throw new InvalidDataException("Логин пользователя проекта должен быть без пробелов по краям, не длиннее 128 символов и без управляющих символов.");
            if (!userNames.Add(userName))
                throw new InvalidDataException($"В проекте повторяется логин пользователя '{userName}'.");
            if (userName.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
                userName.Equals("guest", StringComparison.OrdinalIgnoreCase) ||
                userName.Equals("Инженер-ПНР", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Логин '{userName}' зарезервирован для встроенной учётной записи.");
            if (!new[] { "Проектировщик", "Инженер ПНР", "Гость" }.Contains(user.RoleName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Проектный пользователь '{userName}' ссылается на недопустимую роль '{user.RoleName}'.");
            if (user.RoleName.Equals("Администратор", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Проектная учётная запись не может получить роль Администратора.");
            if (user.PasswordHashVersion != 1 || user.PasswordIterations != 600_000)
                throw new InvalidDataException($"Для пользователя '{userName}' заданы неподдерживаемые параметры хеширования пароля.");
            try
            {
                byte[] hash = Convert.FromBase64String(user.PasswordHash ?? string.Empty);
                byte[] salt = Convert.FromBase64String(user.PasswordSalt ?? string.Empty);
                if (hash.Length != 32 || salt.Length < 16 || salt.Length > 64)
                    throw new FormatException();
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"Для пользователя '{userName}' заданы некорректные данные пароля.", ex);
            }
        }

        // Local and baseline snapshots intentionally may differ (pending local edits,
        // additions and deletions). Their keys are validated independently, not forced equal.
        // Table mappings may refer to online tables not included in a partial snapshot.
        // Connection strings and credentials are deliberately not modeled in this format.
    }

    private static ConfiguratorProject CloneProject(ConfiguratorProject source)
    {
        // JSON round-trip creates a deep, detached snapshot without changing the on-disk schema.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, JsonOptions);
        return JsonSerializer.Deserialize<ConfiguratorProject>(bytes)
            ?? throw new InvalidDataException("Не удалось создать согласованный снимок локального проекта.");
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Split('/').Any(part => part == "..") || Path.IsPathRooted(name))
                throw new InvalidDataException("Архив проекта содержит недопустимый путь.");
            if (!names.Add(name)) throw new InvalidDataException("Архив проекта содержит повторяющиеся имена файлов.");
            if (entry.Length > MaxEntryBytes) throw new InvalidDataException("Файл внутри проекта превышает допустимый размер.");
            total = checked(total + entry.Length);
            if (total > MaxPackageExpandedBytes) throw new InvalidDataException("Размер распакованного проекта превышает допустимый предел.");
        }
    }

    private static async Task WriteJsonAsync<T>(ZipArchive archive, string entryName, T value, CancellationToken token)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
    }

    private static async Task<T?> ReadJsonAsync<T>(ZipArchive archive, string entryName, CancellationToken token)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null) return default;
        await using var stream = OpenLimited(entry);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token);
    }

    /// <summary>
    /// ZipArchiveEntry.Length comes from the archive header and can be wrong or forged, so the real number
    /// of decompressed bytes is limited while reading.
    /// </summary>
    private static Stream OpenLimited(ZipArchiveEntry entry) => new LimitedReadStream(entry.Open(), MaxEntryBytes);

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public LimitedReadStream(Stream inner, long limit) { _inner = inner; _limit = limit; }

        private int Count(int n)
        {
            _read += n;
            if (_read > _limit) throw new InvalidDataException("Файл внутри проекта превышает допустимый размер.");
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await _inner.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* Best-effort cleanup only. */ }
    }
}
