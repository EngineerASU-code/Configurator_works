using System;
using System.Data;
using System.Globalization;

namespace Configurator;

/// <summary>Projects the ten transition masks into a grid without changing untouched values.</summary>
public static class ProgramTransitionConditions
{
    private static string MaskField(int index) => index switch
    {
        0 => "TransPrev_CondMask",
        1 => "TransNext_CondMask",
        _ => $"TransDest_{index - 1:00}_CondMask"
    };

    public static DataTable Create(DataRow transition)
    {
        var grid = new DataTable();
        grid.Columns.Add("Переход", typeof(string));
        grid.Columns.Add("Назначение", typeof(int));
        for (int bit = 0; bit < 16; bit++) grid.Columns.Add($"C{bit:00}", typeof(bool));
        for (int i = 0; i < 10; i++)
        {
            string maskName = MaskField(i);
            if (!transition.Table.Columns.Contains(maskName))
                throw new InvalidOperationException($"Нет поля {maskName}.");
            long mask = ReadInteger(transition[maskName]);
            var row = grid.NewRow();
            row[0] = i == 0 ? "Prev" : i == 1 ? "Next" : $"Dest_{i - 1:00}";
            if (i >= 2)
            {
                string destination = $"TransDest_{i - 1:00}";
                if (!transition.Table.Columns.Contains(destination))
                    throw new InvalidOperationException($"Нет поля {destination}.");
                if (transition[destination] != DBNull.Value)
                    row[1] = Convert.ToInt32(transition[destination], CultureInfo.InvariantCulture);
            }
            for (int bit = 0; bit < 16; bit++) row[bit + 2] = (mask & (1L << bit)) != 0;
            grid.Rows.Add(row);
        }
        grid.AcceptChanges();
        return grid;
    }

    public static void Apply(DataTable grid, DataRow transition)
    {
        if (grid.Rows.Count != 10) throw new InvalidOperationException("Изменён состав таблицы условий.");
        var changes = new System.Collections.Generic.Dictionary<string, object>();
        for (int i = 0; i < 10; i++)
        {
            var row = grid.Rows[i];
            if (row.RowState != DataRowState.Modified) continue;
            string maskName = MaskField(i);
            long mask = ReadInteger(transition[maskName]);
            bool maskChanged = false;
            for (int bit = 0; bit < 16; bit++)
            {
                int col = bit + 2;
                bool oldValue = (bool)row[col, DataRowVersion.Original];
                bool newValue = (bool)row[col, DataRowVersion.Current];
                if (oldValue == newValue) continue;
                maskChanged = true;
                mask = newValue ? mask | (1L << bit) : mask & ~(1L << bit);
            }
            if (maskChanged) changes[maskName] = ConvertInteger(mask, transition.Table.Columns[maskName].DataType, true);
            if (i >= 2 && !Equals(row[1, DataRowVersion.Original], row[1, DataRowVersion.Current]))
            {
                string field = $"TransDest_{i - 1:00}";
                object value = row[1];
                changes[field] = value == DBNull.Value ? DBNull.Value : ConvertInteger((int)value, transition.Table.Columns[field].DataType, false);
            }
        }
        transition.BeginEdit();
        try
        {
            foreach (var change in changes) transition[change.Key] = change.Value;
            transition.EndEdit();
        }
        catch
        {
            transition.CancelEdit();
            throw;
        }
        grid.AcceptChanges();
    }

    private static long ReadInteger(object value)
        => value == DBNull.Value ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static object ConvertInteger(long value, Type type, bool mask)
    {
        if (type == typeof(short)) return mask ? unchecked((short)value) : checked((short)value);
        if (type == typeof(int)) return checked((int)value);
        if (type == typeof(long)) return value;
        throw new InvalidOperationException($"Неподдерживаемый тип поля условий: {type.Name}.");
    }
}
