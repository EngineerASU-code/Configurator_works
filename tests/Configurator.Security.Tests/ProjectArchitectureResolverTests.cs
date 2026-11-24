using Xunit;
using System.Threading.Tasks;

namespace Configurator.Security.Tests;

public sealed class ProjectArchitectureResolverTests
{
    [Fact]
    public void LocalSharedMemoryUsesTheExistingProjectsMetadata()
    {
        string local = Environment.MachineName;
        Assert.Equal(ProjectArchitectureResolver.GetProjectIdentity(local, "Project"),
            ProjectArchitectureResolver.GetProjectIdentity("lpc:" + local, "Project"));
    }

    [Fact]
    public void SameDatabaseOnDifferentServersHasDifferentProjectIdentity()
    {
        var first = ProjectArchitectureResolver.GetProjectIdentity("server-a", "Project");
        var second = ProjectArchitectureResolver.GetProjectIdentity("server-b", "Project");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ProjectIdentityIgnoresSurroundingWhitespace()
    {
        Assert.Equal(
            ProjectArchitectureResolver.GetProjectIdentity("server", "Project"),
            ProjectArchitectureResolver.GetProjectIdentity(" server ", " Project "));
    }

    [Fact]
    public async Task LateArchitectureLoadDoesNotApplyAfterSwitchingProjects()
    {
        var projectA = ProjectArchitectureResolver.GetProjectIdentity("server-a", "Project");
        var projectB = ProjectArchitectureResolver.GetProjectIdentity("server-b", "Project");
        var activeProject = projectA;
        var architecture = "A-loading";
        var objectList = "A-loading";
        var selectedClass = "A-class";
        var cache = "A-loading";
        var architectureResolved = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var resolving = ProjectArchitectureResolver.ResolveIfCurrentAsync(
            async () =>
            {
                started.SetResult();
                return await result.Task;
            },
            () => string.Equals(activeProject, projectA, StringComparison.OrdinalIgnoreCase),
            loadedArchitecture =>
            {
                architecture = loadedArchitecture;
                objectList = "A-objects";
                selectedClass = "A-selected";
                cache = projectA;
                architectureResolved = true;
            });

        await started.Task;
        activeProject = projectB;
        architecture = "B-architecture";
        objectList = "B-objects";
        selectedClass = "B-selected";
        cache = projectB;
        architectureResolved = true;
        result.SetResult("A-architecture");

        Assert.False(await resolving);
        Assert.Equal("B-architecture", architecture);
        Assert.Equal("B-objects", objectList);
        Assert.Equal("B-selected", selectedClass);
        Assert.Equal(projectB, cache);
        Assert.True(architectureResolved);
    }
}
