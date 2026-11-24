using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using System.Text.Json.Nodes;

namespace Configurator.Infrastructure.Architecture;

public sealed class TemplateLibraryStore
{
    private readonly string _path;
    private readonly ConfiguratorProject _project;

    public TemplateLibraryStore(string path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Configurator", "class-library.json");
    }

    /// <summary>Project-scoped library. In this mode no profile/AppData files are accessed.</summary>
    public TemplateLibraryStore(ConfiguratorProject project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _path = string.Empty;
    }

    public List<ClassDefinition> Load(bool strict = false)
    {
        try
        {
            if (_project != null)
            {
                var localDefinitions = _project.Architecture["classDefinitions"];
                if (localDefinitions is null) return new List<ClassDefinition>();
                if (localDefinitions is not JsonArray localArray)
                    throw new JsonException("Некорректный раздел определений классов.");
                return localArray.Deserialize<List<ClassDefinition>>() ?? new List<ClassDefinition>();
            }
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<ClassDefinition>>(json)
                ?? throw new JsonException("Библиотека классов содержит NULL вместо списка.");
        }
        catch (Exception ex) when (!strict || ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new List<ClassDefinition>();
        }
    }

    public void Save(IEnumerable<ClassDefinition> classes) =>
        throw new InvalidOperationException(ProjectWriteGuard.ArchitectureReadOnly);

}
