#nullable enable
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Compare;
using Configurator.Core.Projects;

namespace Configurator.Infrastructure.Projects;

public static class CompareSourceReader
{
    public static async Task<CompareSource> OpenOfflineAsync(
        string path, bool allowWrite, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var (project, fingerprint, hasWriter) = await new ProjectPackageService().OpenForCompareAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        DatabaseSchema? schema = null;
        string? issue = null;
        try
        {
            schema = DatabaseGraphRelationMaterializer.ReadCapturedSchema(project.Architecture["physicalSchema"]);
            if (schema == null)
                issue = "Пакет не содержит подробного снимка SQL-ключей и физических связей.";
        }
        catch (JsonException)
        {
            issue = "Подробный снимок SQL-ключей и физических связей повреждён.";
        }

        var reader = new OfflineProjectDataSource(project);
        bool writable = allowWrite && !hasWriter &&
            (File.GetAttributes(fullPath) & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == 0;
        var workspace = new ProjectWorkspace(reader,
            architecture: new ProjectArchitectureDataSource(project, strictReads: true), allowWrite: writable);
        var origin = Origin(ReadString(project.Architecture, "sourceServer"), ReadString(project.Architecture, "sourceDatabase"));
        return new CompareSource(workspace, project.Manifest.ProjectId, origin, schema, fullPath, fingerprint, writable, issue)
        {
            OfflineProject = project
        };
    }

    public static async Task<CompareSource> OpenOnlineAsync(
        string connectionString, string displayName, bool allowWrite, CancellationToken cancellationToken = default)
    {
        SqlProviderGuard.EnsureSqlAllowed();
        var reader = new OnlineProjectDataSource(connectionString, displayName);
        var workspace = new ProjectWorkspace(reader,
            allowWrite ? new OnlineProjectCrudDataSource(connectionString) : null,
            new ProjectArchitectureDataSource(reader.Server, reader.Database, strictReads: true));
        var settingsStore = new ProjectCompareSettingsStore(reader.Server, reader.Database);

        DatabaseSchema? schema = null;
        string? issue = null;
        try
        {
            schema = await new DatabaseArchitectureAnalyzer(connectionString).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            workspace.Dispose();
            throw;
        }
        catch (Exception)
        {
            issue = "Не удалось прочитать подробные SQL-ключи и связи.";
        }

        return new CompareSource(workspace, null, Origin(reader.Server, reader.Database), schema,
            CanWrite: allowWrite && !workspace.IsReadOnly, MetadataIssue: issue)
        {
            ReadUnkeyedTableAsync = reader.ReadUnkeyedTableAsync,
            ReadProjectSettingsAsync = settingsStore.LoadAsync,
            SaveProjectSettingsAsync = settingsStore.SaveAsync
        };
    }

    private static string? ReadString(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    internal static string? Origin(string? server, string? database) =>
        string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database)
            ? null : ProjectArchitectureResolver.GetProjectIdentity(server, database);
}
