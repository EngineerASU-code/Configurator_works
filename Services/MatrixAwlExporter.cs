#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Configurator;

public static class MatrixAwlExporter
{
    private static readonly IReadOnlyDictionary<char, byte> CommandBits = new Dictionary<char, byte>
    {
        ['A'] = 1, ['R'] = 1, ['N'] = 1,
        ['D'] = 2, ['P'] = 2, ['F'] = 2,
        ['L'] = 4, ['C'] = 4,
        ['U'] = 8, ['B'] = 16, ['V'] = 32, ['W'] = 64, ['E'] = 128
    };

    public static IReadOnlyList<string> CommandOptions { get; } = new[] { string.Empty }
        .Concat(CommandBits.Keys.Select(code => code.ToString()))
        .Concat(new[] { "DL", "DU", "DW", "DE" }).ToArray();

    public static string Generate(MatrixAwlSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Record < 0) throw new InvalidOperationException("Record матрицы должен быть неотрицательным.");
        if (snapshot.DbNumber <= 0) throw new InvalidOperationException("Для экспорта требуется положительный DB_Number.");

        var positions = snapshot.Positions.OrderBy(x => x).ToArray();
        if (positions.Length == 0 || positions.Any(x => x <= 0))
            throw new InvalidOperationException("В матрице должна быть хотя бы одна позиция с положительным номером.");
        if (positions.Distinct().Count() != positions.Length)
            throw new InvalidOperationException("Номера позиций должны быть уникальными.");
        int positionCount = positions[^1];

        var commands = snapshot.Commands.OrderBy(x => x.Number).ToArray();
        if (commands.Any(x => x.Number <= 0) || commands.Select(x => x.Number).Distinct().Count() != commands.Length)
            throw new InvalidOperationException("Номера команд должны быть уникальными и положительными.");
        int commandCount = commands.Length == 0 ? 0 : commands[^1].Number;
        var commandIndexes = commands.ToDictionary(x => x.Number, x => x.Number);
        var positionSet = positions.ToHashSet();
        var encodedCells = new Dictionary<(int Command, int Position), byte>();
        foreach (var cell in snapshot.CommandCells)
        {
            if (!commandIndexes.TryGetValue(cell.CommandNumber, out int commandIndex) || !positionSet.Contains(cell.Position))
                throw new InvalidOperationException($"Ячейка команды ссылается на отсутствующую команду или позицию ({cell.Position}, {cell.CommandNumber}).");
            if (!encodedCells.TryAdd((commandIndex, cell.Position), EncodeCommand(cell.Code)))
                throw new InvalidOperationException($"Для позиции {cell.Position} и команды {cell.CommandNumber} найдено несколько значений.");
        }

        var groups = snapshot.DeviceGroups.OrderBy(x => x.Number).ToArray();
        if (groups.Any(x => x.Number <= 0) || groups.Select(x => x.Number).Distinct().Count() != groups.Length)
            throw new InvalidOperationException("Номера групп устройств должны быть уникальными и положительными.");
        int groupCount = groups.Length == 0 ? 0 : groups[^1].Number;
        var groupsByNumber = groups.ToDictionary(group => group.Number);
        foreach (var group in groups)
        {
            if (group.DevicesByPosition.Keys.Any(position => !positionSet.Contains(position)))
                throw new InvalidOperationException($"Группа устройств {group.Number} содержит ссылку на отсутствующую позицию.");
            if (group.DevicesByPosition.Values.Any(value => value < 0 || value > int.MaxValue))
                throw new InvalidOperationException($"Группа устройств {group.Number} содержит ссылку вне диапазона DINT.");
        }

