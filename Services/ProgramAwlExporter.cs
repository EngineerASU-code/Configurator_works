using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Configurator;

internal static class ProgramAwlExporter
{
    public static string Generate(DataRow program, DataRow layout, DataTable defaults,
        DataTable transitions, DataTable steps, DataRow defaultRecipe)
    {
        int record = Number(program, "Record");
        int db = Range(Number(program, "RecipeDB_Number"), 1, short.MaxValue, "RecipeDB_Number");
        string name = "Prog_" + Format(record);
        int programParameters = Capacity("ProgParam_MaxCount");
        int programFunctions = Capacity("ProgFunction_MaxCount");
        int programOptions = Capacity("ProgOption_MaxCount");
        int stepCount = Capacity("Step_MaxCount");
        int stepParameters = Capacity("StepParam_MaxCount");
        int stepFunctions = Capacity("StepFunction_MaxCount");
        int stepOptions = Capacity("StepOption_MaxCount");
        var values = defaults.Rows.Cast<DataRow>()
            .OrderBy(row => Number(row, "Step_Number")).ThenBy(row => Number(row, "Type"))
            .ThenBy(row => Number(row, "Record")).ToArray();
        var recipe = values.Where(row => Number(row, "UseRecipes") > 0).ToArray();
        var stepDefinitions = steps.Rows.Cast<DataRow>()
            .ToDictionary(row => (Number(row, "PLC_Class_Number"), Number(row, "Record")));
        var output = new StringBuilder();
        void Line(string text = "") => output.Append(text).Append("\r\n");
        int Capacity(string column) => Range(Number(layout, column), 1, short.MaxValue, column);
        void ArrayField(string field, int size, string type) => Line($"    {field} : ARRAY  [1 .. {Format(size)} ] OF {type} ;");

        Line($"DATA_BLOCK DB{Format(db)}");
        Line($"TITLE = {name}");
        Line("{ S7_Optimized_Access := 'FALSE' }");
        Line("AUTHOR:  GASS");
        Line("FAMILY: Prog");
        Line($"NAME: {name}");
        Line("VERSION : 0.1");
        Line("NON_RETAIN");
        Line();
        Line("  STRUCT");
        Line("   Prog : STRUCT");
        foreach (string field in new[] { "Seq_Number", "Recipe_Number", "Matrix_Number", "Product_Number" })
            Line($"    {field} : INT ;");
        ArrayField("Param_Setpoint", programParameters, "REAL");
        ArrayField("Func_Setpoint", programFunctions, "REAL");
        ArrayField("Option_Setpoint", programOptions, "BOOL");
        Line("   END_STRUCT ;");
        Line($"   Step : ARRAY  [1 .. {Format(stepCount)} ] OF STRUCT");
        Line("    FB_Number : INT ;");
        Line("    Step_RunTime_Setpoint : DINT ;");
        Line("    Step_MonTime_Setpoint : DINT ;");
        ArrayField("Param_Setpoint", stepParameters, "REAL");
        ArrayField("Func_Setpoint", stepFunctions, "REAL");
        ArrayField("Option_Setpoint", stepOptions, "BOOL");
        ArrayField("Transition", 8, "INT");
        Line("    TransitionPrev_CondMask : WORD ;");
        Line("    TransitionNext_CondMask : WORD ;");
        for (int i = 1; i <= 8; i++) Line($"    Transition{i}_CondMask : WORD ;");
        Line("   END_STRUCT ;");
        Line();
        if (recipe.Length > 0)
        {
            Line("   Recipe : STRUCT");
            Line("    Recipe_Number : INT ;");
            Line("    Product_Number : INT ;");
            for (int i = 0; i < recipe.Length; i++)
            {
                Line($"    Param_{Format(i + 1, "D3")} : STRUCT");
                Line("     Step_Number : BYTE ;");
                Line("     Param_Type : BYTE ;");
                Line("     Param_Number : BYTE ;");
                Line($"     Param_Setpoint : {SetpointType(Number(recipe[i], "Type"))} ;");
                Line("    END_STRUCT ;");
            }
            Line("   END_STRUCT ;");
        }
        Line("  END_STRUCT ;");
        Line("BEGIN");
        Line("   Prog.Seq_Number := 1;");
        Line("   Prog.Recipe_Number := 0;");
        Line("   Prog.Matrix_Number := 0;");
        Line("   Prog.Product_Number := 0;");
        foreach (var row in values)
        {
            int step = Range(Number(row, "Step_Number"), 0, stepCount, "Step_Number");
            int type = Number(row, "Type");
            int index = Number(row, "Record");
            bool programValue = step == 0;
            string Index(int capacity) => Format(Range(index, 1, capacity, "Record"));
            string field = type switch
            {
                -2 when !programValue => "Step_MonTime_Setpoint",
                -1 when !programValue => "Step_RunTime_Setpoint",
                0 => $"Param_Setpoint[{Index(programValue ? programParameters : stepParameters)}]",
                >= 1 and <= 9 => $"Func_Setpoint[{Index(programValue ? programFunctions : stepFunctions)}]",
                10 => $"Option_Setpoint[{Index(programValue ? programOptions : stepOptions)}]",
                _ => throw new InvalidOperationException($"Некорректный тип параметра AWL {type} для шага {step}.")
            };
            string prefix = programValue ? "Prog" : $"Step[{Format(step)}]";
            Line($"   {prefix}.{field} := {Setpoint(row, type)};");
        }
        foreach (var row in transitions.Rows.Cast<DataRow>().OrderBy(row => Number(row, "SeqStep_Number")))
        {
            int step = Range(Number(row, "SeqStep_Number"), 1, stepCount, "SeqStep_Number");
            var key = (Number(row, "PLC_Class_Step_Number"), Number(row, "Step_Record"));
            if (!stepDefinitions.TryGetValue(key, out var definition))
                throw new InvalidOperationException($"Не найдено определение шага {step} для экспорта AWL.");
            string prefix = $"   Step[{Format(step)}].";
            Line($"{prefix}FB_Number := {Format(Range(Number(definition, "FC_Number"), 0, short.MaxValue, "FC_Number"))};");
            Line($"{prefix}TransitionPrev_CondMask := {Word(Number(row, "TransPrev_CondMask"))};");
            Line($"{prefix}TransitionNext_CondMask := {Word(Number(row, "TransNext_CondMask"))};");
            for (int i = 1; i <= 8; i++)
            {
                string destination = $"TransDest_{i:D2}";
                Line($"{prefix}Transition[{i}] := {Format(Range(Number(row, destination), short.MinValue, short.MaxValue, destination))};");
                Line($"{prefix}Transition{i}_CondMask := {Word(Number(row, destination + "_CondMask"))};");
            }
        }
        if (recipe.Length > 0)
        {
            Line("    Recipe.Recipe_Number := 0;");
            for (int i = 0; i < recipe.Length; i++)
            {
                var row = recipe[i];
                int type = Number(row, "Type");
                string prefix = $"     Recipe.Param_{Format(i + 1, "D3")}.";
                Line($"{prefix}Step_Number := B#16#{Format(Range(Number(row, "Step_Number"), 0, 255, "Step_Number"), "X")};");
                Line($"{prefix}Param_Number := B#16#{Format(Range(Number(row, "Record"), 0, 255, "Record"), "X")};");
                int recipeType = type is >= 1 and <= 9 ? 1 : unchecked((byte)type);
                Line($"{prefix}Param_Type := B#16#{Format(recipeType, "X2")};");
                Line($"{prefix}Param_Setpoint := {Setpoint(row, type)};");
            }
            if (defaultRecipe != null)
                Line($"    Recipe.Product_Number := {Format(Range(Number(defaultRecipe, "Type"), short.MinValue, short.MaxValue, "Type"))};");
        }

        return output.Append("END_DATA_BLOCK").ToString();
    }

