using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Configurator;

public sealed partial class ProgramManagementRepository
{
    internal async Task<string> BuildAwlAsync(ProgramContext context, CancellationToken ct = default)
    {
        var programScope = Context(context.PlcValue, context.ClassNumberValue);
        programScope["Record"] = context.RecordValue;
        var program = RequireSingle(await GetScopedTableAsync(ProgramTable, programScope, ct), ProgramTable);
        const string typesTable = "dbo.Prog_Types";
        var layout = RequireSingle(await GetScopedTableAsync(typesTable,
            new Dictionary<string, object> { ["Record"] = program["Type"] }, ct), typesTable);

        var sequenceContext = new ProgramContext
        {
            PlcValue = context.PlcValue,
            ClassNumberValue = context.ClassNumberValue,
            RecordValue = context.RecordValue,
            SeqRecordValue = 1
        };
        var scope = ProgramScope(sequenceContext, includeSequence: true);
        var defaultsScope = new Dictionary<string, object>(scope) { ["Recipe_Record"] = 0 };
        const string valuesTable = "dbo.Prog_Recipes";
        var defaults = await GetScopedTableAsync(valuesTable, defaultsScope, ct, readAll: true);
        if (defaults.Columns.Count == 0)
            throw new InvalidOperationException($"В проекте отсутствует {valuesTable}.");

        DataRow defaultRecipe = null;
        if (defaults.Rows.Cast<DataRow>().Any(row => Convert.ToInt32(row["UseRecipes"], CultureInfo.InvariantCulture) > 0))
        {
            var headerScope = new Dictionary<string, object>(scope) { ["Record"] = 0 };
            var headers = await GetScopedTableAsync("dbo.Prog_Recipe", headerScope, ct);
            defaultRecipe = headers.Rows.Cast<DataRow>().SingleOrDefault();
        }
        var transitions = await GetScopedTableAsync(TransitionTable, scope, ct, readAll: true);
        if (transitions.Columns.Count == 0)
            throw new InvalidOperationException($"В проекте отсутствует {TransitionTable}.");
        var steps = await GetReferencedStepsAsync(sequenceContext, ct);
        return ProgramAwlExporter.Generate(program, layout, defaults, transitions, steps, defaultRecipe);
    }

    private static DataRow RequireSingle(DataTable data, string table) => data.Rows.Count == 1
        ? data.Rows[0] : throw new InvalidOperationException($"В {table} не найдена единственная запись для экспорта AWL.");
}
