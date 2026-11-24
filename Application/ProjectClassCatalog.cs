#nullable enable
using Configurator.Core.Architecture;
using Configurator.Core.Projects;

namespace Configurator;

internal sealed record ProjectClassTable(string Title, IReadOnlyDictionary<string, string>? SharedClasses = null);

internal static class ProjectClassCatalog
{
    internal static async Task<Dictionary<string, ProjectClassTable>> ReadTablesAsync(IProjectWorkspace workspace,
        IReadOnlyList<ProjectTableSchema> tables, CancellationToken ct)
    {
        var definitions = await workspace.GetClassDefinitionsAsync(ct).ConfigureAwait(false);
        var registry = new List<(string Table, string Title, string? Number)>();
        var registryName = GraphRelationDefinition.ResolveTableName(tables.Select(t => t.TableName), "dbo.Classes");
        if (registryName != null)
        {
            for (int page = 1; ; page++)
            {
                var result = await workspace.ReadPageAsync(new ProjectReadQuery
                {
                    TableName = registryName, PageNumber = page, PageSize = 1000
                }, ct).ConfigureAwait(false);
                foreach (var row in result.Items)
                {
                    var table = Read(row, "DBName") ?? Read(row, "TableName");
                    var title = Read(row, "Name") ?? Read(row, "ClassName") ?? Read(row, "Class");
                    if (title != null) registry.Add((table ?? title, title, Read(row, "Record") ?? Read(row, "ClassNumber")));
                }
                if (page >= result.TotalPages || result.Items.Count == 0) break;
            }
        }

        var names = tables.Select(t => t.TableName).ToList();
        var labels = new Dictionary<string, ProjectClassTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var lookup in SystemLookupCatalog.All)
        {
            var table = GraphRelationDefinition.ResolveTableName(names, lookup.TargetTable)
                ?? ResolveLegacyTable(names, lookup.ClassName);
            if (table != null) labels[table] = new(lookup.ClassName);
        }
        var configured = definitions.Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => (Table: GraphRelationDefinition.ResolveTableName(names, d.PrimaryStorageTableName)
                ?? GraphRelationDefinition.ResolveTableName(names, d.Name), Title: d.Name, Number: (string?)d.ClassNumber.ToString()));
        AddMappings(configured);
        AddMappings(registry.Select(r => (GraphRelationDefinition.ResolveTableName(names, r.Table), r.Title, r.Number)));
        return labels;

        void AddMappings(IEnumerable<(string? Table, string Title, string? Number)> mappings)
        {
            foreach (var group in mappings.Where(r => r.Table != null).GroupBy(r => r.Table!, StringComparer.OrdinalIgnoreCase))
            {
                var titles = group.Select(r => r.Title).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                var byNumber = group.Where(r => r.Number != null).GroupBy(r => r.Number!)
                    .Where(g => g.Select(r => r.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                    .ToDictionary(g => g.Key, g => g.First().Title);
                labels[group.Key] = titles.Count == 1 ? new(titles[0]) : new(group.Key, byNumber);
            }
        }
    }

    internal static string? Read(ProjectRecord row, string column) =>
        row.Values.FirstOrDefault(v => v.Key.Equals(column, StringComparison.OrdinalIgnoreCase)).Value?.ToString().Trim() is { Length: > 0 } value
            ? value : null;

    private static string? ResolveLegacyTable(IReadOnlyList<string> names, string className)
    {
        string[] aliases = className switch
        {
            "DSV" => ["dbo.Valves2S"],
            "AI" => ["dbo.AnalogIn"],
            "AO" => ["dbo.AnalogOut"],
            "PID" => ["dbo.Regler"],
            "Program" => ["dbo.Programs", "dbo.Program"],
            "Step" => ["dbo.ProgramStep"],
            "Matrix" => ["dbo.Matrix_List", "dbo.Matrices"],
            _ => [$"dbo.{className}"]
        };
        return aliases.Select(alias => GraphRelationDefinition.ResolveTableName(names, alias)).FirstOrDefault(table => table != null);
    }
}
