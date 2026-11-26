using Configurator.Infrastructure.Database;
using Xunit;

namespace Configurator.Security.Tests;

public class LocalSqlLibraryTests
{
    [Fact]
    public void OpenWindowDropsUnavailableDatabaseAndKeepsDraft()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new FreeSqlWindow(_ => throw new InvalidOperationException(), new[] { "A", "B" }, "A");
                try
                {
                    window.Show();
                    window.TxtSql.Text = "SELECT 1";
                    window.UpdateLayout();
                    Assert.True(window.TxtSql.ActualHeight > window.ActualHeight / 3);
                    window.CmbDatabase.SelectedItem = "B";
                    Assert.Equal("SELECT 1", window.TxtSql.Text);
                    Assert.True(window.BtnExecute.IsEnabled);
                    window.CmbDatabase.SelectedItem = "A";
                    window.SetDatabaseAvailable("A", false);
                    Assert.DoesNotContain("A", window.CmbDatabase.Items.Cast<string>());
                    Assert.Null(window.CmbDatabase.SelectedItem);
                    Assert.False(window.BtnExecute.IsEnabled);
                    window.SetDatabaseAvailable("A", true);
                    Assert.Contains("A", window.CmbDatabase.Items.Cast<string>());
                    Assert.Equal("SELECT 1", window.TxtSql.Text);
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void CreateReopenUpdateAndRejectStaleEditor()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new LocalSqlLibrary(path);
            Assert.Empty(store.Read());
            var first = store.Save(null, "Замер", "SELECT 1");
            var other = new LocalSqlLibrary(path);
            Assert.Equal(first, Assert.Single(other.Read()));
            var updated = other.Save(first, "Повтор", "SELECT 2");
            Assert.Equal(first.Id, updated.Id);
            Assert.Throws<InvalidOperationException>(() => store.Save(first, "Замер", "SELECT 3"));
            Assert.Throws<InvalidOperationException>(() => store.Save(null, "повтор", "SELECT 4"));
            Assert.Throws<InvalidOperationException>(() => store.Delete(first));
            Assert.Equal(updated, Assert.Single(store.Read()));
            store.Delete(updated);
            Assert.Empty(other.Read());
        }
        finally { File.Delete(path); }
    }
}
