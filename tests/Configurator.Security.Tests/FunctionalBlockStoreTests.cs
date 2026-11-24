using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class FunctionalBlockStoreTests
{
    [Fact]
    public void FailedSaveRestoresExistingBlockAndDoesNotLeakIntoLaterSave()
    {
        var block = new FunctionalBlockDefinition { Name = "old", Description = "old description", InterfaceText = "old interface", SourceText = "old source" };
        var blocks = new List<FunctionalBlockDefinition> { block };
        Assert.False(FunctionalBlockEditorControl.SaveWithRollback(blocks, block, false, "new", "changed", "changed", "changed", () => false));
        Assert.Equal("old", block.Name);
        Assert.Equal("old description", block.Description);
        Assert.Equal("old interface", block.InterfaceText);
        Assert.Equal("old source", block.SourceText);
        Assert.Same(block, Assert.Single(blocks));
    }

    [Fact]
    public void FailedNewBlockSaveRestoresCollectionEvenOnException()
    {
        var existing = new FunctionalBlockDefinition { Name = "existing" };
        var added = new FunctionalBlockDefinition();
        var blocks = new List<FunctionalBlockDefinition> { existing };
        Assert.Throws<IOException>(() => FunctionalBlockEditorControl.SaveWithRollback(blocks, added, true, "new", "", "", "", () => throw new IOException()));
        Assert.Same(existing, Assert.Single(blocks));
        Assert.Equal("", added.Name);
    }

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
