using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class FunctionalBlockStoreTests
{
    [Fact]
    public async Task OnlineBlocksCopyIntoOfflinePackageAndReopenWithContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "Configurator-FB-" + Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "project.cfgproj");
        Directory.CreateDirectory(root);
        try
        {
            var onlineStore = new FunctionalBlockStore(" Server ", "Project", Path.Combine(root, "online"));
            onlineStore.Save(new[]
            {
                new FunctionalBlockDefinition
                {
                    Name = "Motor control", Description = "Start and stop", InterfaceText = "Enable: BOOL",
                    SourceText = "User authored text"
                }
            });

            var reopenedOnlineStore = new FunctionalBlockStore("server", "project", Path.Combine(root, "online"));
            var project = new ConfiguratorProject();
            new FunctionalBlockStore(project).Save(reopenedOnlineStore.Load());
            await new ProjectPackageService().SaveAsync(packagePath, project);

            var reopenedProject = await new ProjectPackageService().OpenAsync(packagePath);
            var blocks = new FunctionalBlockStore(reopenedProject).Load();
            var block = Assert.Single(blocks);
            Assert.Equal("Motor control", block.Name);
            Assert.Equal("Start and stop", block.Description);
            Assert.Equal("Enable: BOOL", block.InterfaceText);
            Assert.Equal("User authored text", block.SourceText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
