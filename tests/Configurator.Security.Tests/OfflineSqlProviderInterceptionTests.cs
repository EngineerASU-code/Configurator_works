#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

/// <summary>Observes real Microsoft.Data.SqlClient DiagnosticListener events, not application log flags.</summary>
public sealed class OfflineSqlProviderInterceptionTests
{
    [Fact]
    public async Task OfflineSourceReadAndCrudScenariosEmitNoSqlClientProviderEvents()
    {
        using var observer = new SqlClientEventObserver();
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "Offline SQL isolation" },
            TableSchemas = { new ProjectTableSchema { TableName = "dbo.Items", KeyColumns = { "Id" }, HasStableKey = true,
                Columns = { new ProjectColumnSchema { Name = "Id", DataType = "int" }, new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" } } } },
            LocalRecords = { new ProjectRecord { TableName = "dbo.Items", Key = "1", Values = new JsonObject { ["Id"] = 1, ["Name"] = "local" } } }
        };
        var source = new OfflineProjectDataSource(project);

        _ = source.GetTables();
        _ = source.GetSchema("dbo.Items");
        _ = source.GetRecords("dbo.Items");
        await source.CreateRecordAsync("dbo.Items", new Dictionary<string, object?> { ["Id"] = 2, ["Name"] = "created" });
        await source.UpdateRecordAsync("dbo.Items", new[] { "Id" }, new Dictionary<string, object?> { ["Id"] = 2 },
            new Dictionary<string, object?> { ["Name"] = "updated" });
        _ = await source.DeleteRecordsAsync("dbo.Items", new[] { "Id" }, new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["Id"] = 2 }
        });

        Assert.Equal(0, observer.ProviderEventCount);
    }


    [Theory]
    [InlineData("mode-1 database administration")]
    [InlineData("mode-2 template metadata")]
    [InlineData("mode-3 PLC/Area/class/Preview/object lookup")]
    [InlineData("mode-4 graph and relation editor")]
    [InlineData("lookup validation and uniqueness")]
    [InlineData("background or delayed Online operation")]
    public void SqlConnectionFactoryFailsClosedForEveryOfflineScenario(string scenario)
    {
        using var observer = new SqlClientEventObserver();
        var context = new ProjectDataSourceContext();
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "Guard test" }
        };
        try
        {
            context.Set(new OfflineProjectDataSource(project));
            var error = Assert.Throws<InvalidOperationException>(() =>
                SqlProviderGuard.CreateConnection("Server=127.0.0.1;Database=ShouldNeverConnect;Integrated Security=true;TrustServerCertificate=true"));
            Assert.Contains("Offline", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(scenario));
            Assert.Equal(0, observer.ProviderEventCount);
        }
        finally
        {
            context.Set((IProjectDataSource?)null);
        }
    }


    [Fact]
    public async Task ConnectionCreatedBeforeOfflineTransitionCannotOpenAfterTransition()
    {
        using var observer = new SqlClientEventObserver();
        SqlProviderGuard.SetOfflineActive(false);
        using var connection = SqlProviderGuard.CreateConnection(
            "Server=127.0.0.1;Database=MustNeverOpen;Integrated Security=true;Connect Timeout=1");
        using var context = new ProjectDataSourceContext();
        context.Set(new OfflineProjectDataSource(new ConfiguratorProject()));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqlProviderGuard.OpenConnectionAsync(connection));
            Assert.Equal(0, observer.ProviderEventCount);
        }
        finally
        {
            context.Clear();
        }
    }

    [Fact]
    public async Task RealDatabaseServiceConnectionAttemptIsBlockedBeforeSqlClientProvider()
    {
        using var observer = new SqlClientEventObserver();
        var context = new ProjectDataSourceContext();
        context.Set(new OfflineProjectDataSource(new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "Offline database service guard" }
        }));
        try
        {
            var db = new Configurator.DatabaseService(
                "Server=127.0.0.1;Database=MustNotConnect;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=1");
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.TestConnectionAsync());
            Assert.Equal(0, observer.ProviderEventCount);
        }
        finally
        {
            context.Set((IProjectDataSource?)null);
        }
    }

    [Fact]
    public void AllApplicationSqlConnectionConstructionMustPassThroughGuard()
    {
        var root = FindRepositoryRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals("SqlProviderGuard.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("new SqlConnection(", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void AllApplicationSqlConnectionOpenCallsRecheckOfflineGuard()
    {
        var root = FindRepositoryRoot();
        var unguarded = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadAllLines(path).Select((line, index) => new { path, line, lineNumber = index + 1 }))
            .Where(x => x.line.Contains("await connection.OpenAsync(", StringComparison.Ordinal) ||
                        x.line.Contains("await conn.OpenAsync(", StringComparison.Ordinal) ||
                        x.line.Contains("await cn.OpenAsync(", StringComparison.Ordinal) ||
                        x.line.Contains("connection.Open();", StringComparison.Ordinal))
            .Where(x => !Path.GetFileName(x.path).Equals("SqlProviderGuard.cs", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.line.Contains("SqlProviderGuard.OpenConnectionAsync", StringComparison.Ordinal) &&
                        !x.line.Contains("SqlProviderGuard.OpenConnection(", StringComparison.Ordinal))
            .Select(x => $"{x.path}:{x.lineNumber}: {x.line.Trim()}")
            .ToArray();
        Assert.Empty(unguarded);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Configurator.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        // Test runner commonly launches from the test project folder.
        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Configurator.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Не удалось найти корень репозитория для проверки SQL-перехвата.");
    }

    private sealed class SqlClientEventObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _allListenersSubscription;
        private IDisposable? _providerSubscription;
        private int _providerEventCount;
        public int ProviderEventCount => Volatile.Read(ref _providerEventCount);

        public SqlClientEventObserver() => _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name.Contains("SqlClient", StringComparison.OrdinalIgnoreCase))
                _providerSubscription = listener.Subscribe(this, _ => true);
        }
        public void OnNext(KeyValuePair<string, object?> value) => Interlocked.Increment(ref _providerEventCount);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { _providerSubscription?.Dispose(); _allListenersSubscription.Dispose(); }
    }
}
