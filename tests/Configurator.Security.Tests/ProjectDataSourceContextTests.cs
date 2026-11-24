using System.Text.Json.Nodes;
﻿using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProjectDataSourceContextTests
{
    [Fact]
    public void ContextSwitchesFromOfflineToNoSourceWithoutRetainingOfflineFlag()
    {
        var project = new ConfiguratorProject();
        var context = new ProjectDataSourceContext();

        var offline = new OfflineProjectDataSource(project);
        context.Set(offline);
        Assert.True(context.IsOffline);
        Assert.NotNull(context.CurrentCrud);
        Assert.NotSame(offline, context.CurrentCrud);
        Assert.NotNull(context.Current);
        Assert.NotNull(context.CurrentWorkspace);
        Assert.True(context.CurrentWorkspace.IsOffline);

        context.Set(null);
        Assert.False(context.IsOffline);
        Assert.Null(context.Current);
        Assert.Null(context.CurrentCrud);
        Assert.Null(context.CurrentWorkspace);
    }

    [Fact]
    public async Task OnlineReadAdapterCannotOpenSqlWhenOfflineWorkspaceIsActive()
    {
        var context = new ProjectDataSourceContext();
        context.Set(new OfflineProjectDataSource(new ConfiguratorProject()));
        try
        {
            var online = new OnlineProjectDataSource("Server=127.0.0.1;Database=NeverConnect;Integrated Security=true");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => online.GetTablesAsync());
            Assert.Contains("Offline", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            context.Clear();
        }
    }

    [Fact]
    public async Task OfflineWorkspaceRejectsChangesToClassDefinitionsAndGraphRelations()
    {
        var project = new ConfiguratorProject();
        using var context = new ProjectDataSourceContext();
        context.Set(new OfflineProjectDataSource(project));

        var definition = new Configurator.Core.Architecture.ClassDefinition { Id = "pump", Name = "Pump" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.CurrentWorkspace.SaveClassDefinitionsAsync(new[] { definition }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.CurrentWorkspace.SaveGraphRelationsAsync(new JsonObject()));
        Assert.Empty(await context.CurrentWorkspace.GetClassDefinitionsAsync());
        Assert.Empty(project.GraphRelations);
        Assert.Null(project.Architecture["classDefinitions"]);
    }

    [Fact]
    public void ContextSwitchesToOnlineWithUnifiedReaderAndWriterAdapters()
    {
        using var context = new ProjectDataSourceContext();
        context.SetOnline("Server=127.0.0.1;Database=NeverConnect;Integrated Security=true", "Test DB");
        Assert.False(context.IsOffline);
        Assert.False(SqlProviderGuard.IsOfflineActive);
        Assert.IsType<OnlineProjectDataSource>(context.Current);
        Assert.NotNull(context.CurrentCrud);
        Assert.NotNull(context.CurrentWorkspace);
        Assert.False(context.CurrentWorkspace.IsOffline);
    }

    [Fact]
    public void ReplacingOfflineProjectReplacesTheExactSourceInstance()
    {
        var first = new OfflineProjectDataSource(new ConfiguratorProject());
        var second = new OfflineProjectDataSource(new ConfiguratorProject());
        using var context = new ProjectDataSourceContext();

        context.Set(first);
        Assert.Same(first, context.Current);
        context.Set(second);
        Assert.Same(second, context.Current);
        Assert.NotSame(first, context.Current);
    }
}
