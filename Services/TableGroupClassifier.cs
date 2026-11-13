namespace Configurator;

// Internal semantic labels used by dependency-resolution logic; they do not rename SQL tables.
public enum SemanticTableGroup { Unknown, ClassObjects, ClassTypes, CoreLookups, ProgramSteps, Matrix, RelationshipContext }

public static class TableGroupClassifier
{
    private static readonly HashSet<string> ClassObjects = new(StringComparer.OrdinalIgnoreCase)
    { "AnaIN", "AnaOUT", "DiscretIN", "DiscretOUT", "Valve", "Valve2S", "Motors", "Reglers", "PLC", "Areas" };
    private static readonly HashSet<string> ClassTypes = new(StringComparer.OrdinalIgnoreCase)
    { "AnaInType", "AnaOutType", "DiscretInType", "DiscretOutType", "ValveType", "Valve2SType", "MotorType", "ReglerType", "PLC_Types", "ProgTypes", "Prog_StepType", "MatrixType" };
    private static readonly HashSet<string> ProgramSteps = new(StringComparer.OrdinalIgnoreCase)
    { "Prog", "Program", "Programs", "Prog_Step", "ProgramStep", "Prog_StepParam", "StepParam", "Prog_Record" };
    private static readonly HashSet<string> Matrix = new(StringComparer.OrdinalIgnoreCase)
    { "Matrix", "Matrices", "Matrix_List", "Matrix_Commands", "Matrix_CommandList", "Matrix_DeviceList", "Matrix_Devices" };
    private static readonly HashSet<string> RelationshipContext = new(StringComparer.OrdinalIgnoreCase)
    { "Device_Global_Class", "Device_PLC", "Device_PLC_Class_Number", "Device_Record", "PLC_Class_Number", "PLC_Class_Prog_Number" };

    public static SemanticTableGroup Classify(string tableName)
    {
        var name = (tableName ?? "").Trim();
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        if (ClassObjects.Contains(name)) return SemanticTableGroup.ClassObjects;
        if (ClassTypes.Contains(name)) return SemanticTableGroup.ClassTypes;
        if (ProgramSteps.Contains(name)) return SemanticTableGroup.ProgramSteps;
        if (Matrix.Contains(name)) return SemanticTableGroup.Matrix;
        if (RelationshipContext.Contains(name)) return SemanticTableGroup.RelationshipContext;
        return SemanticTableGroup.Unknown;
    }
}