        byte[] delayOn = new byte[commandCount];
        byte[] delayOff = new byte[commandCount];
        byte[] condition = new byte[commandCount];
        foreach (var command in commands)
        {
            delayOn[command.Number - 1] = ToByte(command.DelayOn, "DelayON", command.Number);
            delayOff[command.Number - 1] = ToByte(command.DelayOff, "DelayOFF", command.Number);
            condition[command.Number - 1] = ToByte(command.Condition, "Condition", command.Number);
        }
        var output = new StringBuilder();
        void Line(string value = "") => output.Append(value).Append("\r\n");
        string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        string ByteValue(byte value) => $"B#16#{value:X}";
        long DeviceValue(int group, int position) => groupsByNumber.TryGetValue(group, out var item)
            && item.DevicesByPosition.TryGetValue(position, out long value) ? value : 0;

        Line($"DATA_BLOCK DB{Number(snapshot.DbNumber)}");
        Line($"TITLE = Matrix_{Number(snapshot.Record)}");
        Line("{ S7_Optimized_Access := 'FALSE' }");
        Line("AUTHOR:  GASS");
        Line("FAMILY: Matrix");
        Line($"NAME: Matrix{Number(snapshot.Record)}");
        Line("VERSION : 0.1");
        Line("NON_RETAIN");
        Line("  STRUCT");
        Line($"   Pos_count : INT  := {Number(positionCount)};");
        Line($"   Cmd_count : INT  := {Number(commandCount)};");
        Line("   spare1 : INT  := 0;");
        Line("   spare2 : DINT := L#0;");
        Line($"   DelayON : ARRAY  [1 .. {Number(commandCount)} ] OF BYTE ;");
        Line($"   DelayOFF : ARRAY  [1 .. {Number(commandCount)} ] OF BYTE ;");
        Line($"   Condition : ARRAY  [1 .. {Number(commandCount)} ] OF BYTE ;");
        Line($"   CMD : ARRAY  [1 .. {Number(commandCount)}, 1 .. {Number(positionCount)} ] OF BYTE ;");
        Line($"   DevList : ARRAY  [1 .. {Number(groupCount)}, 1 .. {Number(positionCount)} ] OF DINT ;");
        Line("  END_STRUCT ;");
        Line("BEGIN");
        Line($"   Pos_count := {Number(positionCount)};");
        Line($"   Cmd_count := {Number(commandCount)};");
        Line("   spare1 := 0;");
        Line("   spare2 := L#0;");
        for (int i = 0; i < commandCount; i++)
            Line($"   DelayON[{Number(i + 1)}] := {ByteValue(delayOn[i])};");
        for (int i = 0; i < commandCount; i++)
            Line($"   DelayOFF[{Number(i + 1)}] := {ByteValue(delayOff[i])};");
        for (int i = 0; i < commandCount; i++)
            Line($"   Condition[{Number(i + 1)}] := {ByteValue(condition[i])};");
        for (int command = 1; command <= commandCount; command++)
            for (int position = 1; position <= positionCount; position++)
                Line($"   CMD[{Number(command)}, {Number(position)}] := {ByteValue(encodedCells.GetValueOrDefault((command, position)))};");
        for (int group = 1; group <= groupCount; group++)
            for (int position = 1; position <= positionCount; position++)
                Line($"   DevList[{Number(group)}, {Number(position)}] := L#{checked(DeviceValue(group, position)).ToString(CultureInfo.InvariantCulture)};");
        Line("END_DATA_BLOCK");
        return output.ToString();
    }

    public static byte EncodeCommand(string? code)
    {
        byte result = 0;
        foreach (char letter in (code ?? string.Empty).Where(c => !char.IsWhiteSpace(c)))
        {
            char normalized = char.ToUpperInvariant(letter);
            if (!CommandBits.TryGetValue(normalized, out byte bit))
                throw new InvalidOperationException($"Неизвестный код команды «{letter}». Допустимы A/R/N, D/P/F, L/C, U, B, V, W, E.");
            result |= bit;
        }
        return result;
    }

    private static byte ToByte(object? value, string field, int commandNumber)
    {
        if (value == null || value == DBNull.Value || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))) return 0;
        if (value is bool flag) return flag ? (byte)1 : (byte)0;
        if (!byte.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte result))
            throw new InvalidOperationException($"{field} команды {commandNumber} должен быть целым числом от 0 до 255.");
        return result;
    }
}
