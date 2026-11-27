using Configurator.Core.Projects;

namespace Configurator;

internal static class MsTypeCatalog
{
    internal static bool IsField(string name) => name.Equals("MS_Type", StringComparison.OrdinalIgnoreCase);

    internal static async Task<List<LookupOption>> ReadAsync(IProjectWorkspace workspace, string table, string classNumber)
    {
        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var schema = (await workspace.GetTablesAsync(default).ConfigureAwait(false))
            .First(t => t.TableName.Equals(table, StringComparison.OrdinalIgnoreCase));
        var classColumn = ProjectClassCatalog.FindClassColumn(schema.Columns.Select(c => c.Name));
        if (classColumn != null && string.IsNullOrWhiteSpace(classNumber)) return new();
        for (int number = 1; ; number++)
        {
            var page = await workspace.ReadPageAsync(new ProjectReadQuery { TableName = table, PageNumber = number, PageSize = 1000 }, default).ConfigureAwait(false);
            foreach (var row in page.Items)
                if ((classColumn == null || ProjectClassCatalog.Read(row, classColumn) == classNumber)
                    && ProjectClassCatalog.Read(row, "MS_Type") is string value)
                    values.Add(value);
            if (number >= page.TotalPages || page.Items.Count == 0) break;
        }
        return values.Select(v => new LookupOption { Key = v, Display = v }).ToList();
    }
}
