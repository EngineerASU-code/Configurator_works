#nullable enable
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configurator.Core.Compare;
using Configurator.Core.Projects;

namespace Configurator.Infrastructure.Projects;

/// <summary>Stores non-SQL project comparison assets per Online database identity.</summary>
public sealed class ProjectCompareSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public ProjectCompareSettingsStore(string server, string database)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Configurator", "ProjectCompareSettings");
        var canonicalKey = $"{ConnectionSettings.ProjectServerName(server).ToUpperInvariant()}\0{database.Trim().ToUpperInvariant()}";
        _path = Path.Combine(root, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey)))}.json");
    }

    internal ProjectCompareSettingsStore(string path) => _path = Path.GetFullPath(path);

    public async Task<ProjectCompareSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path)) return new ProjectCompareSettings([], []);
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await JsonSerializer.DeserializeAsync<ProjectCompareSettings>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Файл проектных настроек содержит NULL.");
    }

    public async Task SaveAsync(ProjectCompareSettings settings, string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // A stable lock file serializes all cooperating processes across the read/check/replace.
        // It is deliberately retained: deleting it would allow two independent locks on Windows.
        await using var gate = await AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!CompareMetadataTransferPreview.FingerprintProjectSettings(current).Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new CompareTransferException("Проектные настройки изменились после чтения. Повторите операцию.");
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<FileStream> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