    private static string SetpointType(int type) => type switch
    {
        -2 or -1 => "DINT",
        >= 0 and <= 9 => "REAL",
        10 => "BOOL",
        _ => throw new InvalidOperationException($"Неизвестный тип параметра AWL: {type}.")
    };

    private static string Setpoint(DataRow row, int type)
    {
        double value = Convert.ToDouble(row["Setpoint"], CultureInfo.InvariantCulture);
        if (!double.IsFinite(value)) throw new InvalidOperationException("Setpoint должен быть конечным числом.");
        return SetpointType(type) switch
        {
            "DINT" when value == Math.Truncate(value) && value >= int.MinValue && value <= int.MaxValue
                => "L#" + Format((int)value),
            "DINT" => throw new InvalidOperationException("Setpoint времени выходит за пределы DINT."),
            "BOOL" => value == 0 ? "FALSE" : "TRUE",
            _ when Math.Abs(value) <= float.MaxValue => value.ToString("0.000000E+000", CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Setpoint выходит за пределы REAL.")
        };
    }

    private static string Word(int value)
    {
        Range(value, short.MinValue, ushort.MaxValue, "CondMask");
        return "W#16#" + Format(value & 255, "X2") + Format((value >> 8) & 255, "X2");
    }

    private static int Number(DataRow row, string column) => Convert.ToInt32(row[column], CultureInfo.InvariantCulture);
    private static string Format(int number, string format = "D") => number.ToString(format, CultureInfo.InvariantCulture);
    private static int Range(int value, int min, int max, string field) => value >= min && value <= max
        ? value : throw new InvalidOperationException($"{field}={value} вне диапазона {min}…{max} для AWL.");
}
