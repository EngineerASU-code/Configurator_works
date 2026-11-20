#nullable enable
using Configurator.Core.Architecture;
using Configurator.Core.Projects;

namespace Configurator.Core.Compare;

public enum CompareStatus { Equal, OnlyA, OnlyB, Changed, Ambiguous, NotComparable, Error }

public sealed record CompareSource(
    IProjectWorkspace Workspace,
    string? ProjectId,
    string? Origin,
    DatabaseSchema? PhysicalSchema,
    string? PackagePath = null,
    string? PackageFingerprint = null,
    bool CanWrite = false,
    string? MetadataIssue = null)
{
    internal ConfiguratorProject? OfflineProject { get; init; }
    internal Func<string, CancellationToken, Task<IReadOnlyList<ProjectRecord>>>? ReadUnkeyedTableAsync { get; init; }
}

public sealed record CompareTable(
    ProjectTableSchema Schema,
    ClassDefinition? Class,
    IReadOnlySet<string> DependencyColumns,
    bool HasUnresolvedDependencies,
    string? MappingRole = null,
    string? MetadataFingerprint = null);

public sealed record CompareRecordPair(
    CompareTable? A,
    CompareTable? B,
    ProjectRecord? RecordA,
    ProjectRecord? RecordB);

public sealed record CompareDifference(
    string Title,
    string? A,
    string? B,
    CompareStatus Status,
    IReadOnlyList<CompareDifference>? Children = null,
    CompareRecordPair? Pair = null,
    string? Column = null,
    string? Issue = null);

public sealed record CompareResult(
    IReadOnlyList<CompareDifference> Architecture,
    IReadOnlyList<CompareDifference> Objects,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);
