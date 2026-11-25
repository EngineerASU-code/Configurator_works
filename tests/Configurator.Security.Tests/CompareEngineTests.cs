#nullable enable
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Compare;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class CompareEngineTests
{
    [Fact]
    public async Task SameLineagePairsRowsByTypedKeyAndShowsChangedCell()
    {
        var classId = "equipment";
        var classDefinition = Class(classId, "Equipment", "dbo.Valves");
        var sourceA = Source(classDefinition, Schema("dbo.Valves"), Record(7, "Pump", "1.25"));
        var sourceB = Source(classDefinition, Schema("dbo.Valves"), Record(7, "Pump", "1.50"));

        var result = await CompareEngine.CompareAsync(sourceA, sourceB);

        var item = Assert.Single(result.Objects);
        Assert.Equal(CompareStatus.Changed, item.Status);
        Assert.Contains("Record=7", item.Title);
        var pressure = Assert.Single(item.Children!, x => x.Column == "Pressure");
        Assert.Equal(CompareStatus.Changed, pressure.Status);
        Assert.NotNull(pressure.Pair);
        Assert.Equal(7, pressure.Pair!.RecordA!.Values["Record"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("Pump B", CompareStatus.KeyConflict)]
    [InlineData("Pump A", CompareStatus.Changed)]
    [InlineData("pump a", CompareStatus.Changed)]
    [InlineData("", CompareStatus.Changed)]
    public async Task SharedRecordKeyConflictsOnlyForDifferentNonemptyObjectNames(string name, CompareStatus expected)
    {
        const string table = "dbo.Reglers";
        var definition = Class("pid", "PID", table);
        definition.IsSystem = true;
        var schema = Schema(table);
        schema.KeyColumns.AddRange(["PLC", "PLC_Class_Number"]);
        schema.Columns.AddRange([
            new ProjectColumnSchema { Name = "PLC", DataType = "int" },
            new ProjectColumnSchema { Name = "PLC_Class_Number", DataType = "int" },
            new ProjectColumnSchema { Name = "Name", DataType = "nvarchar", MaxLength = 100 }
        ]);
        ProjectRecord Pid(string objectName, string pressure) => new()
        {
            TableName = table,
            Key = "pid-7",
            Values = new JsonObject
            {
                ["Record"] = 7, ["PLC"] = 1, ["PLC_Class_Number"] = 3,
                ["Name"] = objectName, ["Pressure"] = JsonNode.Parse(pressure)
            }
        };
        var sourceA = Source(definition, schema, Pid("Pump A", "1.25")) with { CanWrite = true };
        var sourceB = Source(definition, schema, Pid(name, "1.50")) with { CanWrite = true };

        var item = Assert.Single((await CompareEngine.CompareAsync(sourceA, sourceB)).Objects);
        Assert.Equal(expected, item.Status);
        if (expected == CompareStatus.KeyConflict)
        {
            Assert.Equal("Pump A", item.A);
            Assert.Equal("Pump B", item.B);
            Assert.Null(item.Children);
            Assert.NotNull(item.Pair?.RecordA);
            Assert.NotNull(item.Pair?.RecordB);
            Assert.False(CompareTransferPreview.Evaluate(sourceA, sourceB, item, aToB: true).IsAllowed);
            Assert.False(CompareTransferPreview.Evaluate(sourceA, sourceB, item, aToB: false).IsAllowed);
        }
        else
        {
            Assert.Equal(CompareStatus.Changed, Assert.Single(item.Children!, row => row.Column == "Pressure").Status);
        }
    }

    [Fact]
    public async Task TagIdentifiesObjectsWhenTheTableHasNoNameColumn()
    {
        var definition = Class("equipment", "Equipment", "dbo.Valves");
        var sourceA = Source(definition, Schema("dbo.Valves"), Record(7, "Pump A", "1.25"));
        var sourceB = Source(definition, Schema("dbo.Valves"), Record(7, "Pump B", "1.25"));

        var item = Assert.Single((await CompareEngine.CompareAsync(sourceA, sourceB)).Objects);

        Assert.Equal(CompareStatus.KeyConflict, item.Status);
        Assert.Equal("Pump A", item.A);
        Assert.Equal("Pump B", item.B);
        Assert.Null(item.Children);
    }

    [Fact]
    public async Task MissingObjectsProduceOneDifferencePerObjectWithoutPropertyRows()
    {
        var definition = Class("equipment", "Equipment", "dbo.Valves");
        var sourceA = Source(definition, Schema("dbo.Valves"), Record(10, "Only A", "1.25"));
        var sourceB = Source(definition, Schema("dbo.Valves"), Record(20, "Only B", "2.50"));

        var result = await CompareEngine.CompareAsync(sourceA, sourceB);

        Assert.Equal(2, result.Objects.Count);
        var onlyA = Assert.Single(result.Objects, item => item.Status == CompareStatus.OnlyA);
        var onlyB = Assert.Single(result.Objects, item => item.Status == CompareStatus.OnlyB);
        Assert.Contains("Record=10", onlyA.Title);
        Assert.Contains("Record=20", onlyB.Title);
        Assert.Null(onlyA.Children);
        Assert.Null(onlyB.Children);
        Assert.NotNull(onlyA.Pair?.RecordA);
        Assert.NotNull(onlyB.Pair?.RecordB);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateExportsIgnoreCaptureMetadataAndRetainActualChanges(bool changeValue)
    {
        using var files = new CompareTestDirectory();
        var before = ExportSnapshot("1.25", DateTimeOffset.UnixEpoch);
        var after = ExportSnapshot(changeValue ? "1.50" : "1.25", DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.NotEqual(before.Manifest.ProjectId, after.Manifest.ProjectId);
        var packages = new ProjectPackageService();
        await packages.SaveAsync(files.A, before);
        await packages.SaveAsync(files.B, after);
        byte[] originalA = File.ReadAllBytes(files.A);
        byte[] originalB = File.ReadAllBytes(files.B);

        var a = await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: false);
        using var workspaceA = (IDisposable)a.Workspace;
        var b = await CompareSourceReader.OpenOfflineAsync(files.B, allowWrite: false);
        using var workspaceB = (IDisposable)b.Workspace;
        var result = await CompareEngine.CompareAsync(a, b);

        Assert.Empty(result.Architecture);
        if (changeValue)
        {
            var changedObject = Assert.Single(result.Objects, item => item.Status != CompareStatus.Equal);
            var cell = Assert.Single(changedObject.Children!, child => child.Status != CompareStatus.Equal);
            Assert.Equal("Pressure", cell.Column);
            Assert.Equal(CompareStatus.Changed, cell.Status);
        }
        else
        {
            Assert.Empty(result.Objects);
        }

        Assert.Equal(originalA, File.ReadAllBytes(files.A));
        Assert.Equal(originalB, File.ReadAllBytes(files.B));
    }

    [Fact]
    public async Task ManualRelationsCompareResolvedGraphEndpointsAndIgnoreHiddenEntries()
    {
        var a = CreateSource(
            Link("dbo.Missing", "hidden"),
            Link("legacy.Valves", "same"),
            Link("dbo.Valves", "before", "OtherField"));
        var b = CreateSource(
            Link("dbo.Valves", "same"),
            Link("dbo.Valves", "after", "OtherField"),
            Link("dbo.Valves", "added", "NewField"));

        var result = await CompareEngine.CompareAsync(a, b);

        var relations = Assert.Single(result.Architecture, item => item.Title == "Связи");
        Assert.Collection(relations.Children!,
            item => Assert.Equal(CompareStatus.OnlyB, item.Status),
            item => Assert.Equal(CompareStatus.Changed, item.Status),
            item => Assert.Equal(CompareStatus.Equal, item.Status));

        static GraphRelationDefinition Link(string source, string rule, string column = "Record") =>
            new() { Source = source, Target = "dbo.Units", SourceColumn = column, TargetColumn = "Record", Rule = rule };

        static CompareSource CreateSource(params GraphRelationDefinition[] links)
        {
            var valves = Schema("dbo.Valves");
            valves.Columns.Add(new ProjectColumnSchema { Name = "OtherField" });
            valves.Columns.Add(new ProjectColumnSchema { Name = "NewField" });
            var workspace = new TestWorkspace([], [valves, Schema("dbo.Units")], new Dictionary<string, IReadOnlyList<ProjectRecord>>())
            {
                GraphRelations = JsonSerializer.SerializeToNode(new GraphRelationProjectDocument { ManualRelations = [.. links] })!.AsObject()
            };
            var physical = new DatabaseSchema();
            return new CompareSource(workspace, "shared-project", "server/db", physical);
        }
    }

    private static ConfiguratorProject ExportSnapshot(string pressure, DateTimeOffset capturedAt)
    {
        const string table = "dbo.Valves";
        var physical = new DatabaseSchema { AnalyzedAtUtc = capturedAt };
        physical.Tables.Add(new DatabaseTable { Name = "Valves" });
        return new ConfiguratorProject
        {
            Architecture = new JsonObject
            {
                ["sourceServer"] = "test-server",
                ["sourceDatabase"] = "test-database",
                ["capturedUtc"] = JsonValue.Create(capturedAt),
                ["physicalSchemaCapturedUtc"] = JsonValue.Create(capturedAt),
                ["physicalSchema"] = JsonSerializer.SerializeToNode(physical),
                ["classDefinitions"] = JsonSerializer.SerializeToNode(new[] { Class("equipment", "Equipment", table) })
            },
            TableSchemas = [Schema(table)],
            LocalRecords = [Record(7, "Pump", pressure), Record(8, "Unchanged", "2.25")]
        };
    }

    [Fact]
    public async Task DifferentLineageMakesRowsAmbiguousEvenWhenLocalKeysMatch()
    {
        var definition = Class("equipment", "Equipment", "dbo.Valves");
        var sourceA = Source(definition, Schema("dbo.Valves"), Record(7, "Pump", "1.25"), origin: "server-a/db-a");
        var sourceB = Source(definition, Schema("dbo.Valves"), Record(7, "Pump", "1.25"), origin: "server-b/db-b");

        var result = await CompareEngine.CompareAsync(sourceA, sourceB);

        Assert.All(result.Objects, item => Assert.Equal(CompareStatus.Ambiguous, item.Status));
        Assert.All(result.Objects, item => Assert.Null(item.Pair));
    }

    [Fact]
    public async Task SameClassNameWithDifferentIdsIsAmbiguousAndNeverPaired()
    {
        var classA = Class("class-a", "Equipment", "dbo.Valves");
        var classB = Class("class-b", "Equipment", "dbo.Valves");
        var sourceA = Source(classA, Schema("dbo.Valves"), Record(7, "Pump", "1.25"));
        var sourceB = Source(classB, Schema("dbo.Valves"), Record(7, "Pump", "1.25"));

        var result = await CompareEngine.CompareAsync(sourceA, sourceB);

        Assert.All(result.Objects, item => Assert.Equal(CompareStatus.Ambiguous, item.Status));
        Assert.All(result.Objects, item => Assert.Null(item.Pair));
        var classes = Assert.Single(result.Architecture, x => x.Title == "Классы");
        Assert.Contains(classes.Children!, x => x.Title == "Класс · Equipment" && x.Status == CompareStatus.Ambiguous);
    }

    [Fact]
    public async Task DuplicateOrIncompleteKeysAreAmbiguousWithoutFalseOnlyRows()
    {
        var definition = Class("equipment", "Equipment", "dbo.Valves");
        var duplicateA = Source(definition, Schema("dbo.Valves"),
            Record(7, "Pump A", "1.25"), Record(7, "Pump B", "1.50"));
        var uniqueB = Source(definition, Schema("dbo.Valves"), Record(7, "Pump", "1.25"));

        var duplicateResult = await CompareEngine.CompareAsync(duplicateA, uniqueB);
        Assert.All(duplicateResult.Objects, item => Assert.Equal(CompareStatus.Ambiguous, item.Status));

        var incomplete = Record(7, "Missing key", "1.25");
        incomplete.Values.Remove("Record");
        var incompleteResult = await CompareEngine.CompareAsync(
            Source(definition, Schema("dbo.Valves"), incomplete),
            uniqueB);
        Assert.All(incompleteResult.Objects, item => Assert.Equal(CompareStatus.Ambiguous, item.Status));
        Assert.All(incompleteResult.Objects, item => Assert.Null(item.Pair));
    }

    [Fact]
    public async Task CompatibleClassIdentityPairsDifferentPhysicalTableNames()
    {
        var definitionA = Class("equipment", "Equipment", "dbo.Valves");
        var definitionB = Class("equipment", "Equipment", "dbo.Valve");
        var sourceA = Source(definitionA, Schema("dbo.Valves"), Record(7, "Pump", "1.25"));
        var sourceB = Source(definitionB, Schema("dbo.Valve"), Record(7, "Pump", "1.25"));

        var result = await CompareEngine.CompareAsync(sourceA, sourceB);

        var item = Assert.Single(result.Objects);
        Assert.Equal(CompareStatus.Equal, item.Status);
        Assert.NotNull(item.Pair?.RecordA);
        Assert.NotNull(item.Pair?.RecordB);
    }

    [Fact]
    public async Task ColumnDifferencesAreReportedSeparatelyFromObjectValues()
    {
        var definition = Class("equipment", "Equipment", "dbo.Valves");
        var schemaA = Schema("dbo.Valves");
        var schemaB = Schema("dbo.Valves");
        schemaB.Columns.Single(x => x.Name == "Pressure").DataType = "nvarchar";
        var sourceA = Source(definition, schemaA, Record(7, "Pump", "1.25"));
        var sourceB = Source(definition, schemaB, Record(7, "Pump", "1.25"));

        var result = await CompareEngine.CompareAsync(sourceA, sourceB);

        var tables = Assert.Single(result.Architecture, x => x.Title == "Таблицы");
        var table = Assert.Single(tables.Children!, x => x.Title == "Таблица · Equipment");
        var pressure = Assert.Single(table.Children!, x => x.Title == "Столбец · Pressure");
        Assert.Contains(pressure.Children!, x => x.Title == "Тип" && x.Status == CompareStatus.Changed);
        Assert.Equal(CompareStatus.NotComparable,
            Assert.Single(result.Objects.Single().Children!, x => x.Column == "Pressure").Status);
    }

    [Fact]
    public async Task ReadFailureIsReportedWithoutInventingOnlyRows()
    {
        var definition = Class("equipment", "Equipment", "dbo.Valves");
        var workspaceA = Workspace(definition, Schema("dbo.Valves"), Record(7, "Pump", "1.25"));
        var workspaceB = Workspace(definition, Schema("dbo.Valves"), Record(7, "Pump", "1.25"));
        workspaceB.FailTableRead = true;
        var sourceA = new CompareSource(workspaceA, "same", "server/db", null);
        var sourceB = new CompareSource(workspaceB, "same", "server/db", null);

        var result = await CompareEngine.CompareAsync(sourceA, sourceB);

        Assert.Contains(result.Architecture, x => x.Title == "Источник B · Метаданные таблиц" && x.Status == CompareStatus.Error);
        Assert.DoesNotContain(result.Architecture.SelectMany(x => x.Children ?? []),
            x => x.Status is CompareStatus.OnlyA or CompareStatus.OnlyB);
        Assert.Contains(result.Objects, x => x.Status == CompareStatus.NotComparable);
    }

    [Fact]
    public async Task LookupDefinitionsMarkOwnAndReferencedColumnsAsDependencies()
    {
        var equipment = Class("equipment", "Equipment", "dbo.Valves");
        equipment.Fields.Add(new FieldDefinition
        {
            Id = "status-field",
            Name = "Status",
            DataType = "int",
            LookupTable = "dbo.Statuses",
            LookupKeyField = "Record",
            LookupDisplayField = "Name"
        });
        var status = Class("test-status", "Test status catalog", "dbo.Statuses");
        var equipmentSchema = Schema("dbo.Valves");
        equipmentSchema.Columns.Add(new ProjectColumnSchema { Name = "Status", DataType = "int" });
        var statusSchema = new ProjectTableSchema
        {
            TableName = "dbo.Statuses",
            HasStableKey = true,
            KeyColumns = ["Record"],
            Columns =
            [
                new ProjectColumnSchema { Name = "Record", DataType = "int", IsNullable = false },
                new ProjectColumnSchema { Name = "Name", DataType = "nvarchar", IsNullable = false }
            ]
        };
        var equipmentRecord = Record(7, "Pump", "1.25");
        equipmentRecord.Values["Status"] = JsonValue.Create(1);
        var statusRecord = new ProjectRecord
        {
            TableName = "dbo.Statuses",
            Key = "legacy-status-1",
            Values = new JsonObject { ["Record"] = 1, ["Name"] = "Running" }
        };
        var records = new Dictionary<string, IReadOnlyList<ProjectRecord>>
        {
            ["dbo.Valves"] = [equipmentRecord],
            ["dbo.Statuses"] = [statusRecord]
        };
        var workspace = new TestWorkspace([equipment, status], [equipmentSchema, statusSchema], records);
        var source = new CompareSource(workspace, "same", "server/db", null);

        var result = await CompareEngine.CompareAsync(source, source);

        var equipmentObject = Assert.Single(result.Objects, x => x.Pair?.A?.Schema.TableName == "dbo.Valves");
        Assert.Contains("Status", equipmentObject.Pair!.A!.DependencyColumns);
        var statusObject = Assert.Single(result.Objects, x => x.Pair?.A?.Schema.TableName == "dbo.Statuses");
        Assert.Contains("Record", statusObject.Pair!.A!.DependencyColumns);
        Assert.Contains("Name", statusObject.Pair.A.DependencyColumns);
    }

    [Fact]
    public void MetadataFingerprintIsStableAndIncludesLogicalRelations()
    {
        var classes = new[] { Class("equipment", "Equipment", "dbo.Valves") };
        var graph = new JsonObject();
        var original = CompareEngine.FingerprintMetadata(classes, graph);

        Assert.Equal(original, CompareEngine.FingerprintMetadata(classes, new JsonObject()));
        Assert.NotEqual(original, CompareEngine.FingerprintMetadata(classes,
            new JsonObject { ["SuppressedAutomaticRelations"] = new JsonArray(JsonValue.Create("LOGICAL|dbo.A|dbo.B|AId|BId")) }));
    }

    [Fact]
    public void CompareValuesPreservesNullEmptyAndHighPrecisionDecimalDifferences()
    {
        Assert.Equal(CompareStatus.Equal, CompareValues.Compare(null, null, "nvarchar", "nvarchar"));
        Assert.Equal(CompareStatus.Changed, CompareValues.Compare(null, JsonValue.Create(""), "nvarchar", "nvarchar"));
        Assert.Equal(CompareStatus.Changed, CompareValues.Compare(JsonValue.Create(0), null, "int", "int"));

        var first = JsonNode.Parse("0.1234567890123456789012345678901");
        var second = JsonNode.Parse("0.1234567890123456789012345678902");
        Assert.Equal(CompareStatus.Changed, CompareValues.Compare(first, second, "decimal", "decimal"));
        Assert.Equal(CompareStatus.Equal,
            CompareValues.Compare(JsonNode.Parse("1.2300"), JsonNode.Parse("1.23"), "decimal", "decimal"));
    }

    [Fact]
    public void CompareValuesDoesNotCoerceStringToNumberOrMalformedTypedValues()
    {
        Assert.Equal(CompareStatus.NotComparable,
            CompareValues.Compare(JsonValue.Create(1), JsonValue.Create("1"), "int", "nvarchar"));
        Assert.Equal(CompareStatus.NotComparable, CompareValues.Compare(null, null, "int", "nvarchar"));
        Assert.Equal(CompareStatus.Changed, CompareValues.Compare(JsonValue.Create(false), null, "bit", "bit"));
        Assert.Equal(CompareStatus.Changed, CompareValues.Compare(JsonValue.Create("2026-11-20T09:00:00+03:00"),
            JsonValue.Create("2026-11-20T06:00:00+00:00"), "datetimeoffset", "datetimeoffset"));
        Assert.Equal(CompareStatus.NotComparable,
            CompareValues.Compare(JsonValue.Create("not a number"), JsonValue.Create("not a number"), "decimal", "decimal"));
    }

    private static CompareSource Source(
        ClassDefinition definition,
        ProjectTableSchema schema,
        params ProjectRecord[] records) =>
        new(Workspace(definition, schema, records), "shared-project", "server/db", null);

    private static CompareSource Source(
        ClassDefinition definition,
        ProjectTableSchema schema,
        ProjectRecord record,
        string origin) =>
        new(Workspace(definition, schema, record), origin, origin, null);

    private static TestWorkspace Workspace(
        ClassDefinition definition,
        ProjectTableSchema schema,
        params ProjectRecord[] records) =>
        new([definition], [schema], new Dictionary<string, IReadOnlyList<ProjectRecord>>
        {
            [schema.TableName] = records
        });

    private static ClassDefinition Class(string id, string name, string table) => new()
    {
        Id = id,
        Name = name,
        StorageMappings = [new StorageMapping { Role = "primary", TableName = table, KeyColumns = ["Record"] }],
        Fields =
        [
            new FieldDefinition { Id = "record-field", Name = "Record", DataType = "int", Nullable = false },
            new FieldDefinition { Id = "pressure-field", Name = "Pressure", DataType = "decimal", Nullable = true }
        ]
    };

    private static ProjectTableSchema Schema(string table) => new()
    {
        TableName = table,
        HasStableKey = true,
        KeyColumns = ["Record"],
        Columns =
        [
            new ProjectColumnSchema { Name = "Record", DataType = "int", IsNullable = false },
            new ProjectColumnSchema { Name = "Tag", DataType = "nvarchar", IsNullable = true, MaxLength = 100 },
            new ProjectColumnSchema { Name = "Pressure", DataType = "decimal", IsNullable = true, Precision = 18, Scale = 4 }
        ]
    };

    private static ProjectRecord Record(int id, string tag, string pressure) => new()
    {
        TableName = "dbo.Valves",
        Key = $"legacy-{id}",
        Values = new JsonObject
        {
            ["Record"] = JsonValue.Create(id),
            ["Tag"] = JsonValue.Create(tag),
            ["Pressure"] = JsonNode.Parse(pressure)
        }
    };

    private sealed class TestWorkspace : IProjectWorkspace
    {
        private readonly IReadOnlyList<ClassDefinition> _classes;
        private readonly IReadOnlyList<ProjectTableSchema> _schemas;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<ProjectRecord>> _records;

        public TestWorkspace(
            IReadOnlyList<ClassDefinition> classes,
            IReadOnlyList<ProjectTableSchema> schemas,
            IReadOnlyDictionary<string, IReadOnlyList<ProjectRecord>> records)
        {
            _classes = classes;
            _schemas = schemas;
            _records = records;
        }

        public string DisplayName => "Test";
        public bool IsOffline => true;
        public bool IsReadOnly => false;
        public long Revision => 0;
        public bool FailTableRead { get; set; }
        public JsonObject GraphRelations { get; init; } = new();

        public Task<IReadOnlyList<ProjectTableSchema>> GetTablesAsync(CancellationToken cancellationToken = default)
        {
            if (FailTableRead) throw new InvalidOperationException("read failed");
            return Task.FromResult(_schemas);
        }

        public Task<ProjectTableSchema?> GetSchemaAsync(string tableName, CancellationToken cancellationToken = default) =>
            Task.FromResult(_schemas.FirstOrDefault(x => x.TableName == tableName));

        public Task<ProjectTableSchema?> RefreshSchemaAsync(string tableName, CancellationToken cancellationToken = default) =>
            GetSchemaAsync(tableName, cancellationToken);

        public Task<ProjectRecordPage> ReadPageAsync(ProjectReadQuery query, CancellationToken cancellationToken = default)
        {
            var rows = _records.TryGetValue(query.TableName, out var found) ? found : Array.Empty<ProjectRecord>();
            return Task.FromResult(new ProjectRecordPage
            {
                Items = rows,
                PageNumber = query.PageNumber,
                PageSize = query.PageSize,
                TotalCount = rows.Count
            });
        }

        public Task<int> CountAsync(ProjectReadQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(_records.TryGetValue(query.TableName, out var rows) ? rows.Count : 0);

        public Task CreateRecordAsync(string tableName, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateRecordsAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateRecordAsync(string tableName, IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object?> keyValues, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteRecordsAsync(string tableName, IReadOnlyList<string> keyColumns, IReadOnlyList<IReadOnlyDictionary<string, object?>> keyValues, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ClassDefinition>> GetClassDefinitionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_classes);

        public Task SaveClassDefinitionsAsync(IReadOnlyList<ClassDefinition> definitions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonObject> GetGraphRelationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GraphRelations);

        public Task SaveGraphRelationsAsync(JsonObject document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
