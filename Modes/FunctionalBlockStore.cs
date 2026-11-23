#nullable enable
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;

namespace Configurator;

/// <summary>Stores generic user-authored block text without claiming a vendor file format.</summary>
internal sealed class FunctionalBlockStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConfiguratorProject? _project;
    private readonly string? _path;

    public FunctionalBlockStore(ConfiguratorProject project) =>
        _project = project ?? throw new ArgumentNullException(nameof(project));

    public FunctionalBlockStore(string server, string database)
        : this(server, database, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Configurator", "FunctionalBlocks"))
    {
    }

    internal FunctionalBlockStore(string server, string database, string storageDirectory)
    {
        var canonicalKey = $"{server.Trim().ToUpperInvariant()}\0{database.Trim().ToUpperInvariant()}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey)));
        _path = Path.Combine(storageDirectory, $"{key}.json");
    }

    public List<FunctionalBlockDefinition> Load()
    {
        JsonArray? blocks;
        if (_project != null)
        {
            blocks = _project.Architecture["functionalBlocks"] as JsonArray;
            if (blocks == null && _project.Architecture.ContainsKey("functionalBlocks"))
                throw new JsonException("Некорректный раздел функциональных блоков проекта.");
        }
        else
        {
            if (_path == null || !File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<List<FunctionalBlockDefinition>>(File.ReadAllText(_path))
                ?? throw new JsonException("Хранилище функциональных блоков содержит NULL вместо списка.");
        }

        return blocks?.Deserialize<List<FunctionalBlockDefinition>>() ?? new();
    }

    public void Save(IReadOnlyList<FunctionalBlockDefinition> blocks)
    {
        var normalized = blocks.ToList();
        if (_project != null)
        {
            _project.Architecture["functionalBlocks"] = JsonSerializer.SerializeToNode(normalized, JsonOptions);
            _project.Manifest.ModifiedUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (_path == null) throw new InvalidOperationException("Не задано хранилище функциональных блоков.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(tempPath, _path, true);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}
