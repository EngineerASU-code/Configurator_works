using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Configurator;

internal static class LocalRowAttempt
{
    internal const string Unknown = "Подтверждение отсутствует. Сверьте строку на сервере.";
    internal const string Rejected = "Сервер отклонил изменение. Данные этой попытки не записаны.";

    internal static void NoteRollback(string path, ChangeJournalEntry entry)
    {
        entry.Result = Rejected;
        try { Save(Path.GetDirectoryName(path)!, entry, replace: true); }
        catch (IOException) { /* The original durable attempt remains available. */ }
        catch (UnauthorizedAccessException) { }
    }

    internal static string Folder(string server, string database, string user)
    {
        var identity = string.Join("\0", Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant(),
            ConnectionSettings.ProjectServerName(server).ToUpperInvariant(), database.ToUpperInvariant(), user.ToUpperInvariant());
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Configurator", "RowAttempts", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    internal static string Save(string folder, ChangeJournalEntry entry, bool replace = false)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, entry.OperationId + ".attempt");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entry)), null, DataProtectionScope.CurrentUser);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, replace);
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static ChangeJournalEntry Read(string path) =>
        JsonConvert.DeserializeObject<ChangeJournalEntry>(Encoding.UTF8.GetString(ProtectedData.Unprotect(
            File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser))) ?? throw new InvalidDataException("Пустая попытка записи.");

    internal static string[] List(string folder) => Directory.Exists(folder)
        ? Directory.GetFiles(folder, "*.attempt").OrderBy(File.GetCreationTimeUtc).ToArray() : Array.Empty<string>();
}
