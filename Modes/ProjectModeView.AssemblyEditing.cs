using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Transactions;
using System.Windows;
using Configurator.Core.Projects;
using Microsoft.Data.SqlClient;
using static Configurator.AssemblyEditWindow;

namespace Configurator;

public partial class ProjectModeView
{
    private bool _assemblyDialogOpen;
    private bool CanEditAssembly(bool copy) => _workspace is { IsReadOnly: false } &&
        UserAuthorizationService.Has(copy ? Permission.AddData : Permission.EditData);

    internal sealed record BindingEdge(AssemblyLink Owner, AssemblyLink Target)
    {
        public string Id => $"{Owner.Row.TableName}/{(string.IsNullOrEmpty(Owner.Row.Key) ? string.Join(";", LogicalBindingKey(Owner.Row.TableName).Select(k => Field(Owner.Row,k))) : Owner.Row.Key)}/{Target.SourceField}";
    }

    private static List<BindingEdge> AssemblyEdges(List<AssemblyLink> links) =>
        links.SelectMany(row => row.Children.SelectMany(status =>
            new[] { new BindingEdge(row, status) }.Concat(status.Children.Select(sensor => new BindingEdge(status, sensor)))))
            .DistinctBy(edge => edge.Id).ToList();

    private static Dictionary<string,object> ScopeOf(ProjectRecord row)
    {
        var result = new Dictionary<string,object> { ["PLC"] = Field(row,"PLC") };
        if (row.TableName != "dbo.DiscretIN") result["PLC_Class_Number"] = Field(row,"PLC_Class_Number");
        if (result.Values.Any(value => string.IsNullOrWhiteSpace(value.ToString())))
            throw new InvalidOperationException("В записи отсутствует PLC или номер класса.");
        return result;
    }

    private static string AssemblySnapshot(ProjectRecord program, List<AssemblyLink> links) =>
        JsonSerializer.Serialize(links.Where(l => l.Row != null).SelectMany(l => new[]{l}.Concat(l.Children)
            .Concat(l.Children.SelectMany(c => c.Children))).Select(l => $"{l.Row.TableName}/{l.Row.Key}/{l.Row.Values.ToJsonString()}")
            .Append(program.Values.ToJsonString()).OrderBy(s => s,StringComparer.Ordinal));

