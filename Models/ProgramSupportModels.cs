#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace Configurator;

public sealed record ProgramSupportTableDefinition(
    string Id,
    string Caption,
    string TableName,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> ScopeColumns,
    IReadOnlyList<string> EditableColumns,
    IReadOnlyList<string> ProtectedColumns,
    bool ReadOnly = false,
    bool AllowInsert = false,
    bool AllowDelete = true,
    string? ReadOnlyReason = null);

public sealed class ProgramSupportTableData
{
    public required ProgramSupportTableDefinition Definition { get; init; }
    public required DataTable Rows { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlySet<string> IdentityColumns { get; init; }
    public required IReadOnlySet<string> ComputedColumns { get; init; }
    public required IReadOnlySet<string> DefaultColumns { get; init; }
    public bool CanWrite { get; init; }
    public string? WriteBlockReason { get; init; }
    public IReadOnlyList<ProgMatrixRow>? ProgMatrixRows { get; init; }
    public IReadOnlyList<MatrixListRow>? MatrixListRows { get; init; }
}

public sealed record ProgMatrixKey(int Record, int PLC, int PLC_Class_Number);

public sealed record ProgMatrixRow(
    ProgMatrixKey Key,
    int? Type,
    string? Name_L1,
    string? Name_L2,
    string? DescriptionL1,
    string? DescriptionL2);

public sealed record MatrixListKey(int Record, int PLC, int PLC_Class_Number);

public sealed record MatrixListRow(MatrixListKey Key, string Name_L1, string Name_L2, int? DB_Number);

public sealed record MatrixDeviceClassOption(int Number, string Name, string TableName);

public sealed record MatrixDevicePlcOption(int Number, string Caption);

public sealed record MatrixDeviceObjectOption(int Plc, int ClassNumber, int Record, string Caption);

public sealed record MatrixAwlCommand(int Number, object? DelayOn, object? DelayOff, object? Condition);

public sealed record MatrixAwlDeviceGroup(int Number, IReadOnlyDictionary<int, long> DevicesByPosition);

public sealed record MatrixAwlSnapshot(
    int Record,
    int DbNumber,
    IReadOnlyList<int> Positions,
    IReadOnlyList<MatrixAwlCommand> Commands,
    IReadOnlyList<(int Position, int CommandNumber, string? Code)> CommandCells,
    IReadOnlyList<MatrixAwlDeviceGroup> DeviceGroups);

public static class ProgramSupportCatalog
{
    private static readonly string[] ProgramScope = { "PLC", "PLC_Class_Prog_Number", "Prog_Record" };
    private static readonly string[] SequenceScope = { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record" };
    private static readonly string[] MatrixScope = { "PLC", "PLC_Class_Number" };
    private static readonly string[] MatrixRecordScope = { "PLC", "PLC_Class_Number", "Record" };

    public static IReadOnlyList<ProgramSupportTableDefinition> ProgramTables { get; } = new[]
    {
        new ProgramSupportTableDefinition("recipes", "Рецепты", "dbo.Prog_Recipe",
            new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record" }, SequenceScope,
            new[] { "Type", "Name_L1", "Name_L2", "Description_L1", "Description_L2" }, new[] { "Type" }, AllowInsert: true),
        new ProgramSupportTableDefinition("recipe-values", "Параметры рецептов", "dbo.Prog_Recipes",
            new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Recipe_Record", "Step_Number", "Type" }, SequenceScope,
            new[] { "Setpoint", "Mask", "UseRecipes" }, new[] { "Recipe_Record", "Step_Number", "Type" }, AllowDelete: true),
        new ProgramSupportTableDefinition("queues", "Очереди", "dbo.Prog_Queue",
            new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record" }, ProgramScope,
            new[] { "Type", "Name_L1", "Name_L2", "Description_L1", "Description_L2", "Use_Param1", "Use_Param2", "Use_Param3", "Use_Param4", "Param1_Name_L1", "Param1_Name_L2", "Param1_Type", "Param1_Unit", "Param1_SubType", "Param1_Max", "Param1_Min", "Param2_Name_L1", "Param2_Name_L2", "Param2_Type", "Param2_Unit", "Param2_SubType", "Param2_Max", "Param2_Min", "Param3_Name_L1", "Param3_Name_L2", "Param3_Type", "Param3_Unit", "Param3_SubType", "Param3_Max", "Param3_Min", "Param4_Name_L1", "Param4_Name_L2", "Param4_Type", "Param4_Unit", "Param4_SubType", "Param4_Max", "Param4_Min", "Use_Recipe", "Item_Count", "Use_Matrix" },
            new[] { "PLC_Class_Matrix_Number", "Matrix_Record" }, AllowInsert: true),
        new ProgramSupportTableDefinition("queue-selections", "Элементы очередей", "dbo.Prog_QueueSelections",
            new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Queue_Record" }, SequenceScope,
            new[] { "Param1", "Param2", "Param3", "Param4" }, new[] { "Queue_Record", "PLC_Class_Status_Number", "Status_Record", "PLC_Class_Matrix_Number", "Matrix_Record", "Recipe", "Matrix_DeviceList" }, AllowInsert: true),
        new ProgramSupportTableDefinition("softkey", "SoftKey", "dbo.Prog_Softkey",
            new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record" }, ProgramScope,
            new[] { "Name_L1", "Name_L2", "Description_L1", "Description_L2" }, Array.Empty<string>(), AllowInsert: true),
        new ProgramSupportTableDefinition("messages", "Сообщения", "dbo.Prog_Msg",
            new[] { "Record", "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Type" }, ProgramScope,
            new[] { "Text_L1", "Text_L2" }, Array.Empty<string>(), AllowInsert: true)
    };

    public static IReadOnlyList<ProgramSupportTableDefinition> MatrixTables { get; } = new[]
    {
        new ProgramSupportTableDefinition("program-matrix", "Конфигурация класса · Prog_Matrix", "dbo.Prog_Matrix",
            new[] { "Record", "PLC", "PLC_Class_Number" }, MatrixScope,
            new[] { "Type", "Name_L1", "Name_L2", "DescriptionL1", "DescriptionL2" }, Array.Empty<string>(), AllowInsert: true),
        new ProgramSupportTableDefinition("matrix-list", "Матрицы · Matrix_List", "dbo.Matrix_List",
            new[] { "Record", "PLC", "PLC_Class_Number" }, MatrixScope,
            new[] { "Name_L1", "Name_L2", "DB_Number" }, Array.Empty<string>(), AllowInsert: true),
        new ProgramSupportTableDefinition("matrix-command-list", "Команды · Matrix_CommandList", "dbo.Matrix_CommandList",
            new[] { "Record", "PLC", "PLC_Class_Number", "CmdNumber" }, MatrixRecordScope,
            new[] { "Name_L1", "Name_L2", "DelayON", "DelayOFF", "Condition" }, Array.Empty<string>(), AllowInsert: true),
        new ProgramSupportTableDefinition("matrix-commands", "Карта команд · Position × CmdNumber", "dbo.Matrix_Commands",
            new[] { "Record", "PLC", "PLC_Class_Number", "Position", "CmdNumber" }, MatrixRecordScope,
            new[] { "Cmd" }, Array.Empty<string>(), AllowInsert: true),
        new ProgramSupportTableDefinition("matrix-device-list", "Группы списков устройств", "dbo.Matrix_DeviceList",
            new[] { "Record", "PLC", "PLC_Class_Number", "DeviceList" }, MatrixRecordScope,
            new[] { "Name_L1", "Name_L2" }, Array.Empty<string>(), AllowInsert: true),
        new ProgramSupportTableDefinition("matrix-device-list-type", "Позиции и типы устройств", "dbo.Matrix_DeviceListType",
            new[] { "Record", "PLC", "PLC_Class_Number", "Position" }, MatrixRecordScope,
            new[] { "Name_L1", "Name_L2", "Device_Global_Class" }, Array.Empty<string>(), AllowInsert: true),
        new ProgramSupportTableDefinition("matrix-devices", "Назначения объектов", "dbo.Matrix_Devices",
            new[] { "Record", "PLC", "PLC_Class_Number", "DeviceList", "Position" }, MatrixRecordScope,
            new[] { "Device_PLC", "Device_PLC_Class_Number", "Device_Record" }, Array.Empty<string>(), AllowInsert: true)
    };
}
