#nullable enable
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Compare;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class CompareOnlineIntegrationTests
{
    [CompareLocalDbFact]
    public async Task ReadUsesRealSqlWithoutMutatingRowsSchemaOrSnapshot()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Valves (Record int NOT NULL PRIMARY KEY, Tag nvarchar(30) NOT NULL,
                Pressure decimal(15,4) NOT NULL CONSTRAINT DF_Valves_Pressure DEFAULT (0),
                ExactValue decimal(38,0) NOT NULL);
            INSERT dbo.Valves (Record, Tag, Pressure, ExactValue)
            VALUES (1, N'V-1', 10.2500, 99999999999999999999999999999999999999);
            """);

        var previousGuard = SqlProviderGuard.IsOfflineActive;
        SqlProviderGuard.SetOfflineActive(false);
        try
        {
            var online = await CompareSourceReader.OpenOnlineAsync(db.ConnectionString, "SQL acceptance", allowWrite: false);
            using var onlineWorkspace = (IDisposable)online.Workspace;
            Assert.False(online.CanWrite);
            Assert.Null(online.MetadataIssue);
            var schemas = await online.Workspace.GetTablesAsync();
            var table = Assert.Single(schemas);
            Assert.Equal(new[] { "Record" }, table.KeyColumns);
            var captured = Assert.Single(online.PhysicalSchema!.Tables);
            Assert.Contains(captured.Columns, column => column.Name == "Pressure" && column.DefaultDefinition == "((0))");
            Assert.Contains(captured.Keys, key => key.IsPrimary && key.IsUnique);
            var page = await online.Workspace.ReadPageAsync(new ProjectReadQuery { TableName = table.TableName });
            Assert.Equal("99999999999999999999999999999999999999", Assert.Single(page.Items).Values["ExactValue"]?.ToJsonString());
            var project = new ConfiguratorProject { TableSchemas = schemas.ToList(), LocalRecords = page.Items.ToList() };
            var sqlIdentity = new SqlConnectionStringBuilder(db.ConnectionString);
            project.Architecture["sourceServer"] = sqlIdentity.DataSource;
            project.Architecture["sourceDatabase"] = sqlIdentity.InitialCatalog;
            project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(online.PhysicalSchema);
            project.GraphRelations = JsonSerializer.SerializeToNode(new Configurator.GraphRelationProjectDocument
            {
                ManualRelations =
                [
                    new() { Source = "dbo.Removed", Target = table.TableName, SourceColumn = "Record", TargetColumn = "Record" },
                    new() { Source = table.TableName, Target = table.TableName, SourceColumn = "Record", TargetColumn = "Record" }
                ]
            })!.AsObject();
            await new ProjectPackageService().SaveAsync(files.A, project);
            var packageBefore = File.ReadAllBytes(files.A);
            var offline = await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: false);
            using var offlineWorkspace = (IDisposable)offline.Workspace;

            var report = await Configurator.Core.Compare.CompareEngine.CompareAsync(online, offline);
            Assert.Contains(report.Objects, difference => difference.Status == Configurator.Core.Compare.CompareStatus.Equal);
            var relations = Assert.Single(report.Architecture, item => item.Title == "Связи");
            var manual = Assert.Single(relations.Children!, item => item.Title.StartsWith("Логическая связь", StringComparison.Ordinal));
            Assert.Equal(CompareStatus.OnlyB, manual.Status);
            Assert.Equal(10.2500m, await db.DecimalAsync("SELECT Pressure FROM dbo.Valves WHERE Record=1"));
            Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves"));
            Assert.Equal(4m, await db.DecimalAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Valves')"));
            Assert.Equal(packageBefore, File.ReadAllBytes(files.A));
        }
        finally
        {
            SqlProviderGuard.SetOfflineActive(previousGuard);
        }
    }

    [CompareLocalDbFact]
    public async Task ExportAndOnlineCompareUnkeyedRowsByContentWithDuplicatesAndXml()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Valves2S (Record int NOT NULL, Tag nvarchar(30) NOT NULL, Detail xml NULL,
                Pressure decimal(15,4) NOT NULL);
            INSERT dbo.Valves2S (Record, Tag, Detail, Pressure)
            VALUES (3, N'Клапан', N'<v/>', 10.2500), (2, N'Не совпадает порядок', NULL, 1),
                   (3, N'Клапан', N'<v/>', 10.2500);
            CREATE TABLE dbo.ExportUnique (Code int NOT NULL UNIQUE, Value int NOT NULL);
            INSERT dbo.ExportUnique VALUES (1, 40);
            """);

        var previousGuard = SqlProviderGuard.IsOfflineActive;
        SqlProviderGuard.SetOfflineActive(false);
        try
        {
            var online = await CompareSourceReader.OpenOnlineAsync(db.ConnectionString, "SQL acceptance", allowWrite: false);
            using var onlineWorkspace = (IDisposable)online.Workspace;
            Assert.NotNull(online.PhysicalSchema);
            var schemas = await online.Workspace.GetTablesAsync();
            var sql = new Configurator.DatabaseService(db.ConnectionString);
            var project = new ConfiguratorProject();
            project.Architecture["sourceServer"] = new SqlConnectionStringBuilder(db.ConnectionString).DataSource;
            project.Architecture["sourceDatabase"] = new SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
            project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(online.PhysicalSchema);
            project.Architecture["classDefinitions"] = JsonSerializer.SerializeToNode(await online.Workspace.GetClassDefinitionsAsync());
            project.GraphRelations = await online.Workspace.GetGraphRelationsAsync();
            foreach (var schema in schemas)
            {
                var key = await sql.GetPrimaryKeyColumnsAsync(schema.TableName);
                project.TableSchemas.Add(new ProjectTableSchema
                {
                    TableName = schema.TableName, Columns = schema.Columns, KeyColumns = key, HasStableKey = key.Count != 0
                });
                var data = await sql.GetAllTableRowsAsync(schema.TableName);
                int index = 0;
                foreach (DataRow row in data.Rows.Cast<DataRow>().Reverse())
                {
                    var values = new JsonObject();
                    foreach (DataColumn column in data.Columns.Cast<DataColumn>().Reverse())
                        values[column.ColumnName] = row.IsNull(column) ? null : JsonSerializer.SerializeToNode(row[column]);
                    project.LocalRecords.Add(new ProjectRecord
                    {
                        TableName = schema.TableName, Key = $"snapshot:{index++}", Values = values
                    });
                }
            }
            await new ProjectPackageService().SaveAsync(files.A, project);
            var packageBefore = File.ReadAllBytes(files.A);
            var offline = await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: false);
            using var offlineWorkspace = (IDisposable)offline.Workspace;

            var unchanged = await CompareEngine.CompareAsync(online, offline);
            Assert.All(unchanged.Architecture, row => Assert.Equal(CompareStatus.Equal, row.Status));
            Assert.DoesNotContain(unchanged.Objects, row => row.Status != CompareStatus.Equal);

            await db.ExecuteAsync("DELETE TOP (1) FROM dbo.Valves2S WHERE Record=3;");
            var changed = await CompareEngine.CompareAsync(online, offline);
            var warning = Assert.Single(changed.Objects, row => row.Status != CompareStatus.Equal);
            Assert.Contains("Valves2S", warning.Title);
            Assert.Equal(CompareStatus.Ambiguous, warning.Status);
            Assert.Equal("Строк без совпадения: 0", warning.A);
            Assert.Equal("Строк без совпадения: 1", warning.B);
            Assert.Null(warning.Pair);
            Assert.Null(warning.Children);
            Assert.Equal(packageBefore, File.ReadAllBytes(files.A));
        }
        finally
        {
            SqlProviderGuard.SetOfflineActive(previousGuard);
        }
    }

    [CompareLocalDbFact]
    public async Task BothWorkspacesRollBackInvalidBatchAndCompareSuccessfulBatchExactly()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Valves (Record int NOT NULL PRIMARY KEY, Name nvarchar(30) NOT NULL);
            INSERT dbo.Valves VALUES (1, N'Existing');
            """);

        var online = await CompareSourceReader.OpenOnlineAsync(db.ConnectionString, "Batch acceptance", allowWrite: true);
        using var onlineWorkspace = (IDisposable)online.Workspace;
        var schema = Assert.Single(await online.Workspace.GetTablesAsync());
        var initialRows = await online.Workspace.ReadPageAsync(new ProjectReadQuery { TableName = schema.TableName });
        var project = new ConfiguratorProject { TableSchemas = [schema], LocalRecords = initialRows.Items.ToList() };
        var sqlIdentity = new SqlConnectionStringBuilder(db.ConnectionString);
        project.Architecture["sourceServer"] = sqlIdentity.DataSource;
        project.Architecture["sourceDatabase"] = sqlIdentity.InitialCatalog;
        project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(online.PhysicalSchema);
        await new ProjectPackageService().SaveAsync(files.A, project);
        var originalPackage = File.ReadAllBytes(files.A);
        var offline = await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: true);
        using var offlineWorkspace = (IDisposable)offline.Workspace;

        static IReadOnlyDictionary<string, object?> MakeRow(int id, string name) =>
            new Dictionary<string, object?> { ["Record"] = id, ["Name"] = name };

        var invalid = new[] { MakeRow(2, "Added"), MakeRow(1, "Duplicate") };
        await Assert.ThrowsAsync<SqlException>(() => online.Workspace.CreateRecordsAsync(schema.TableName, invalid));
        await Assert.ThrowsAsync<InvalidOperationException>(() => offline.Workspace.CreateRecordsAsync(schema.TableName, invalid));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves"));
        Assert.Equal(originalPackage, File.ReadAllBytes(files.A));
        var rolledBack = await CompareEngine.CompareAsync(online, offline);
        Assert.All(rolledBack.Objects, difference => Assert.Equal(CompareStatus.Equal, difference.Status));

        var valid = new[] { MakeRow(2, "Added"), MakeRow(3, "Second") };
        await online.Workspace.CreateRecordsAsync(schema.TableName, valid);
        await offline.Workspace.CreateRecordsAsync(schema.TableName, valid);
        var equal = await CompareEngine.CompareAsync(online, offline);
        Assert.Equal(3m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.Valves"));
        Assert.All(equal.Objects, difference => Assert.Equal(CompareStatus.Equal, difference.Status));
        await new ProjectPackageService().SaveAsync(files.B, offline.OfflineProject!);
        var reopened = await CompareSourceReader.OpenOfflineAsync(files.B, allowWrite: false);
        using var reopenedWorkspace = (IDisposable)reopened.Workspace;
        var saved = await CompareEngine.CompareAsync(online, reopened);
        Assert.Equal(3, saved.Objects.Count);
        Assert.All(saved.Objects, difference => Assert.Equal(CompareStatus.Equal, difference.Status));

        await db.ExecuteAsync("UPDATE dbo.Valves SET Name=N'Changed on SQL' WHERE Record=3;");
        var changed = await CompareEngine.CompareAsync(online, reopened);
        var changedRow = Assert.Single(changed.Objects, difference => difference.Status != CompareStatus.Equal);
        Assert.Equal(CompareStatus.Changed, changedRow.Status);
        Assert.Contains(changedRow.Children!, cell => cell.Column == "Name" && cell.Status == CompareStatus.Changed);
        Assert.Equal(originalPackage, File.ReadAllBytes(files.A));
    }

    [CompareLocalDbFact]
    public async Task LegacyPlcLookupUsesEqualKeysAndLabelsInSqlAndCapturedRows()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.PLCs (Id int NOT NULL PRIMARY KEY, Name nvarchar(30) NOT NULL);
            INSERT dbo.PLCs VALUES (10, N'Контроллер 10'), (20, N'Контроллер 20');
            """);
        var source = await CompareSourceReader.OpenOnlineAsync(db.ConnectionString, "Lookup acceptance", allowWrite: false);
        using var workspace = (IDisposable)source.Workspace;
        var schema = Assert.Single(await source.Workspace.GetTablesAsync());
        var page = await source.Workspace.ReadPageAsync(new ProjectReadQuery { TableName = schema.TableName });
        var snapshot = new ConfiguratorProject { TableSchemas = [schema], LocalRecords = page.Items.ToList() };
        var online = await new Configurator.LookupOptionsService(new Configurator.DatabaseService(db.ConnectionString))
            .TryGetAsync("Valve", "dbo.Valves", "PLC");
        var offline = Configurator.OfflineLookupOptionsService.Get(snapshot, "Valve", "dbo.Valves",
            new Configurator.Core.Architecture.FieldDefinition { Name = "PLC" });

        Assert.Equal(new[] { "10", "20" }, offline.Select(option => option.Key).Order());
        Assert.Equal(online.Select(option => (option.Key, option.Display)).Order(),
            offline.Select(option => (option.Key, option.Display)).Order());
        Assert.Equal(2m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.PLCs"));
    }

    [CompareLocalDbFact]
    public async Task PhysicalRelationsSurviveExportAndStaleGraphRulesProduceNoDifference()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            EXEC('CREATE SCHEMA plantA'); EXEC('CREATE SCHEMA plantB');
            CREATE TABLE dbo.Parent (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE plantA.Child (ParentId int NOT NULL CONSTRAINT FK_Same REFERENCES dbo.Parent(Id));
            CREATE TABLE plantB.Child (ParentId int NOT NULL CONSTRAINT FK_Same REFERENCES dbo.Parent(Id));
            """);
        var online = await CompareSourceReader.OpenOnlineAsync(db.ConnectionString, "Relations acceptance", allowWrite: false);
        using var onlineWorkspace = (IDisposable)online.Workspace;
        var schemas = await online.Workspace.GetTablesAsync();
        Assert.Equal(2, online.PhysicalSchema!.Relations.Count(relation =>
            relation.Kind == Configurator.Core.Architecture.DatabaseRelationKind.PhysicalForeignKey));
        var project = new ConfiguratorProject { TableSchemas = schemas.ToList() };
        project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(online.PhysicalSchema);
        project.GraphRelations = JsonSerializer.SerializeToNode(new Configurator.GraphRelationProjectDocument
        {
            ManualRelations = [new() { Source = "dbo.Missing", SourceColumn = "Id", Target = "dbo.Parent", TargetColumn = "Id" }],
            SuppressedAutomaticRelations = ["LOGICAL|dbo.Missing|dbo.Parent|Id|Id"]
        })!.AsObject();
        await new ProjectPackageService().SaveAsync(files.A, project);
        var bytes = File.ReadAllBytes(files.A);
        var offline = await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: false);
        using var offlineWorkspace = (IDisposable)offline.Workspace;

        var result = await CompareEngine.CompareAsync(online, offline);
        var relations = Assert.Single(result.Architecture, difference => difference.Title == "Связи");
        Assert.Equal(2, relations.Children!.Count);
        Assert.All(relations.Children, difference => Assert.Equal(CompareStatus.Equal, difference.Status));
        Assert.Equal(bytes, File.ReadAllBytes(files.A));
    }

    [CompareLocalDbFact]
    public async Task KeyedCreateReturnsTheSqlIdentityAndReadsActualStoredValues()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.KeyedObjects (Id int IDENTITY(42,7) NOT NULL PRIMARY KEY, Name nvarchar(30) NOT NULL,
                PostInsertValue int NOT NULL DEFAULT (0));
            EXEC('CREATE TRIGGER dbo.AfterKeyedInsert ON dbo.KeyedObjects AFTER INSERT AS
                BEGIN SET NOCOUNT ON;
                UPDATE dbo.KeyedObjects SET PostInsertValue=7
                WHERE Id IN (SELECT Id FROM inserted);
                END');
            """);
        using var session = new ProjectCrudSession(new OnlineProjectCrudDataSource(db.ConnectionString));
        var key = await session.CreateRecordAndGetKeyAsync("dbo.KeyedObjects",
            new Dictionary<string, object?> { ["Name"] = "Copied" });

        Assert.Equal(42, Assert.IsType<int>(key["Id"]));
        Assert.Equal(7m, await db.DecimalAsync("SELECT PostInsertValue FROM dbo.KeyedObjects WHERE Id=42"));
        Assert.Equal(1m, await db.DecimalAsync("SELECT COUNT(*) FROM dbo.KeyedObjects"));
    }

    [CompareLocalDbFact]
    public async Task FailedOnlineConnectionCannotProduceSuccessfulSourceReads()
    {
        await using var db = await CompareLocalDb.CreateAsync();
        var builder = new SqlConnectionStringBuilder(db.ConnectionString)
        {
            InitialCatalog = "GassCompareMissing_" + Guid.NewGuid().ToString("N"),
            ConnectTimeout = 2
        };
        var online = await CompareSourceReader.OpenOnlineAsync(builder.ConnectionString, "Missing acceptance database", allowWrite: false);
        using var workspace = (IDisposable)online.Workspace;
        Assert.NotNull(online.MetadataIssue);
        await Assert.ThrowsAsync<SqlException>(() => online.Workspace.GetTablesAsync());
    }
}