    private async Task EditAssemblyAsync(ProjectRecord program, string identity)
    {
        bool copy = identity == null;
        if (_assemblyDialogOpen) return;
        if (!CanEditAssembly(copy)) { Infrastructure.ModernDialog.Show("Для этой операции требуется право изменения проекта.", "Objects"); return; }
        var ws = _workspace;
        bool Current() => ReferenceEquals(ws,_workspace) && _configurationKind == ConfigurationKind.Objects &&
            (ObjectList.SelectedItem as ObjectItem)?.AssemblyProgram == program;
        _assemblyDialogOpen = true;
        try
        {
            var (links, _) = await ReadAssemblyAsync(ws,program,Current);
            if (!Current()) return;
            var signature = AssemblySnapshot(program,links);
            var edges = AssemblyEdges(links);
            var scoped = new Dictionary<string,List<ProjectRecord>>();
            async Task<List<ProjectRecord>> Options(ProjectRecord row)
            {
                var filter = ScopeOf(row);
                string key = row.TableName + "/" + string.Join("/",filter.Values);
                if (!scoped.TryGetValue(key,out var records))
                    scoped[key] = records = await AssemblyRowsAsync(ws,row.TableName,filter,Current);
                return records;
            }
            if (copy)
            {
                foreach (var group in links.Where(l=>l.Row != null).Select(l=>l.Row).Append(program).GroupBy(r=>r.TableName))
                {
                    var schema = await ws.GetSchemaAsync(group.Key);
                    foreach (var row in group) AssemblyCopyKey(schema,row);
                }
            }
            var programs = copy ? await Options(program) : new List<ProjectRecord>();
            var dialog = new AssemblyEditWindow(copy, AssemblyLabel(program),
                FirstFree(programs.Select(r => int.Parse(Field(r,"Record")))),
                copy ? $"Записей программы для копирования: {links.Count(l => l.Row != null)}. Чтобы изменить датчик в копии, выберите «Создать копию» для его статуса."
                     : "Выберите новый элемент для нужной привязки. Замена в общем статусе действует для всех программ, использующих этот статус.")
                { Owner = Window.GetWindow(this) };
            var editable = copy ? edges.DistinctBy(edge => edge.Target.Identity) : edges.Where(edge => edge.Target.Identity == identity);
            foreach (var edge in editable)
            {
                var row = edge.Target.Row;
                var options = (await Options(row)).GroupBy(r => int.Parse(Field(r,"Record")))
                    .Where(g => g.Count()==1).Select(g => new AssemblyRecordOption(g.Key, AssemblyLabel(g.First()))).OrderBy(o=>o.Record).ToList();
                string role = row.TableName == "dbo.Statuses" ? "Статус" : "Датчик";
                string label = copy ? $"{role} · {edge.Target.Caption}" : $"{edge.Owner.Caption} · {edge.Target.SourceField}";
                dialog.AddChoice(copy ? edge.Target.Identity : edge.Id, label + $" · № {Field(row,"Record")}", int.Parse(Field(row,"Record")),options);
            }
            if (!Current()) return;
            var offline = _offlineSource;
            string connection = ws.IsOffline ? null : _connection.ToConnectionString(_selectedDatabase);
            dialog.Commit = async () =>
            {
                void Guard()
                {
                    if (!Current() || !CanEditAssembly(copy)) throw new InvalidOperationException("Проект, выбор или права изменились. Откройте окно заново.");
                }
                async Task ApplyPlan()
                {
                    Guard();
                    var key = ScopeOf(program);
                    key["Record"] = Field(program,"Record");
                    var current = (await AssemblyRowsAsync(ws,program.TableName,key,Current)).Single();
                    var (fresh, _) = await ReadAssemblyAsync(ws,current,Current);
                    if (AssemblySnapshot(current,fresh) != signature) throw new InvalidOperationException("Состав изменился. Откройте настройки заново.");
                    var liveEdges = AssemblyEdges(fresh);
                    foreach (var choice in dialog.Entries.Where(x=>x.Action != ActionKind.Keep))
                    {
                        var target = liveEdges.First(e => (copy ? e.Target.Identity : e.Id) == choice.Id).Target.Row;
                        var filter = ScopeOf(target);
                        filter["Record"] = choice.Record;
                        var count = (await AssemblyRowsAsync(ws,target.TableName,filter,Current)).Count;
                        if (count != (choice.Action == ActionKind.Replace ? 1 : 0))
                            throw new InvalidOperationException($"Record {choice.Record} недоступен в {target.TableName}.");
                    }
                    if (copy) await CopyAssemblyAsync(ws,current,fresh,dialog.Entries,dialog.ProgramRecord,dialog.ProgramName,offline);
                    else foreach (var choice in dialog.Entries.Where(x=>x.Action == ActionKind.Replace))
                    {
                        var edge = liveEdges.Single(e=>e.Id == choice.Id);
                        await WriteAssemblyBindingAsync(ws,connection,edge,choice.Record,offline);
                    }
                    Guard();
                }
                if (ws.IsOffline) await offline.ExecuteMutationTransactionAsync(ApplyPlan);
                else
                {
                    if (!new SqlConnectionStringBuilder(connection).Enlist) throw new InvalidOperationException("Подключение отключает участие в транзакциях.");
                    using var tx = new TransactionScope(TransactionScopeOption.RequiresNew,
                        new TransactionOptions { IsolationLevel = IsolationLevel.Serializable },TransactionScopeAsyncFlowOption.Enabled);
                    await ApplyPlan();
                    tx.Complete();
                }
            };
            if (dialog.ShowDialog() == true && Current())
            {
                if (copy)
                {
                    await LoadAssemblyListAsync();
                    var added = ObjectList.Items.OfType<ObjectItem>().FirstOrDefault(r=>r.AssemblyProgram != null &&
                        Field(r.AssemblyProgram,"Record") == dialog.ProgramRecord.ToString() && Field(r.AssemblyProgram,"PLC")==Field(program,"PLC") &&
                        Field(r.AssemblyProgram,"PLC_Class_Number")==Field(program,"PLC_Class_Number"));
                    if (added != null) { ObjectList.SelectedItem = added; ObjectList.ScrollIntoView(added); }
                }
                else ShowAssemblySelection();
            }
        }
        catch (Exception ex) { Infrastructure.ModernDialog.Show(Infrastructure.UserErrorMessage.Format(ex),"Objects"); }
        finally { _assemblyDialogOpen = false; }
    }

