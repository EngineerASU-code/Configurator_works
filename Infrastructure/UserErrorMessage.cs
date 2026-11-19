using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Configurator.Infrastructure;

/// <summary>Formats exceptions for users while retaining technical details in the diagnostic log.</summary>
public static class UserErrorMessage
{
    public static string Format(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        SessionTestLog.Trace("ОШИБКА", exception.ToString());
        System.Diagnostics.Debug.WriteLine(exception);
        for (Exception current = exception; current != null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql.Number switch
                {
                    2601 or 2627 => "Запись с таким значением уже существует. Измените его и повторите попытку.",
                    547 => "Операция нарушает связь между записями. Проверьте связанные данные.",
                    515 => "Не заполнено обязательное поле. Проверьте данные и повторите попытку.",
                    207 or 208 => "Структура базы данных не соответствует ожидаемой. Проверьте выбранную базу.",
                    18456 => "Не удалось войти в SQL Server. Проверьте данные для входа.",
                    -2 => "Время ожидания SQL Server истекло. Проверьте подключение и повторите попытку.",
                    _ => "Не удалось выполнить операцию с базой данных."
                };
            }
        }
        if (Regex.IsMatch(exception.Message, "[А-Яа-яЁё]") && !Regex.IsMatch(exception.Message, "[A-Za-z]{3,}"))
            return exception.Message;

        return exception switch
        {
            UnauthorizedAccessException => "Нет доступа к файлу или папке. Проверьте права доступа.",
            FileNotFoundException => "Не удалось найти файл. Проверьте путь и повторите попытку.",
            DirectoryNotFoundException => "Не удалось найти папку. Проверьте путь и повторите попытку.",
            IOException => "Не удалось выполнить операцию с файлом.",
            _ => "Не удалось выполнить операцию."
        };
    }
}
