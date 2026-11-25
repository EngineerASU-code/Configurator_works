using System.IO;
using System.Text.Json;

namespace Configurator.Infrastructure.Database;

internal sealed record LocalQuery(Guid Id, string Name, string Sql);

internal class LocalSqlLibrary(string libraryPath)
{
    public LocalSqlLibrary() : this(Path.Combine(AppContext.BaseDirectory, "SqlQueries.json")) { }

    public List<LocalQuery> Read()
    {
        try
        {
            using var file = new FileStream(libraryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<List<LocalQuery>>(file)
                ?? throw new InvalidDataException("Пустой файл библиотеки запросов.");
        }
        catch (FileNotFoundException) { return new(); }
    }

    public LocalQuery Save(LocalQuery original, string name, string sql)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("Задайте имя запроса и введите SQL.");

        using var writerLock = new FileStream(libraryPath + ".lock", FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        var queries = Read();
        if (queries.Any(q => q.Id != original?.Id && string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Запрос с таким именем уже существует.");

        var saved = new LocalQuery(original?.Id ?? Guid.NewGuid(), name, sql);
        if (original == null) queries.Add(saved);
        else
        {
            int index = queries.IndexOf(original);
            if (index < 0) throw new InvalidOperationException("Запрос изменился. Обновите список и откройте его снова.");
            queries[index] = saved;
        }

        var temporary = libraryPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(queries));
            File.Move(temporary, libraryPath, true);
        }
        finally { File.Delete(temporary); }
        return saved;
    }
}
