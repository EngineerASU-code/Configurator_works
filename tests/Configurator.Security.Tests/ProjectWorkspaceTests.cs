using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using System.Text.Json.Nodes;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProjectWorkspaceTests
{
    [Fact]
    public async Task OfflineWorkspaceFiltersSearchesSortsAndPagesWithoutChangingSource()
    {
        var project = new ConfiguratorProject();
        project.TableSchemas.Add(new ProjectTableSchema
        {
            TableName = "dbo.Devices", KeyColumns = new() { "Record" }, HasStableKey = true,
            Columns = new() { new ProjectColumnSchema { Name = "Record" }, new ProjectColumnSchema { Name = "Name" }, new ProjectColumnSchema { Name = "Area" } }
        });
        project.LocalRecords.AddRange(new[]
        {
            new ProjectRecord { TableName = "dbo.Devices", Key = "1", Values = new JsonObject { ["Record"] = 1, ["Name"] = "Valve B", ["Area"] = "A" } },
            new ProjectRecord { TableName = "dbo.Devices", Key = "2", Values = new JsonObject { ["Record"] = 2, ["Name"] = "Valve A", ["Area"] = "A" } },
            new ProjectRecord { TableName = "dbo.Devices", Key = "3", Values = new JsonObject { ["Record"] = 3, ["Name"] = "Pump", ["Area"] = "B" } }
        });
        var source = new OfflineProjectDataSource(project);
        var workspace = new ProjectWorkspace(source);

        var page = await workspace.ReadPageAsync(new ProjectReadQuery
        {
            TableName = "dbo.Devices", PageNumber = 1, PageSize = 1,
            Filters = new Dictionary<string, object?> { ["Area"] = "A" },
            SearchText = "Valve", SortColumn = "Name"
        });

        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal("Valve A", page.Items[0].Values["Name"]?.ToString());
        Assert.Equal(2, await workspace.CountAsync(new ProjectReadQuery { TableName = "dbo.Devices", Filters = new Dictionary<string, object?> { ["Area"] = "A" } }));
        Assert.Equal(3, project.LocalRecords.Count);
    }

    [Fact]
    public async Task WorkspaceDelegatesPagingToProviderInsteadOfMaterializingWholeTable()
    {
        var source = new PagedOnlySource();
        using var workspace = new ProjectWorkspace(source);
        var page = await workspace.ReadPageAsync(new ProjectReadQuery { TableName = "dbo.Remote", PageNumber = 2, PageSize = 25 });
        Assert.Equal(1, source.ReadCalls);
        Assert.Equal(25, page.Items.Count);
        Assert.Equal(1000, page.TotalCount);
        Assert.Equal(1000, await workspace.CountAsync(new ProjectReadQuery { TableName = "dbo.Remote" }));
    }

    [Fact]
    public async Task DisposingWorkspaceCancelsAnInFlightPagedRead()
    {
        var source = new BlockingPagedSource();
        var workspace = new ProjectWorkspace(source);
        var pending = workspace.ReadPageAsync(new ProjectReadQuery { TableName = "dbo.Remote" });
        await source.Started.Task;
        workspace.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task OfflineBatchCreateRollsBackAllRowsWhenOneRecordFails()
    {
        var project = new ConfiguratorProject();
        project.TableSchemas.Add(new ProjectTableSchema
        {
            TableName = "dbo.Items", KeyColumns = new() { "Id" }, HasStableKey = true,
            Columns = { new ProjectColumnSchema { Name = "Id", DataType = "int" }, new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" } }
        });
        project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.Items", Key = "Id=1", Values = new JsonObject { ["Id"] = 1, ["Name"] = "existing" } });
        var source = new OfflineProjectDataSource(project);
        using var context = new ProjectDataSourceContext();
        context.Set(source);
        var workspace = context.CurrentWorkspace!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.CreateRecordsAsync("dbo.Items",
            new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?> { ["Id"] = 2, ["Name"] = "first" },
                new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "duplicate" }
            }));

        Assert.Single(project.LocalRecords);
        Assert.Equal("existing", project.LocalRecords[0].Values["Name"]?.ToString());
    }

    [Fact]
    public async Task ReadOnlyWorkspaceRejectsMutations()
    {
        var project = new ConfiguratorProject();
        var source = new ReadOnlyProjectDataSource(new OfflineProjectDataSource(project));
        var workspace = new ProjectWorkspace(source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.CreateRecordAsync("dbo.X", new Dictionary<string, object?> { ["Name"] = "x" }));
    }

    private sealed class BlockingPagedSource : IProjectDataSource, IProjectPagedDataSource
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DisplayName => "Blocking fake";
        public bool IsReadOnly => true;
        public IReadOnlyList<ProjectTableSchema> GetTables() => Array.Empty<ProjectTableSchema>();
        public IReadOnlyList<ProjectRecord> GetRecords(string tableName) => Array.Empty<ProjectRecord>();
        public ProjectTableSchema? GetSchema(string tableName) => null;
        public async Task<ProjectRecordPage> ReadPageAsync(ProjectReadQuery query, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ProjectRecordPage();
        }
        public Task<int> CountAsync(ProjectReadQuery query, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class PagedOnlySource : IProjectDataSource, IProjectPagedDataSource
    {
        public int ReadCalls { get; private set; }
        public string DisplayName => "Remote fake";
        public bool IsReadOnly => true;
        public IReadOnlyList<ProjectTableSchema> GetTables() => throw new InvalidOperationException("Must use async provider metadata.");
        public IReadOnlyList<ProjectRecord> GetRecords(string tableName) => throw new InvalidOperationException("Full-table materialization is forbidden.");
        public ProjectTableSchema? GetSchema(string tableName) => null;
        public Task<ProjectRecordPage> ReadPageAsync(ProjectReadQuery query, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(new ProjectRecordPage
            {
                Items = Enumerable.Range(0, query.PageSize).Select(i => new ProjectRecord
                {
                    TableName = query.TableName, Key = (i + (query.PageNumber - 1) * query.PageSize).ToString(),
                    Values = new JsonObject { ["Id"] = i + (query.PageNumber - 1) * query.PageSize }
                }).ToList(),
                PageNumber = query.PageNumber, PageSize = query.PageSize, TotalCount = 1000
            });
        }
        public Task<int> CountAsync(ProjectReadQuery query, CancellationToken cancellationToken = default) => Task.FromResult(1000);
    }

    private sealed class ReadOnlyProjectDataSource : IProjectDataSource
    {
        private readonly IProjectDataSource _inner;
        public ReadOnlyProjectDataSource(IProjectDataSource inner) => _inner = inner;
        public string DisplayName => _inner.DisplayName;
        public bool IsReadOnly => true;
        public IReadOnlyList<ProjectTableSchema> GetTables() => _inner.GetTables();
        public IReadOnlyList<ProjectRecord> GetRecords(string tableName) => _inner.GetRecords(tableName);
        public ProjectTableSchema? GetSchema(string tableName) => _inner.GetSchema(tableName);
    }
}
