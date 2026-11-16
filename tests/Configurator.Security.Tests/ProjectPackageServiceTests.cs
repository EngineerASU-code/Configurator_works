using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProjectPackageServiceTests
{
    [Fact]
    public async Task SavedPackageCanBeReopenedWithoutChangingItsFormat()
    {
        var path = NewPath();
        try
        {
            var service = new ProjectPackageService();
            await service.SaveAsync(path, CreateProject());
            var reopened = await service.OpenAsync(path);
            Assert.Equal("Test project", reopened.Manifest.Name);
            Assert.Equal(3, reopened.Manifest.FormatVersion);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RejectsCorruptedNonZipPackage()
    {
        var path = NewPath();
        try
        {
            await File.WriteAllTextAsync(path, "not a zip archive");
            await Assert.ThrowsAnyAsync<Exception>(() => new ProjectPackageService().OpenAsync(path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RejectsPackageWithMissingRequiredV2Section()
    {
        var path = NewPath();
        var broken = NewPath();
        try
        {
            await new ProjectPackageService().SaveAsync(path, CreateProject());
            using (var input = ZipFile.OpenRead(path))
            using (var output = ZipFile.Open(broken, ZipArchiveMode.Create))
            {
                foreach (var entry in input.Entries.Where(e => e.FullName != "data/baseline.json"))
                {
                    var copy = output.CreateEntry(entry.FullName);
                    using var source = entry.Open();
                    using var target = copy.Open();
                    source.CopyTo(target);
                }
            }
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectPackageService().OpenAsync(broken));
            Assert.Contains("data/baseline.json", ex.Message);
        }
        finally { Delete(path); Delete(broken); }
    }

    [Fact]
    public async Task RejectsDuplicateRecordKeysWithinOneTable()
    {
        var path = NewPath();
        try
        {
            var project = CreateProject();
            project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Id"] = 1 } });
            project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Id"] = 2 } });
            await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectPackageService().SaveAsync(path, project));
            Assert.False(File.Exists(path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RejectsUnsupportedFormatVersion()
    {
        var path = NewPath();
        try
        {
            var project = CreateProject();
            project.Manifest.FormatVersion = 999;
            await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectPackageService().SaveAsync(path, project));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task SaveSerializesDetachedSnapshotRatherThanLaterModelMutation()
    {
        var path = NewPath();
        try
        {
            var service = new ProjectPackageService();
            var project = CreateProject();
            project.Settings.DisplayName = "Before";
            var saveTask = service.SaveAsync(path, project);
            project.Settings.DisplayName = "After";
            await saveTask;
            var reopened = await service.OpenAsync(path);
            Assert.Equal("Before", reopened.Settings.DisplayName);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task CancelledSaveDoesNotReplaceExistingPackage()
    {
        var path = NewPath();
        try
        {
            var service = new ProjectPackageService();
            await service.SaveAsync(path, CreateProject());
            var original = await File.ReadAllBytesAsync(path);
            var project = CreateProject();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(path, project, cts.Token, requireExisting: true));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { Delete(path); }
    }




    [Fact]
    public async Task ExistingTargetDeletedDuringSaveIsNotRecreated()
    {
        var path = NewPath();
        try
        {
            await new ProjectPackageService().SaveAsync(path, CreateProject());
            var service = new ProjectPackageService
            {
                SaveCheckpoint = point =>
                {
                    if (point == "after-temp-archive") File.Delete(path);
                }
            };
            await Assert.ThrowsAsync<FileNotFoundException>(() => service.SaveAsync(path, CreateProject(), requireExisting: true));
            Assert.False(File.Exists(path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReservedHandleSaveCommitsValidPackageAndCleansJournal()
    {
        var path = NewPath();
        try
        {
            var service = new ProjectPackageService();
            await service.SaveAsync(path, CreateProject());
            await using (var reserved = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 81920, FileOptions.Asynchronous))
            {
                var changed = CreateProject();
                changed.Settings.DisplayName = "Committed update";
                await service.SaveAsync(path, changed, requireExisting: true, reservedFile: reserved);
            }
            var reopened = await service.OpenAsync(path);
            Assert.Equal("Committed update", reopened.Settings.DisplayName);
            Assert.False(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
            Assert.False(File.Exists(ProjectPackageService.GetRecoveryBackupPath(path)));
        }
        finally
        {
            Delete(path);
            Delete(ProjectPackageService.GetRecoveryMarkerPath(path));
            Delete(ProjectPackageService.GetRecoveryBackupPath(path));
        }
    }

    [Theory]
    [InlineData("after-journal")]
    [InlineData("after-truncate")]
    [InlineData("after-write")]
    [InlineData("before-commit")]
    public async Task InterruptedReservedSaveRecoversLastConfirmedPackage(string checkpoint)
    {
        var path = NewPath();
        FileStream reserved = null;
        try
        {
            var initialService = new ProjectPackageService();
            var initial = CreateProject();
            initial.Settings.DisplayName = "Confirmed before save";
            await initialService.SaveAsync(path, initial);

            reserved = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 81920, FileOptions.Asynchronous);
            var failingService = new ProjectPackageService
            {
                SaveCheckpoint = point =>
                {
                    if (point == checkpoint) throw new ProjectPackageService.SimulatedProcessCrashException(point);
                }
            };
            var changed = CreateProject();
            changed.Settings.DisplayName = "Unconfirmed candidate";
            await Assert.ThrowsAsync<ProjectPackageService.SimulatedProcessCrashException>(
                () => failingService.SaveAsync(path, changed, requireExisting: true, reservedFile: reserved));
            await reserved.DisposeAsync();
            reserved = null;

            var recovered = await new ProjectPackageService().OpenAsync(path);
            Assert.Equal("Confirmed before save", recovered.Settings.DisplayName);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
            Assert.False(File.Exists(ProjectPackageService.GetRecoveryBackupPath(path)));
        }
        finally
        {
            if (reserved is not null) await reserved.DisposeAsync();
            Delete(path);
            Delete(ProjectPackageService.GetRecoveryMarkerPath(path));
            Delete(ProjectPackageService.GetRecoveryBackupPath(path));
        }
    }


    [Fact]
    public async Task ReservedSaveRejectsTargetHashMismatchAndRestoresConfirmedPackage()
    {
        var path = NewPath();
        var externalPath = NewPath();
        try
        {
            var service = new ProjectPackageService();
            var original = CreateProject();
            original.Settings.DisplayName = "Confirmed hash baseline";
            await service.SaveAsync(path, original);
            var external = CreateProject();
            external.Settings.DisplayName = "Unexpected substituted package";
            await service.SaveAsync(externalPath, external);
            byte[] substitutedBytes = await File.ReadAllBytesAsync(externalPath);

            await using (var reserved = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 81920, FileOptions.Asynchronous))
            {
                var saving = new ProjectPackageService
                {
                    SaveCheckpoint = point =>
                    {
                        if (point == "after-write")
                        {
                            // Mutate through the already-reserved handle. Opening the same path
                            // with File.WriteAllBytes requests an incompatible share mode on Windows.
                            reserved.Position = 0;
                            reserved.SetLength(0);
                            reserved.Write(substitutedBytes, 0, substitutedBytes.Length);
                            reserved.Flush(flushToDisk: true);
                        }
                    }
                };
                var changed = CreateProject();
                changed.Settings.DisplayName = "Intended candidate";
                await Assert.ThrowsAsync<InvalidDataException>(() => saving.SaveAsync(path, changed, requireExisting: true, reservedFile: reserved));
            }

            var reopened = await new ProjectPackageService().OpenAsync(path);
            Assert.Equal("Confirmed hash baseline", reopened.Settings.DisplayName);
            Assert.False(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
            Assert.False(File.Exists(ProjectPackageService.GetRecoveryBackupPath(path)));
        }
        finally
        {
            Delete(path); Delete(externalPath);
            Delete(ProjectPackageService.GetRecoveryMarkerPath(path));
            Delete(ProjectPackageService.GetRecoveryBackupPath(path));
        }
    }

    [Fact]
    public async Task RollbackRejectsBackupHashMismatchAndPreservesRecoveryArtifacts()
    {
        var path = NewPath();
        try
        {
            var original = CreateProject();
            original.Settings.DisplayName = "Confirmed before tamper";
            await new ProjectPackageService().SaveAsync(path, original);
            var backupPath = ProjectPackageService.GetRecoveryBackupPath(path);
            await using var reserved = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 81920, FileOptions.Asynchronous);
            var service = new ProjectPackageService
            {
                SaveCheckpoint = point =>
                {
                    if (point == "after-truncate") throw new IOException("Injected write failure.");
                    if (point == "before-restore") File.WriteAllText(backupPath, "tampered backup");
                }
            };
            var changed = CreateProject();
            changed.Settings.DisplayName = "Unconfirmed candidate";
            var error = await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(path, changed, requireExisting: true, reservedFile: reserved));
            Assert.Contains("восстановить исходный файл", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
            Assert.True(File.Exists(backupPath));
        }
        finally
        {
            Delete(path);
            Delete(ProjectPackageService.GetRecoveryMarkerPath(path));
            Delete(ProjectPackageService.GetRecoveryBackupPath(path));
        }
    }

    [Fact]
    public async Task InterruptedRollbackRetainsJournalAndCanRecoverOnNextOpen()
    {
        var path = NewPath();
        FileStream reserved = null;
        try
        {
            var original = CreateProject();
            original.Settings.DisplayName = "Confirmed original";
            await new ProjectPackageService().SaveAsync(path, original);
            reserved = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 81920, FileOptions.Asynchronous);
            var failingService = new ProjectPackageService
            {
                SaveCheckpoint = point =>
                {
                    if (point == "after-truncate")
                        throw new IOException("Injected write failure.");
                    if (point == "before-restore")
                        throw new ProjectPackageService.SimulatedProcessCrashException(point);
                }
            };
            var changed = CreateProject();
            changed.Settings.DisplayName = "Unconfirmed candidate";
            await Assert.ThrowsAsync<IOException>(() => failingService.SaveAsync(path, changed, requireExisting: true, reservedFile: reserved));
            await reserved.DisposeAsync();
            reserved = null;

            var reopened = await new ProjectPackageService().OpenAsync(path);
            Assert.Equal("Confirmed original", reopened.Settings.DisplayName);
            Assert.False(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
        }
        finally
        {
            if (reserved is not null) await reserved.DisposeAsync();
            Delete(path);
            Delete(ProjectPackageService.GetRecoveryMarkerPath(path));
            Delete(ProjectPackageService.GetRecoveryBackupPath(path));
        }
    }



    [Fact]
    public async Task RecoveryDoesNotOverwriteUnexpectedNewerValidTarget()
    {
        var path = NewPath();
        var candidatePath = NewPath();
        var externalPath = NewPath();
        try
        {
            var service = new ProjectPackageService();
            var original = CreateProject();
            original.Settings.DisplayName = "Confirmed old version";
            await service.SaveAsync(path, original);
            var backupPath = ProjectPackageService.GetRecoveryBackupPath(path);
            File.Copy(path, backupPath);

            var candidate = CreateProject();
            candidate.Settings.DisplayName = "Candidate from interrupted save";
            await service.SaveAsync(candidatePath, candidate);
            var external = CreateProject();
            external.Settings.DisplayName = "Unexpected external version";
            await service.SaveAsync(externalPath, external);
            var candidateHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(candidatePath)));
            var backupHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(backupPath)));
            var externalBytes = await File.ReadAllBytesAsync(externalPath);
            File.Copy(externalPath, path, overwrite: true);
            await File.WriteAllTextAsync(ProjectPackageService.GetRecoveryMarkerPath(path),
                $"ConfiguratorOfflineSaveJournalV1\nbackupSha256={backupHash}\ncandidateSha256={candidateHash}\n");

            await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(path));
            Assert.Equal(externalBytes, await File.ReadAllBytesAsync(path));
            var stillExternal = await service.OpenAsync(externalPath);
            Assert.Equal("Unexpected external version", stillExternal.Settings.DisplayName);
            Assert.True(File.Exists(backupPath));
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
            // The target is intentionally not opened through this service because its journal is unresolved.
        }
        finally
        {
            Delete(path); Delete(candidatePath); Delete(externalPath);
            Delete(ProjectPackageService.GetRecoveryMarkerPath(path));
            Delete(ProjectPackageService.GetRecoveryBackupPath(path));
        }
    }

    [Fact]
    public async Task RecoveryIgnoresOrphanTemporaryPackagesWhenJournalIdentifiesConfirmedBackup()
    {
        var path = NewPath();
        var candidatePath = NewPath();
        string orphanTemp = path + ".tmp-abandoned";
        string orphanBackup = path + ".replace-backup-abandoned";
        try
        {
            var service = new ProjectPackageService();
            var original = CreateProject();
            original.Settings.DisplayName = "Confirmed snapshot";
            await service.SaveAsync(path, original);
            File.Copy(path, ProjectPackageService.GetRecoveryBackupPath(path));
            var candidate = CreateProject();
            candidate.Settings.DisplayName = "Unconfirmed orphan candidate";
            await service.SaveAsync(candidatePath, candidate);
            File.Copy(candidatePath, orphanTemp);
            File.Copy(candidatePath, orphanBackup);
            await File.WriteAllTextAsync(ProjectPackageService.GetRecoveryMarkerPath(path), "ConfiguratorOfflineSaveJournalV1");

            await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(path));
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryBackupPath(path)));
            Assert.True(File.Exists(orphanTemp));
            Assert.True(File.Exists(orphanBackup));
        }
        finally
        {
            Delete(path); Delete(candidatePath); Delete(orphanTemp); Delete(orphanBackup);
            Delete(ProjectPackageService.GetRecoveryMarkerPath(path));
            Delete(ProjectPackageService.GetRecoveryBackupPath(path));
        }
    }

    [Fact]
    public async Task RecoveryRejectsBackupHashMismatchAndPreservesAllArtifacts()
    {
        var path = NewPath();
        try
        {
            var service = new ProjectPackageService();
            await service.SaveAsync(path, CreateProject());
            var backup = ProjectPackageService.GetRecoveryBackupPath(path);
            File.Copy(path, backup);
            await File.WriteAllTextAsync(backup, "tampered but not a valid package");
            await File.WriteAllTextAsync(ProjectPackageService.GetRecoveryMarkerPath(path),
                $"ConfiguratorOfflineSaveJournalV1\nbackupSha256={new string('A', 64)}\ncandidateSha256={new string('B', 64)}\n");
            byte[] targetBefore = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(path));
            Assert.Equal(targetBefore, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(backup));
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
        }
        finally { Delete(path); Delete(ProjectPackageService.GetRecoveryMarkerPath(path)); Delete(ProjectPackageService.GetRecoveryBackupPath(path)); }
    }

    [Fact]
    public async Task RecoveryRejectsEmptyJournalWithoutDeletingArtifacts()
    {
        var path = NewPath();
        try
        {
            var service = new ProjectPackageService();
            await service.SaveAsync(path, CreateProject());
            File.Copy(path, ProjectPackageService.GetRecoveryBackupPath(path));
            await File.WriteAllTextAsync(ProjectPackageService.GetRecoveryMarkerPath(path), string.Empty);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(path));
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryBackupPath(path)));
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
        }
        finally { Delete(path); Delete(ProjectPackageService.GetRecoveryMarkerPath(path)); Delete(ProjectPackageService.GetRecoveryBackupPath(path)); }
    }

    [Fact]
    public async Task RecoveryDoesNotReplaceValidTargetWithCorruptBackup()
    {
        var path = NewPath();
        try
        {
            var service = new ProjectPackageService();
            var project = CreateProject();
            project.Settings.DisplayName = "Keep valid target";
            await service.SaveAsync(path, project);
            await File.WriteAllTextAsync(ProjectPackageService.GetRecoveryBackupPath(path), "corrupt backup");
            await File.WriteAllTextAsync(ProjectPackageService.GetRecoveryMarkerPath(path), "ConfiguratorOfflineSaveJournalV1");

            await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(path));
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryMarkerPath(path)));
            Assert.True(File.Exists(ProjectPackageService.GetRecoveryBackupPath(path))); // retained for diagnosis
        }
        finally
        {
            Delete(path);
            Delete(ProjectPackageService.GetRecoveryMarkerPath(path));
            Delete(ProjectPackageService.GetRecoveryBackupPath(path));
        }
    }

    [Fact]
    public void OfflineDataSourceReadsAndMutatesOnlyItsInMemoryProject()
    {
        var project = CreateProject();
        project.TableSchemas.Add(new ProjectTableSchema
        {
            TableName = "dbo.Items",
            KeyColumns = new() { "Id" },
            HasStableKey = true,
            Columns = new() { new ProjectColumnSchema { Name = "Id", DataType = "int" } }
        });
        var source = new OfflineProjectDataSource(project);
        source.UpsertRecord(new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Id"] = 1 } });
        Assert.Single(source.GetRecords("Items"));
        Assert.False(source.IsReadOnly);
        Assert.Equal("dbo.Items", source.GetTables().Single().TableName);
    }

    private static ConfiguratorProject CreateProject() => new()
    {
        Manifest = new ConfiguratorProjectManifest { Name = "Test project" },
        Settings = new ProjectSettings { DisplayName = "Test project" },
        Mappings = new(),
        Architecture = new JsonObject(),
        GraphRelations = new JsonObject(),
        Filters = new JsonObject(),
        TextLists = new(),
        LocalRecords = new(),
        BaselineRecords = new(),
        TableSchemas = new()
    };

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "Configurator-" + Guid.NewGuid().ToString("N") + ".cfgproj");
    private static void Delete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

