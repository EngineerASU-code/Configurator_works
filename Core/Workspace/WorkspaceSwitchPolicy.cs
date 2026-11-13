namespace Configurator.Core.Workspace;

/// <summary>Pure decisions used by workspace transitions; kept independent of WPF and SQL.</summary>
public static class WorkspaceSwitchPolicy
{
    /// <summary>A workspace switch persists a local package only when local data is dirty.</summary>
    public static bool ShouldPersistOfflineProject(bool projectDirty, bool gridDirty)
        => projectDirty || gridDirty;

    /// <summary>Whether a workspace transition must explicitly bind the active editor to the local project.</summary>
    public static bool ShouldConfigureOfflineEditor(bool isOfflineWorkspace, bool hasProject, int mode)
        => isOfflineWorkspace && hasProject && mode is 2 or 3;

    /// <summary>Keep the current project when it is active; otherwise choose the first available database.</summary>
    public static string ResolveOnlineDatabase(string selectedDatabase, IEnumerable<string> availableDatabases)
    {
        var databases = availableDatabases?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(selectedDatabase))
        {
            // Return the canonical name reported by the available-database source,
            // not the caller's potentially differently-cased value.
            var matchedDatabase = databases.FirstOrDefault(
                x => x.Equals(selectedDatabase, StringComparison.OrdinalIgnoreCase));
            if (matchedDatabase is not null)
                return matchedDatabase;
        }
        return databases.FirstOrDefault() ?? string.Empty;
    }
}