    private static IReadOnlyList<string> LogicalBindingKey(string table) => table switch
    {
        "dbo.Prog_QueueSelections" => ProgramSupportCatalog.ProgramTables.Single(t => t.TableName == table).KeyColumns,
        "dbo.Statuses" => new[] { "PLC", "PLC_Class_Number", "Record" },
        "dbo.Prog_Statuses" => new[] { "PLC", "PLC_Class_Number", "PLC_Class_Prog_Number", "Prog_Record", "Record" },
        "dbo.DiscretIN" => new[] { "PLC", "Record" },
        _ => ProgramManagementRepository.GetLogicalKey(table)
    };

    internal static async Task WriteAssemblyBindingAsync(IProjectWorkspace ws,string connection,BindingEdge edge,int target, OfflineProjectDataSource offline = null)
    {
        var row = edge.Owner.Row;
        var schema = await ws.GetSchemaAsync(row.TableName);
        var columns = schema?.HasStableKey == true && schema.KeyColumns.Count > 0
            ? (IReadOnlyList<string>)schema.KeyColumns : LogicalBindingKey(row.TableName);
        if (columns.Count == 0 || columns.Any(k => Field(row,k).Length == 0))
            throw new InvalidOperationException($"Ссылка {row.TableName}.{edge.Target.SourceField} сохранена без изменений: в проекте отсутствуют поля, однозначно определяющие исходную строку.");
        var keys = columns.ToDictionary(k=>k,k=>(object)Field(row,k));
        var field = edge.Target.SourceField;
        ProjectWriteGuard.EnsureDataTableWritable(row.TableName);
        if (ws.IsOffline)
        {
            if (schema.HasStableKey != true)
            {
                if (offline == null || ws.IsReadOnly) throw new InvalidOperationException("Снимок недоступен для изменения.");
                var matches = await AssemblyRowsAsync(ws,row.TableName,keys,()=>true);
                if (matches.Count != 1 || Field(matches[0],field) != Field(row,field))
                    throw new InvalidOperationException($"Привязка {row.TableName}.{field} изменилась или неоднозначна.");
                offline.UpdateAssemblySnapshotField(matches[0],field,target);
                return;
            }
            await ws.UpdateRecordAsync(row.TableName,schema.KeyColumns,keys,new Dictionary<string,object>{[field]=target});
            return;
        }
        static string Quote(string s) => "[" + s.Replace("]","]]") + "]";
        await using var sql = SqlProviderGuard.CreateConnection(connection);
        await SqlProviderGuard.OpenConnectionAsync(sql,default);
        using var cmd = sql.CreateCommand();
        var where = keys.Select((p,i) => { cmd.Parameters.AddWithValue("@k"+i,p.Value); return Quote(p.Key)+"=@k"+i; }).ToList();
        cmd.Parameters.AddWithValue("@old",int.Parse(Field(row,field)));
        cmd.Parameters.AddWithValue("@new",target);
        string table = string.Join(".", row.TableName.Split('.').Select(Quote));
        string predicate = string.Join(" AND ", where);
        cmd.CommandText = $"UPDATE {table} SET {Quote(field)}=@new WHERE {predicate} AND {Quote(field)}=@old AND (SELECT COUNT_BIG(*) FROM {table} WITH (UPDLOCK,HOLDLOCK) WHERE {predicate})=1";
        if (await cmd.ExecuteNonQueryAsync()!=1)
            throw new InvalidOperationException($"Ссылка {row.TableName}.{field} не сохранена. Строка изменилась, исчезла или найдены строки с одинаковыми ключевыми полями ({string.Join(", ",columns)}). Обновите состав Object.");
    }

    private static IReadOnlyList<string> AssemblyCopyKey(ProjectTableSchema schema, ProjectRecord row)
    {
        var key = schema?.HasStableKey == true ? schema.KeyColumns : LogicalBindingKey(row.TableName);
        if (schema == null || key.Count == 0 || key.Any(k => Field(row,k).Length == 0))
            throw new InvalidOperationException($"Дублирование недоступно: для {row.TableName} не определён полный ключ записи.");
        return key;
    }

