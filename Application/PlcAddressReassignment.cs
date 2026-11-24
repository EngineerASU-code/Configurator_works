#nullable enable
using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using Configurator.Core.Projects;
using Configurator.Core.Architecture;
using Configurator.Core.Workspace;
using Microsoft.Data.SqlClient;

namespace Configurator;

/// <summary>Rebuilds the address map while holding SQL locks through the update.</summary>
internal static class PlcAddressReassignment
{
    internal static async Task ExecuteAsync(string connectionString, IProjectArchitectureDataSource architecture,
        AddressOwner expected, string newAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (string.IsNullOrWhiteSpace(newAddress)) throw new InvalidOperationException("Укажите новый адрес.");
        ProjectWriteGuard.EnsureDataTableWritable(expected.TableName);
        await using var connection = SqlProviderGuard.CreateConnection(connectionString);
        await SqlProviderGuard.OpenConnectionAsync(connection, ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        try
        {
            var names = new List<string>();
            await using (var command = new SqlCommand("SELECT s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id ORDER BY s.name,t.name", connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) names.Add(reader.GetString(0) + "." + reader.GetString(1));

            var project = new ConfiguratorProject();
            foreach (var name in names)
            {
                var schema = await OnlineProjectDataSource.ReadSchemaAsync(connection, transaction, name, ct).ConfigureAwait(false);
                if (schema == null) throw new InvalidOperationException("Структура базы изменилась. Обновите карту.");
                project.TableSchemas.Add(schema);
            }
            // Lock every potential address source, including currently unmapped tables, and the class registry.
            foreach (var schema in project.TableSchemas.Where(IsRelevant).OrderBy(x => x.TableName, StringComparer.Ordinal))
            {
                bool registry = schema.TableName.Split('.').Last().Equals("Classes", StringComparison.OrdinalIgnoreCase);
                var columns = schema.Columns.Where(c => registry || schema.KeyColumns.Contains(c.Name) ||
                    PlcAddressCatalog.IsAddressField(c.Name) || c.Name.ToUpperInvariant() is "PLC" or "RECORD" or "NAME" or "CLASS" or "CLASSNUMBER");
                string projection = string.Join(", ", columns.Select(c => Quote(c.Name)));
                await using var command = new SqlCommand($"SELECT {projection} FROM {QuoteTable(schema.TableName)} WITH (TABLOCKX,HOLDLOCK)", connection, transaction) { CommandTimeout = 60 };
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var values = new JsonObject();
                    for (int i = 0; i < reader.FieldCount; i++) values[reader.GetName(i)] = OnlineProjectDataSource.ReadValue(reader, i);
                    project.LocalRecords.Add(new ProjectRecord { TableName = schema.TableName, Values = values,
                        Key = schema.KeyColumns.Count == 0 ? "" : ProjectKeyCodec.Encode(schema, schema.KeyColumns, n => values[n]) });
                }
            }
            var definitions = await architecture.GetClassDefinitionsAsync(ct).ConfigureAwait(false);
            var definitionsSnapshot = JsonSerializer.Serialize(definitions);
            using var snapshot = new ProjectWorkspace(new OfflineProjectDataSource(project), architecture: new FrozenArchitecture(definitions), allowWrite: false);
            var labels = await ProjectClassCatalog.ReadTablesAsync(snapshot, project.TableSchemas, ct).ConfigureAwait(false);
            foreach (var table in project.TableSchemas.Where(t => IsRelevant(t) && !labels.ContainsKey(t.TableName)))
            {
                var fields = table.Columns.Where(c => PlcAddressCatalog.IsAddressField(c.Name)).Select(c => c.Name).ToList();
                if (project.LocalRecords.Any(r => r.TableName.Equals(table.TableName, StringComparison.OrdinalIgnoreCase) &&
                    ProjectClassCatalog.Read(r, "PLC") == expected.Plc && fields.Any(f => ProjectClassCatalog.Read(r, f) != null)))
                    throw new InvalidOperationException($"Таблица «{table.TableName}» содержит адреса PLC, но её класс не определён. Переназначение остановлено.");
            }
            var maps = await PlcAddressCatalog.LoadAsync(snapshot, ct).ConfigureAwait(false);
            if (!maps.TryGetValue(expected.Plc, out var map) || map.Issues.Count != 0)
                throw new InvalidOperationException("Карта PLC неполная или изменилась. Обновите карту и устраните нераспознанные адреса.");
            var schemaNow = project.TableSchemas.SingleOrDefault(s => s.TableName.Equals(expected.TableName, StringComparison.OrdinalIgnoreCase));
            if (schemaNow == null || schemaNow.KeyColumns.Count == 0 || !schemaNow.HasStableKey ||
                JsonSerializer.Serialize(schemaNow) != JsonSerializer.Serialize(expected.Schema))
                throw new InvalidOperationException("Структура таблицы или стабильный ключ изменились. Обновите карту.");
            var field = schemaNow.Columns.SingleOrDefault(c => c.Name.Equals(expected.Field, StringComparison.OrdinalIgnoreCase));
            if (field == null || field.IsReadOnly || field.DataType.ToLowerInvariant() is not ("varchar" or "nvarchar" or "char" or "nchar" or "tinyint" or "smallint" or "int" or "bigint"))
                throw new InvalidOperationException("Поле адреса недоступно для безопасного изменения.");
            var key = ProjectKeyCodec.Encode(expected.Schema, expected.Schema.KeyColumns, n => expected.Record.Values[n]);
            var matches = project.LocalRecords.Where(r => r.TableName.Equals(schemaNow.TableName, StringComparison.OrdinalIgnoreCase) && r.Key == key).ToList();
            if (matches.Count != 1 || ProjectClassCatalog.Read(matches[0], "PLC") != expected.Plc ||
                ProjectClassCatalog.Read(matches[0], field.Name) != expected.Raw)
                throw new InvalidOperationException("Исходная запись изменилась. Обновите карту.");

            ValidateMove(map, expected, newAddress.Trim());
            var predicates = new List<string>();
            await using var update = new SqlCommand { Connection = connection, Transaction = transaction, CommandTimeout = 60 };
            for (int i = 0; i < schemaNow.KeyColumns.Count; i++)
            {
                string column = schemaNow.KeyColumns[i];
                var type = schemaNow.Columns.Single(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)).DataType.ToLowerInvariant();
                if (type is not ("tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "uniqueidentifier" or "char" or "varchar" or "nchar" or "nvarchar"))
                    throw new InvalidOperationException("Тип ключа не поддерживает безопасное переназначение адреса.");
                var value = matches[0].Values[column]?.ToString() ?? throw new InvalidOperationException("Ключ записи содержит NULL.");
                predicates.Add($"{Quote(column)}=@k{i}");
                update.Parameters.AddWithValue("@k" + i, value);
            }
            object storedAddress = newAddress.Trim();
            if (field.DataType.ToLowerInvariant() is "tinyint" or "smallint" or "int" or "bigint")
            {
                var numeric = Regex.Match(newAddress.Trim(), @"^%?[IQ]?\s*W?\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!expected.Analog || !numeric.Success || !int.TryParse(numeric.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    throw new InvalidOperationException("Для числового аналогового адреса требуется смещение слова.");
                storedAddress = offset;
            }
            update.Parameters.AddWithValue("@address", storedAddress);
            update.CommandText = $"UPDATE {QuoteTable(schemaNow.TableName)} SET {Quote(field.Name)}=@address WHERE {string.Join(" AND ", predicates)}";
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Не удалось однозначно изменить запись. Обновите карту.");
            if (definitionsSnapshot != JsonSerializer.Serialize(await architecture.GetClassDefinitionsAsync(ct).ConfigureAwait(false)))
                throw new InvalidOperationException("Определения классов изменились. Обновите карту.");
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsRelevant(ProjectTableSchema table)
    {
        string bare = table.TableName.Split('.').Last();
        return bare.Equals("Classes", StringComparison.OrdinalIgnoreCase) ||
            ((bare.Equals("PLC", StringComparison.OrdinalIgnoreCase) || bare.Equals("PLCs", StringComparison.OrdinalIgnoreCase) ||
              table.Columns.Any(c => c.Name.Equals("PLC", StringComparison.OrdinalIgnoreCase))) &&
             table.Columns.Any(c => PlcAddressCatalog.IsAddressField(c.Name)));
    }

    private static void ValidateMove(PlcAddressMap map, AddressOwner expected, string address)
    {
        string source = expected.Display + " · " + expected.Raw;
        var actual = map.FindOwner(source);
        if (actual == null || actual.TableName != expected.TableName || actual.Field != expected.Field || actual.Record.Key != expected.Record.Key ||
            actual.DefaultArea != expected.DefaultArea || actual.Analog != expected.Analog)
            throw new InvalidOperationException("Адрес или состав конфликта изменился. Обновите карту.");
        var oldBits = map.Rows().Where(r => r.First == r.Last).SelectMany(r => r.Bits.Select((owners, bit) => (r.Area, Offset: r.First, Bit: bit, Owners: owners)))
            .Where(x => x.Owners.Contains(source)).ToList();
        if (oldBits.Count == 0 || !oldBits.Any(x => x.Owners.Count > 1))
            throw new InvalidOperationException("Исходный конфликт больше не существует. Обновите карту.");
        var candidate = new PlcAddressMap();
        candidate.Add(address, "new", expected.DefaultArea, expected.Analog);
        var newBits = candidate.Rows().Where(r => r.First == r.Last).SelectMany(r => r.Bits.Select((owners, bit) => (r.Area, Offset: r.First, Bit: bit, Owners: owners)))
            .Where(x => x.Owners.Count > 0).ToList();
        if (candidate.Issues.Count != 0 || newBits.Count != oldBits.Count || newBits.Any(x => x.Area != oldBits[0].Area || x.Owners.Count != 1) ||
            newBits.SelectMany(x => x.Owners).Distinct().Count() != 1)
            throw new InvalidOperationException("Новый адрес должен быть одним адресом той же области и ширины.");
        var occupied = map.Rows().Where(r => r.First == r.Last).SelectMany(r => r.Bits.Select((owners, bit) => (r.Area, Offset: r.First, Bit: bit, Owners: owners)))
            .Where(x => x.Owners.Count > 0).Select(x => (x.Area, x.Offset, x.Bit)).ToHashSet();
        if (newBits.Any(x => occupied.Contains((x.Area, x.Offset, x.Bit))))
            throw new InvalidOperationException("Новый адрес уже занят. Обновите карту и выберите свободный адрес.");
    }

    private static string Quote(string value) => "[" + value.Replace("]", "]]") + "]";
    private static string QuoteTable(string name) => string.Join(".", name.Split('.').Select(Quote));

    private sealed class FrozenArchitecture(IReadOnlyList<ClassDefinition> definitions) : IProjectArchitectureDataSource
    {
        public Task<IReadOnlyList<ClassDefinition>> GetClassDefinitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(definitions);
        public Task SaveClassDefinitionsAsync(IReadOnlyList<ClassDefinition> value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonObject> GetGraphRelationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveGraphRelationsAsync(JsonObject value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