// Dedicated SQL acceptance is opt-in. The only targets are generated test databases.
public sealed class CompareLocalDbFactAttribute : FactAttribute
{
    internal const string EnvironmentName = "GASS_COMPARE_LOCALDB_INSTANCE";
    public CompareLocalDbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentName)))
            Skip = "Set GASS_COMPARE_LOCALDB_INSTANCE to an isolated GassCompareAcceptance_* instance.";
    }
}

internal sealed class CompareLocalDb : IAsyncDisposable
{
    private readonly string _database;
    private readonly string _master;
    internal string ConnectionString { get; }

    private CompareLocalDb(string instance)
    {
        _database = "GassCompare_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\" + instance,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Pooling = false,
            ConnectTimeout = 10
        };
        _master = builder.ConnectionString;
        builder.InitialCatalog = _database;
        ConnectionString = builder.ConnectionString;
    }

    internal static async Task<CompareLocalDb> CreateAsync()
    {
        var instance = Environment.GetEnvironmentVariable(CompareLocalDbFactAttribute.EnvironmentName) ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(instance, "^GassCompareAcceptance_[A-Za-z0-9_]+$"))
            throw new InvalidOperationException("Integration tests require a dedicated GassCompareAcceptance_* LocalDB instance.");
        var fixture = new CompareLocalDb(instance);
        await fixture.ExecuteAsync($"CREATE DATABASE [{fixture._database}]", fixture._master);
        return fixture;
    }

    internal async Task ExecuteAsync(string sql, string? connectionString = null)
    {
        await using var connection = new SqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<decimal> DecimalAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync() =>
        await ExecuteAsync($"DROP DATABASE [{_database}]", _master);
}
