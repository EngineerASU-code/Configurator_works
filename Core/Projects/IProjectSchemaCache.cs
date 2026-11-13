#nullable enable
namespace Configurator.Core.Projects;

/// <summary>Allows the workspace to invalidate provider metadata after mutations.</summary>
public interface IProjectSchemaCache
{
    void InvalidateSchemaCache();
}
