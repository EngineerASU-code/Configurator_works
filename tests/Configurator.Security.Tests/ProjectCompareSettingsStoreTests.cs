using Configurator.Core.Compare;
using Configurator.Infrastructure.Projects;
using Configurator.Themes;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProjectCompareSettingsStoreTests
{
    [Fact]
    public async Task ConcurrentWritersWithSameSnapshotCannotOverwriteEachOther()
    {
        var directory = Path.Combine(Path.GetTempPath(), "compare-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var first = new ProjectCompareSettingsStore(path);
            var second = new ProjectCompareSettingsStore(path);
            var before = await first.LoadAsync();
            var fingerprint = CompareMetadataTransferPreview.FingerprintProjectSettings(before);
            using var start = new ManualResetEventSlim(false);
            async Task<bool> Write(ProjectCompareSettingsStore store, string name)
            {
                await Task.Run(() => start.Wait());
                try
                {
                    await store.SaveAsync(before with { ThemePresets = [new ThemePreset { Name = name }] }, fingerprint);
                    return true;
                }
                catch (CompareTransferException) { return false; }
            }
            var a = Write(first, "A");
            var b = Write(second, "B");
            start.Set();
            var successes = await Task.WhenAll(a, b);
            Assert.Single(successes, x => x);
            Assert.Single((await first.LoadAsync()).ThemePresets);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
