#nullable enable
using System;

namespace Configurator.Core.Projects;

/// <summary>
/// Owns the active project's read/write adapters and their lifetime. Project switches
/// cancel in-flight mutations and reads before a new provider becomes active.
/// </summary>
public sealed class ProjectDataSourceContext : IDisposable
{
    private ProjectCrudSession? _crudSession;

    public IProjectDataSource? Current { get; private set; }
    public IProjectCrudDataSource? CurrentCrud { get; private set; }
    public IProjectWorkspace? CurrentWorkspace { get; private set; }
    public bool IsOffline => Current is OfflineProjectDataSource;

    public void Set(IProjectDataSource? source) => Set(source, source as IProjectCrudDataSource);

    public void Set(IProjectDataSource? readSource, IProjectCrudDataSource? crudSource)
    {
        ReleaseCurrent();
        SqlProviderGuard.SetOfflineActive(readSource is OfflineProjectDataSource);
        Current = readSource;
        _crudSession = crudSource == null ? null : new ProjectCrudSession(crudSource);
        CurrentCrud = _crudSession;
        IProjectArchitectureDataSource? architecture = readSource switch
        {
            OfflineProjectDataSource offline => new Configurator.Infrastructure.Projects.ProjectArchitectureDataSource(offline.Project),
            OnlineProjectDataSource online => new Configurator.Infrastructure.Projects.ProjectArchitectureDataSource(online.Server, online.Database),
            _ => null
        };
        CurrentWorkspace = readSource == null ? null : new ProjectWorkspace(readSource, CurrentCrud, architecture);
    }

    /// <summary>Activates both live SQL adapters as one Online workspace.</summary>
    public void SetOnline(string connectionString, string displayName = "Online", bool readOnly = false)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Для Online-рабочего пространства требуется строка подключения.", nameof(connectionString));
        var reader = new OnlineProjectDataSource(connectionString, displayName);
        IProjectCrudDataSource? writer = readOnly ? null : new OnlineProjectCrudDataSource(connectionString);
        Set(reader, writer);
    }

    public void Clear() => Set((IProjectDataSource?)null);

    private void ReleaseCurrent()
    {
        (CurrentWorkspace as IDisposable)?.Dispose();
        _crudSession?.Dispose();
        _crudSession = null;
        CurrentWorkspace = null;
        CurrentCrud = null;
        Current = null;
    }

    public void Dispose()
    {
        ReleaseCurrent();
        SqlProviderGuard.SetOfflineActive(false);
    }
}
