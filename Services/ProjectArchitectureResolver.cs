using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configurator.Application.Architecture;
using Configurator.Core.Architecture;

namespace Configurator;

/// <summary>
/// Resolves the physical storage of system labels from the connected database.
/// SystemClassCatalog intentionally contains labels only; this service supplies the
/// actual table mapping for the current project without changing the database.
/// </summary>
public sealed class ProjectArchitectureResolver
{
    public static string GetProjectIdentity(string server, string database)
        => $"{server?.Trim()}\0{database?.Trim()}";

    private static readonly IReadOnlyDictionary<string, string[]> Candidates =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Valve"] = new[] { "dbo.Valves", "dbo.Valve" },
            ["DSV"] = new[] { "dbo.Valve2S", "dbo.Valves2S", "dbo.Valve2s" },
            ["Motor"] = new[] { "dbo.Motors", "dbo.Motor" },
            ["DI"] = new[] { "dbo.DiscretIN", "dbo.DiscretIn" },
            ["DO"] = new[] { "dbo.DiscretOUT", "dbo.DiscretOut" },
            ["AI"] = new[] { "dbo.AnaIn", "dbo.AnaIN", "dbo.AnalogIn" },
            ["AO"] = new[] { "dbo.AnaOUT", "dbo.AnaOut", "dbo.AnalogOut" },
            ["PID"] = new[] { "dbo.Reglers", "dbo.Regler" },
            ["Status"] = new[] { "dbo.Statuses", "dbo.Status" },
            ["Program"] = new[] { "dbo.Prog", "dbo.Programs", "dbo.Program" },
            ["Step"] = new[] { "dbo.Prog_Step", "dbo.ProgramStep" },
            ["Matrix"] = new[] { "dbo.Matrix", "dbo.Matrices" }
        };

    public static int? ResolveSystemClassNumber(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return null;

        var normalized = NormalizeTableName(tableName);
        foreach (var definition in SystemClassCatalog.CreateDefaults())
            if (Candidates.TryGetValue(definition.Name, out var candidates) &&
                candidates.Any(candidate => NormalizeTableName(candidate).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                return definition.ClassNumber;

        return null;
    }

    private static string NormalizeTableName(string value)
        => (value ?? string.Empty).Trim().Replace("[", "").Replace("]", "");

    public async Task ResolveAsync(
        ClassArchitectureService architecture,
        DatabaseService db,
        CancellationToken ct = default,
        Func<bool> isCurrentProject = null)
    {
        if (architecture == null) throw new ArgumentNullException(nameof(architecture));
        if (db == null) throw new ArgumentNullException(nameof(db));

        await ResolveIfCurrentAsync(async () =>
        {
            var tables = await db.GetTableListAsync(ct);
            return tables.Rows.Cast<System.Data.DataRow>()
                .Select(r => $"{Convert.ToString(r["TABLE_SCHEMA"])}.{Convert.ToString(r["TABLE_NAME"])}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }, isCurrentProject ?? (() => true), names =>
        {
            foreach (var cls in architecture.Classes)
            {
                ct.ThrowIfCancellationRequested();
                string table = ResolveTable(cls, names);
                var mapping = cls.StorageMappings.FirstOrDefault(x => string.Equals(x?.Role, "primary", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(table))
                {
                    if (cls.IsSystem && mapping != null) cls.StorageMappings.Remove(mapping);
                    continue;
                }

                if (mapping == null)
                {
                    mapping = new StorageMapping { Role = "primary" };
                    cls.StorageMappings.Add(mapping);
                }
                mapping.TableName = table;
            }
        });
    }

    internal static async Task<bool> ResolveIfCurrentAsync<T>(Func<Task<T>> load, Func<bool> isCurrentProject, Action<T> apply)
    {
        var result = await load();
        if (!isCurrentProject()) return false;
        apply(result);
        return true;
    }

    private static string ResolveTable(ClassDefinition cls, IReadOnlyList<string> tables)
    {
        if (cls == null) return null;

        if (!cls.IsSystem && !string.IsNullOrWhiteSpace(cls.PrimaryStorage?.TableName))
        {
            var exact = tables.FirstOrDefault(x => x.Equals(cls.PrimaryStorage.TableName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        if (Candidates.TryGetValue(cls.Name, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                var exact = tables.FirstOrDefault(x => x.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
        }

        var shortName = tables
            .Select(x => new { Full = x, Name = x.Contains('.') ? x[(x.IndexOf('.') + 1)..] : x })
            .ToList();
        var direct = shortName.FirstOrDefault(x => x.Name.Equals(cls.Name, StringComparison.OrdinalIgnoreCase));
        if (direct != null) return direct.Full;

        var plural = cls.Name.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? cls.Name : cls.Name + "s";
        var pluralMatch = shortName.FirstOrDefault(x => x.Name.Equals(plural, StringComparison.OrdinalIgnoreCase));
        return pluralMatch?.Full;
    }
}
