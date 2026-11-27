using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Configurator.Core.Projects;

namespace Configurator;

public partial class ProjectModeView
{

    internal sealed record AssemblyLink(string Relation, string Caption)
    {
        public string Identity { get; init; }
        public ProjectRecord Row { get; init; }
        public string SourceField { get; init; }
        public string DisplayGroup { get; init; }
        public List<AssemblyLink> Children { get; } = new();
    }

    private static string Field(ProjectRecord row, params string[] columns)
    {
        foreach (var column in columns)
        {
            var value = row.Values.FirstOrDefault(p => column.Equals(p.Key, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }

    private static string AssemblyLabel(ProjectRecord row)
    {
        var name = Field(row, "Tag", "TagName", "Name_L1", "NameL1", "Name", "Text_L1", "Description_L1");
        return name.Length > 0 ? name : row.Key.Length > 0 ? row.Key : Field(row, "Record");
    }

    private static async Task<List<ProjectRecord>> AssemblyRowsAsync(IProjectWorkspace workspace, string table,
        Dictionary<string, object> filters, Func<bool> current)
    {
        var result = new List<ProjectRecord>();
        for (int i = 1; current(); i++)
        {
            var page = await workspace.ReadPageAsync(new ProjectReadQuery
            {
                TableName = table, PageNumber = i, PageSize = 250, Filters = filters
            });
            result.AddRange(page.Items);
            if (i >= page.TotalPages || page.Items.Count == 0) break;
        }
        return result;
    }

    private async Task LoadAssemblyListAsync()
    {
        var workspace = _workspace;
        bool Current() => ReferenceEquals(workspace, _workspace) && ConfigurationList.SelectedItem is ConfigurationItem { IsObjects: true };
        ObjectList.ItemsSource = null;
        AssemblyCanvas.Children.Clear();

        AssemblyTitle.Text = "Загрузка программ…";
        try
        {
            if (workspace == null || await workspace.GetSchemaAsync(ProgramManagementRepository.ProgramTable) == null)
            {
                if (Current()) AssemblyTitle.Text = "В проекте отсутствует таблица программ";
                return;
            }
            var rows = await AssemblyRowsAsync(workspace, ProgramManagementRepository.ProgramTable, new(), Current);
            if (!Current()) return;
            ObjectList.ItemsSource = rows.Select(r => new ObjectItem { Name = AssemblyLabel(r), AssemblyProgram = r, RowIndex = -1 }).ToList();
            TxtObjectCount.Text = rows.Count.ToString();
            AssemblyTitle.Text = rows.Count == 0 ? "В проекте нет программ" : "Выберите объект";
            if (rows.Count > 0) ObjectList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            if (Current()) AssemblyTitle.Text = Infrastructure.UserErrorMessage.Format(ex);
        }
    }

    private bool CanDeleteAssembly => !_deletingSelection && _workspace is { IsReadOnly: false } &&
        _configurationKind == ConfigurationKind.Objects && ObjectList.SelectedItems.Count == 1 &&
        ObjectList.SelectedItem is ObjectItem { AssemblyProgram: not null } && UserAuthorizationService.Has(Permission.DeleteData);

    private async void AssemblyDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!CanDeleteAssembly) return;
        var program = ((ObjectItem)ObjectList.SelectedItem).AssemblyProgram;
        var generation = _workspaceGeneration;
        _deletingSelection = true;
        try
        {
            var repository = _offlineSource != null ? new ProgramManagementRepository(_offlineSource)
                : new ProgramManagementRepository(_connection.ToConnectionString(_selectedDatabase));
            var programs = await repository.GetProgramsAsync(Field(program,"PLC"), Field(program,"PLC_Class_Number"));
            var row = programs.Rows.Cast<System.Data.DataRow>().Single(r => r["Record"].ToString() == Field(program,"Record"));
            if (generation == _workspaceGeneration && (ObjectList.SelectedItem as ObjectItem)?.AssemblyProgram == program)
                await DeleteProgramFromListAsync(SnapshotRow(row), generation, program);
        }
        catch (Exception ex) { ShowActionFeedback(Infrastructure.UserErrorMessage.Format(ex), isError: true); }
        finally { _deletingSelection = false; AssemblyDeleteButton.IsEnabled = CanDeleteAssembly; }
    }

    private async void AssemblyCopyClick(object sender, RoutedEventArgs e)
    {
        if (ObjectList.SelectedItems.Count == 1 && ObjectList.SelectedItem is ObjectItem { AssemblyProgram: { } program })
            await EditAssemblyAsync(program, null);
    }

    private async void ShowAssemblySelection()
    {
        AssemblyCanvas.Children.Clear();

        AssemblyDeleteButton.IsEnabled = CanDeleteAssembly;
        AssemblyCopyButton.IsEnabled = ObjectList.SelectedItems.Count == 1 && CanEditAssembly(true);
        if (ObjectList.SelectedItem is not ObjectItem { AssemblyProgram: { } program } root) return;
        var workspace = _workspace;
        bool Current() => ReferenceEquals(workspace, _workspace) && ReferenceEquals(ObjectList.SelectedItem, root)
            && ConfigurationList.SelectedItem is ConfigurationItem { IsObjects: true };

        AssemblyTitle.Text = "Загрузка состава…";
        try
        {
            var (links, tank) = await ReadAssemblyAsync(workspace, program, Current);
            if (!Current()) return;
            AssemblyTitle.Text = string.Empty;
            DrawAssembly(program, links, tank);
        }
        catch (Exception ex)
        {
            if (Current()) AssemblyTitle.Text = $"{AssemblyLabel(program)} · {Infrastructure.UserErrorMessage.Format(ex)}";
        }
    }

    private void DrawAssembly(ProjectRecord program, List<AssemblyLink> links, bool tank)
    {
        var statuses = links.SelectMany(row => row.Children).GroupBy(s => s.Identity).ToList();
        var type = links.FirstOrDefault(link => link.Relation.StartsWith("Program.Type"))?.Caption;
        var groups = links.Where(l => l.DisplayGroup != null).GroupBy(l => l.DisplayGroup).ToList();
        int rows = statuses.Sum(s => Math.Max(1, s.First().Children.Count)) + groups.Count;
        double height = Math.Max(330, 130 + rows * 78);
        AssemblyCanvas.Width = 690;
        double center = (height + 64) / 2;
        AssemblyCanvas.Height = height;

        void Connect(Point a, Point b)
        {
            double mid = (a.X + b.X) / 2;
            var curve = new BezierSegment(new Point(mid, a.Y), new Point(mid, b.Y), b, true);
            var line = new Path { Data = new PathGeometry(new[] { new PathFigure(a, new[] { curve }, false) }),
                StrokeThickness = 2, IsHitTestVisible = false };
            line.SetResourceReference(Shape.StrokeProperty, "BrushAccent");
            AssemblyCanvas.Children.Insert(0, line);
        }

        Border Node(double x, double y, string role, string name, string detail, bool main = false, AssemblyLink component = null)
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = role, FontSize = 13, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            content.Children.Add(label);
            var title = new TextBlock { Text = name, FontSize = main ? 19 : 15, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");
            content.Children.Add(title);
            if (main && tank)
            {
                var vessel = new Path { Data = Geometry.Parse("M2,8 C2,0 48,0 48,8 L48,32 C48,42 2,42 2,32 Z M2,8 C2,17 48,17 48,8"),
                    StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,6) };
                vessel.SetResourceReference(Shape.StrokeProperty, "BrushAccent");
                content.Children.Insert(0, vessel);
            }
            var card = new Border { Width = 172, Height = main ? 116 : 60, CornerRadius = new CornerRadius(main ? 14 : 8),
                BorderThickness = new Thickness(main ? 2 : 1), Padding = new Thickness(8,2,8,2), Child = content, ToolTip = detail };
            card.SetResourceReference(Border.BackgroundProperty, main ? "BrushSelected" : "BrushBase");
            card.SetResourceReference(Border.BorderBrushProperty, main ? "BrushAccent" : "BrushBorder");
            if (component != null)
            {
                card.Cursor = System.Windows.Input.Cursors.Hand;
                card.MouseLeftButtonDown += async (_, e) =>
                {
                    if (e.ClickCount != 2) return;
                    e.Handled = true;
                    await EditAssemblyAsync(program, component.Identity);
                };
            }
            Canvas.SetLeft(card, x - 86);
            Canvas.SetTop(card, y - card.Height / 2);
            AssemblyCanvas.Children.Add(card);
            return card;
        }

        var name = AssemblyLabel(program);
        Node(95, center, type ?? "Объект", name, name, true);
        Node(95, 42, "Программа", name, name);
        Connect(new Point(95,72),new Point(95,center-58));
        double next = 95;
        foreach (var bindings in statuses)
        {
            var status = bindings.First();
            var origins = bindings.Select(s => s.SourceField == "Status_Record" ? "очередь" : "программа").Distinct();
            double y = next + (Math.Max(1,status.Children.Count)-1)*39;
            Node(340,y,"Статус · " + string.Join(" / ",origins),status.Caption,status.Relation,component:status);
            Connect(new Point(181,center),new Point(254,y));
            foreach (var sensor in status.Children)
            {
                Node(595,next,sensor.SourceField == "LSL_Sensor" ? "Нижний уровень" : "Верхний уровень",sensor.Caption,status.Caption + " · " + sensor.SourceField,component:sensor);
                Connect(new Point(426,y),new Point(509,next));
                next += 78;
            }
            if (status.Children.Count == 0) next += 78;
        }
        foreach (var group in groups)
        {
            double y = next;
            next += 78;
            var names = group.Select(n => n.Caption).ToList();
            var node = Node(340, y, group.Key, $"{names.Count} · Открыть", group.Key);
            node.Cursor = System.Windows.Input.Cursors.Hand;
            node.MouseLeftButtonUp += (_, _) =>
            {
                var list = new ListBox { ItemsSource = names, Margin = new Thickness(12) };
                var window = new Window { Owner = Window.GetWindow(this), Title = $"{name} · {group.Key}", Width = 480, Height = 440, Content = list, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                window.ShowDialog();
            };
            Connect(new Point(181,center),new Point(254,y));
        }
    }
    internal static async Task<(List<AssemblyLink> Links, bool Tank)> ReadAssemblyAsync(
        IProjectWorkspace workspace, ProjectRecord program, Func<bool> Current)
    {
        var links = new List<AssemblyLink>();
        bool tank = false;
        var schemas = await workspace.GetTablesAsync();
        var tables = schemas.Select(s => s.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var type = Field(program, "Type");
        if (type.Length > 0 && tables.Contains("dbo.Prog_Types"))
        {
            var types = await AssemblyRowsAsync(workspace, "dbo.Prog_Types", new() { ["Record"] = type }, Current);
            if (!Current()) return (links, false);
            var label = types.Count == 1 ? AssemblyLabel(types[0]) : $"Тип {type} · связь не разрешена";
            links.Add(new("Program.Type → Prog_Types.Record", label));
            if (types.Count == 1 && new[] { "танк", "емкость", "ёмкость", "tank" }.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase)))
                tank = true;
        }
        var scope = new Dictionary<string, object>
        {
            ["PLC"] = Field(program, "PLC"), ["PLC_Class_Prog_Number"] = Field(program, "PLC_Class_Number"),
            ["Prog_Record"] = Field(program, "Record")
        };
        if (scope.Values.Any(v => string.IsNullOrEmpty((string)v)))
            links.Add(new("Состав недоступен", "Неполный ключ программы"));
        else
        {
            var stepClasses = new Dictionary<string,List<ProjectRecord>>();
            var seenSteps = new HashSet<(string,string)>();
            var children = new[] { ProgramManagementRepository.SequenceTable, ProgramManagementRepository.TransitionTable,
                ProgramManagementRepository.ParameterTable }.Concat(ProgramSupportCatalog.ProgramTables.Select(t => t.TableName));
            var scopedTables = schemas.Where(s => scope.Keys.All(k => s.Columns.Any(c => c.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))).Select(s => s.TableName);
            foreach (var table in children.Where(tables.Contains).Concat(scopedTables).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var rows = await AssemblyRowsAsync(workspace, table, scope, Current);
                if (!Current()) return (links, false);
                foreach (var row in rows)
                {
                    var child = new AssemblyLink($"{table} → Program · PLC / PLC_Class_Prog_Number / Prog_Record", AssemblyLabel(row)) { Row = row, DisplayGroup = table == ProgramManagementRepository.ParameterTable ? "Параметры" : null };
                    links.Add(child);
                    if (table == ProgramManagementRepository.TransitionTable && tables.Contains(ProgramManagementRepository.StepTable))
                    {
                        string stepClass = Field(row,"PLC_Class_Step_Number"), stepRecord = Field(row,"Step_Record");
                        if (stepClass.Length > 0 && stepRecord.Length > 0 && seenSteps.Add((stepClass,stepRecord)))
                        {
                            if (!stepClasses.TryGetValue(stepClass,out var steps))
                                stepClasses[stepClass] = steps = await AssemblyRowsAsync(workspace,ProgramManagementRepository.StepTable,new() { ["PLC"]=scope["PLC"], ["PLC_Class_Number"]=stepClass },Current);
                            var match = steps.Where(r => Field(r,"Record")==stepRecord).Take(2).ToList();
                            if (match.Count == 1) links.Add(new("Step_Record → Prog_Step.Record",$"{stepRecord} · {AssemblyLabel(match[0])}") { DisplayGroup="Шаги" });
                        }
                    }
                    if (table is not ("dbo.Prog_QueueSelections" or "dbo.Prog_Statuses") || !tables.Contains("dbo.Statuses")) continue;
                    string statusField = table == "dbo.Prog_Statuses" ? "Prog_Status_Number" : "Status_Record";
                    var status = Field(row, statusField);
                    var cls = Field(row, table == "dbo.Prog_Statuses" ? "PLC_Class_Number" : "PLC_Class_Status_Number");
                    if (status.Length == 0 || cls.Length == 0) continue;
                    var matches = await AssemblyRowsAsync(workspace, "dbo.Statuses", new() {
                        ["PLC"] = scope["PLC"], ["PLC_Class_Number"] = cls, ["Record"] = status }, Current);
                    if (!Current()) return (links, false);
                    if (matches.Count != 1) continue;
                    var statusName = AssemblyLabel(matches[0]);
                    var statusNode = new AssemblyLink($"{statusField} → Statuses.Record", statusName) { Identity = $"status/{cls}/{status}", Row = matches[0], SourceField = statusField };
                    child.Children.Add(statusNode);
                    if (!tables.Contains("dbo.DiscretIN")) continue;
                    foreach (var field in new[] { "LSL_Sensor", "LSH_Sensor" })
                    {
                        var sensor = Field(matches[0], field);
                        if (sensor.Length == 0) continue;
                        var inputs = await AssemblyRowsAsync(workspace, "dbo.DiscretIN", new() {
                            ["PLC"] = scope["PLC"], ["Record"] = sensor }, Current);
                        if (!Current()) return (links, false);
                        if (inputs.Count == 1) statusNode.Children.Add(new($"{field} → DiscretIN.Record", AssemblyLabel(inputs[0])) { Identity = $"input/{sensor}", Row = inputs[0], SourceField = field });
                    }
                }
            }
        }
        return (links, tank);
    }
}