    internal static async Task CopyAssemblyAsync(IProjectWorkspace ws, ProjectRecord program, List<AssemblyLink> links,
        IEnumerable<Choice> entries, int programRecord, string programName, OfflineProjectDataSource offline = null)
    {
        var choices = entries.ToDictionary(c=>c.Id);
        var edges = AssemblyEdges(links);
        var programScope = ScopeOf(program);
        programScope["Record"] = programRecord;
        if ((await ws.ReadPageAsync(new ProjectReadQuery {TableName=program.TableName, Filters=programScope, PageSize=1})).TotalCount>0)
            throw new InvalidOperationException("Record новой программы уже занят.");
        var targetScope = new Dictionary<string,object> { ["PLC"] = programScope["PLC"], ["PLC_Class_Prog_Number"] = programScope["PLC_Class_Number"], ["Prog_Record"] = programRecord };
        foreach (var schema in await ws.GetTablesAsync())
        {
            if (!targetScope.Keys.All(k => schema.Columns.Any(c => c.Name.Equals(k,StringComparison.OrdinalIgnoreCase)))) continue;
            if ((await ws.ReadPageAsync(new ProjectReadQuery { TableName=schema.TableName, Filters=targetScope, PageSize=1 })).TotalCount != 0)
                throw new InvalidOperationException($"Record {programRecord} уже используется в {schema.TableName}.");
        }
        async Task Insert(ProjectRecord row,Dictionary<string,object> changes)
        {
            var schema = await ws.GetSchemaAsync(row.TableName);
            var key = AssemblyCopyKey(schema,row);
            var values = new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            foreach(var c in schema.Columns.Where(c=>!c.IsComputed && !c.IsIdentity && !c.IsReadOnly &&
                !c.DataType.Equals("timestamp",StringComparison.OrdinalIgnoreCase) && !c.DataType.Equals("rowversion",StringComparison.OrdinalIgnoreCase)))
            {
                if (!row.Values.TryGetPropertyValue(c.Name,out var v)) continue;
                values[c.Name] = v == null ? null : c.DataType.ToLowerInvariant() switch
                {
                    "binary" or "varbinary" or "image" => Convert.FromBase64String(v.ToString()),
                    "bit" => v.GetValue<bool>(),
                    "int" or "smallint" or "tinyint" or "bigint" => long.Parse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture),
                    "money" or "smallmoney" or "decimal" or "numeric" => decimal.Parse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture),
                    "float" or "real" => double.Parse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture),
                    _ => v.ToString()
                };
            }
            foreach(var change in changes)
            {
                if (!values.ContainsKey(change.Key)) throw new InvalidOperationException($"Поле {row.TableName}.{change.Key} недоступно для записи.");
                values[change.Key] = change.Value;
            }
            if (!schema.HasStableKey)
            {
                var filters = key.ToDictionary(k=>k,k=>values[k]);
                if ((await ws.ReadPageAsync(new ProjectReadQuery { TableName=row.TableName, Filters=filters, PageSize=1 })).TotalCount != 0)
                    throw new InvalidOperationException($"В {row.TableName} уже есть запись с выбранным ключом.");
                if (ws.IsOffline)
                {
                    if (offline == null || ws.IsReadOnly) throw new InvalidOperationException("Снимок недоступен для записи.");
                    offline.InsertAssemblySnapshotRow(schema,values);
                    return;
                }
            }
            await ws.CreateRecordAsync(row.TableName,values);
        }

        foreach(var edge in edges.Where(e=>e.Target.Row.TableName=="dbo.DiscretIN"))
        {
            if (choices[edge.Target.Identity].Action != ActionKind.Keep && choices[edge.Owner.Identity].Action != ActionKind.Copy)
                throw new InvalidOperationException($"Для замены датчика создайте копию статуса «{edge.Owner.Caption}».");
        }
        foreach(var node in edges.Select(e=>e.Target).DistinctBy(n=>n.Identity).OrderBy(n=>n.Row.TableName=="dbo.Statuses"))
        {
            var choice=choices[node.Identity];
            if(choice.Action != ActionKind.Copy) continue;
            var changes = new Dictionary<string,object>{["Record"]=choice.Record};
            foreach(var child in node.Children) changes[child.SourceField]=choices[child.Identity].Record;
            await Insert(node.Row,changes);
        }
        var nameField = new[]{"Tag","TagName","Name_L1","NameL1","Name","Text_L1","Description_L1"}
            .Select(n=>program.Values.FirstOrDefault(p=>n.Equals(p.Key,StringComparison.OrdinalIgnoreCase)).Key).FirstOrDefault(n=>n!=null);
        if(nameField==null) throw new InvalidOperationException("У программы отсутствует поле имени.");
        await Insert(program,new(){["Record"]=programRecord,[nameField]=programName});
        foreach(var link in links.Where(l=>l.Row!=null))
        {
            var changes=new Dictionary<string,object>{["Prog_Record"]=programRecord};
            foreach(var child in link.Children) changes[child.SourceField]=choices[child.Identity].Record;
            await Insert(link.Row,changes);
        }
    }
}
