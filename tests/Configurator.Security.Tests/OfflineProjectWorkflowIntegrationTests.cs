#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

/// <summary>
/// End-to-end data workflow through the same source contract used by Mode 3.
/// This is not a WPF control/UI automation test; it validates the real snapshot and
/// mutation adapters without requiring an installed SQL Server.
/// </summary>
public sealed class OfflineProjectWorkflowIntegrationTests
{
    [Fact]
    public async Task OpenSnapshotReadPreviewValidateCreateEditDeleteWithoutSqlProviderEvents()
    {
        using var observer = new SqlClientObserver();
        var project = new ConfiguratorProject
        {
            Manifest = new ConfiguratorProjectManifest { Name = "Offline workflow integration" },
            TableSchemas =
            {
                new ProjectTableSchema
                {
                    TableName = "dbo.PLC", KeyColumns = { "Id" }, HasStableKey = true,
                    Columns = { new ProjectColumnSchema { Name = "Id", DataType = "int", IsIdentity = true }, new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" } }
                },
                new ProjectTableSchema
                {
                    TableName = "dbo.Area", KeyColumns = { "Id" }, HasStableKey = true,
                    Columns = { new ProjectColumnSchema { Name = "Id", DataType = "int", IsIdentity = true }, new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" } }
                },
                new ProjectTableSchema
                {
                    TableName = "dbo.Valves", KeyColumns = { "PLC", "Record" }, HasStableKey = true,
                    Columns = { new ProjectColumnSchema { Name = "PLC", DataType = "int" }, new ProjectColumnSchema { Name = "Record", DataType = "int" }, new ProjectColumnSchema { Name = "Name", DataType = "nvarchar" } }
                }
            },
            LocalRecords =
            {
                new ProjectRecord { TableName = "dbo.PLC", Key = "Id=1", Values = new JsonObject { ["Id"] = 1, ["Name"] = "PLC-01" } },
                new ProjectRecord { TableName = "dbo.Area", Key = "Id=1", Values = new JsonObject { ["Id"] = 1, ["Name"] = "Area-A" } },
                new ProjectRecord { TableName = "dbo.Valves", Key = "PLC=1|Record=1", Values = new JsonObject { ["PLC"] = 1, ["Record"] = 1, ["Name"] = "XV-001" } }
            }
        };

        // Open the local project and resolve the same PLC/Area/class and Preview data
        // that the Mode 3 view consumes through IProjectDataSource.
        var readSource = new OfflineProjectDataSource(project);
        using var mutations = new ProjectCrudSession(readSource);
        Assert.Equal(new[] { "dbo.Area", "dbo.PLC", "dbo.Valves" }, readSource.GetTables().Select(x => x.TableName).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("PLC-01", readSource.GetRecords("dbo.PLC").Single().Values["Name"]!.GetValue<string>());
        Assert.Equal("Area-A", readSource.GetRecords("dbo.Area").Single().Values["Name"]!.GetValue<string>());
        Assert.Equal("XV-001", readSource.GetRecords("dbo.Valves").Single().Values["Name"]!.GetValue<string>());

        // Validation: duplicate project key must be rejected before changing the snapshot.
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.CreateRecordAsync("dbo.Valves",
            new Dictionary<string, object?> { ["PLC"] = 1, ["Record"] = 1, ["Name"] = "duplicate" }));

        await mutations.CreateRecordAsync("dbo.Valves", new Dictionary<string, object?> { ["PLC"] = 1, ["Record"] = 2, ["Name"] = "XV-002" });
        await mutations.UpdateRecordAsync("dbo.Valves", new[] { "PLC", "Record" },
            new Dictionary<string, object?> { ["PLC"] = 1, ["Record"] = 2 },
            new Dictionary<string, object?> { ["Name"] = "XV-002-EDITED" });
        var deleted = await mutations.DeleteRecordsAsync("dbo.Valves", new[] { "PLC", "Record" },
            new IReadOnlyDictionary<string, object?>[] { new Dictionary<string, object?> { ["PLC"] = 1, ["Record"] = 1 } });

        Assert.Equal(1, deleted);
        var remaining = readSource.GetRecords("dbo.Valves");
        Assert.Single(remaining);
        Assert.Equal("XV-002-EDITED", remaining[0].Values["Name"]!.GetValue<string>());
        Assert.Equal(0, observer.ProviderEventCount);
    }

    private sealed class SqlClientObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _all;
        private IDisposable? _provider;

        public SqlClientObserver()
        {
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }
        private int _events;
        public int ProviderEventCount => System.Threading.Volatile.Read(ref _events);
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)) _provider = value.Subscribe(this, _ => true);
        }
        public void OnNext(KeyValuePair<string, object?> value) => System.Threading.Interlocked.Increment(ref _events);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { _provider?.Dispose(); _all.Dispose(); }
    }
}
