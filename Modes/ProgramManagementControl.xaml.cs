using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace Configurator;

public partial class ProgramManagementControl : UserControl
{
    private enum ProgramTreeNodeKind { Program, Sequence, ProgramParameters, Transitions, StepParameters, StepOccurrence }

    private sealed class ProgramTreeNode
    {
        public ProgramTreeNodeKind Kind { get; init; }
        public ProgramTreeNode Parent { get; init; }
        public DataRowView ProgramRow { get; init; }
        public DataRowView SequenceRow { get; init; }
        public DataRowView TransitionRow { get; init; }
        public DataRowView StepRow { get; init; }
        public TreeViewItem Item { get; set; }
        public string NavigationPath => Parent == null ? Item.ToolTip?.ToString() ?? "" : $"{Parent.NavigationPath} → {Item.ToolTip}";
        public bool ChildrenLoaded { get; set; }
        public bool IsLoading { get; set; }
    }

    private static readonly HashSet<string> ProtectedContextColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "PLC", "PLC_Class_Number", "PLC_Class_Prog_Number", "PLC_Class_Step_Number",
        "PLC_Class_Matrix_Number", "Prog_Record", "Seq_Record", "Record", "Step_Record",
        "SeqStep_Number", "Step_Number", "Recipe_Record", "Queue_Record", "Matrix_Record",
        "Matrix_DeviceList"
    };

    private static readonly Dictionary<string, string> TableByGrid = new(StringComparer.OrdinalIgnoreCase)
    {
        ["programs"] = ProgramManagementRepository.ProgramTable,
        ["sequences"] = ProgramManagementRepository.SequenceTable,
        ["transitions"] = ProgramManagementRepository.TransitionTable,
        ["programParams"] = ProgramManagementRepository.ParameterTable,
        ["stepParams"] = ProgramManagementRepository.ParameterTable
    };

    private static readonly Dictionary<string, string[]> JournalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProgramManagementRepository.ProgramTable] = new[] { "PLC", "PLC_Class_Number", "Record" },
        [ProgramManagementRepository.SequenceTable] = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Record" },
        [ProgramManagementRepository.TransitionTable] = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number" },
        [ProgramManagementRepository.ParameterTable] = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Step_Number", "Type", "Record" }
    };

    private ProgramManagementRepository _repository;
    private Configurator.Core.Projects.OfflineProjectDataSource _offlineSource;
    private string _connectionString;
    private bool _loading;
    private bool _revertingSelection;
    private bool _contextChangePending;
    private bool _syncingTreeSelection;
    private ProgramTreeNode _pendingTreeSelection;
    private DataRow _conditionStep;
    private DataView _referencedSteps;
    private readonly Dictionary<string, HashSet<string>> _nonEditableColumns = new(StringComparer.OrdinalIgnoreCase);

    public ProgramContext CurrentContext { get; private set; }
    public Func<Task<bool>> BeforeContextChangeAsync { get; set; }
    public event Action<ProgramContext> ContextChanged;
    public event Action<DataRow> ProgramSaved;
    public event Action<ChangeJournalEntry> ChangeJournalRequested;

    public ProgramManagementControl()
    {
        InitializeComponent();
        RecipesEditor.SetSections("recipes");
        SoftKeysEditor.SetSections("softkey");
        MessagesEditor.SetSections("messages");
        QueuesEditor.SetSections("queues", "queue-selections");
        foreach (var editor in SupportEditors)
            editor.ChangeJournalRequested += entry => ChangeJournalRequested?.Invoke(entry);
        Loaded += ProgramManagementControl_Loaded;
        Unloaded += ProgramManagementControl_Unloaded;
    }

    private void ProgramManagementControl_Loaded(object sender, RoutedEventArgs e)
    {
        UserAuthorizationService.Changed += UserAuthorization_Changed;
        ApplyEditAuthorization();
    }

    private void ProgramManagementControl_Unloaded(object sender, RoutedEventArgs e)
        => UserAuthorizationService.Changed -= UserAuthorization_Changed;

    private void UserAuthorization_Changed(object sender, EventArgs e)
        => Dispatcher.Invoke(ApplyEditAuthorization);

    private void ApplyEditAuthorization()
    {
        bool canEdit = UserAuthorizationService.Has(Permission.EditData);
        foreach (var grid in new[] { ProgramsGrid, TransitionsGrid })
            if (grid != null) grid.IsReadOnly = !canEdit;
        if (ConditionsGrid != null) ConditionsGrid.IsReadOnly = !CanEditTransitionConditions();
        if (ProgramFields != null) RenderProgramFields(ProgramsGrid.SelectedItem as DataRowView);
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        if (LoadButton == null) return;
        ProgramTreeView.IsEnabled = !_loading && !_contextChangePending;
        if (_loading || _contextChangePending)
        {
            foreach (var button in new[]
                {
                    LoadButton, DiscardChangesButton, AddProgramButton, DeleteProgramButton, SaveProgramsButton,
                    AddSequenceButton, DeleteSequenceButton, SaveSequencesButton, AddStepButton,
                    ExportProgramAwlButton, DeleteStepButton, SaveTransitionsButton, SaveProgramParamsButton, SaveStepParamsButton, MoveStepUpButton, MoveStepDownButton
                })
                button.IsEnabled = false;
            return;
        }

        bool canAdd = UserAuthorizationService.Has(Permission.AddData);
        bool canEdit = UserAuthorizationService.Has(Permission.EditData);
        bool canDelete = UserAuthorizationService.Has(Permission.DeleteData);
        bool hasProgram = CurrentContext?.RecordValue != null;
        bool hasSequence = CurrentContext?.SeqRecordValue != null;

        LoadButton.IsEnabled = _repository != null;
        ExportProgramAwlButton.IsEnabled = hasProgram && UserAuthorizationService.Has(Permission.ExportData);
        DiscardChangesButton.IsEnabled = HasPendingEdits();
        AddProgramButton.IsEnabled = _repository != null && canAdd;
        DeleteProgramButton.IsEnabled = hasProgram && ProgramsGrid.SelectedItem != null && canDelete;
        SaveProgramsButton.IsEnabled = hasProgram && ProgramsGrid.SelectedItem != null && canEdit;
        AddSequenceButton.IsEnabled = hasProgram && canAdd;
        DeleteSequenceButton.IsEnabled = hasProgram && SequencesGrid.SelectedItem != null && canDelete;
        SaveSequencesButton.IsEnabled = hasProgram && SequencesGrid.SelectedItem != null && canEdit;
        AddStepButton.IsEnabled = hasSequence && canAdd;
        DeleteStepButton.IsEnabled = hasSequence && TransitionsGrid.SelectedItem != null && canDelete;
        SaveTransitionsButton.IsEnabled = hasSequence && TransitionsGrid.SelectedItem != null && canEdit;
        int? selectedStep = TryGetSequenceStepNumber(TransitionsGrid.SelectedItem as DataRowView);
        var numbers = TransitionsGrid.Items.OfType<DataRowView>().Select(TryGetSequenceStepNumber);
        MoveStepUpButton.IsEnabled = hasSequence && canEdit && selectedStep.HasValue && numbers.Any(number => number < selectedStep);
        MoveStepDownButton.IsEnabled = hasSequence && canEdit && selectedStep.HasValue && numbers.Any(number => number > selectedStep);
        SaveProgramParamsButton.IsEnabled = hasProgram && ProgramParamsGrid.SelectedItem != null && canEdit;
        SaveStepParamsButton.IsEnabled = hasSequence && StepParamsGrid.SelectedItem != null && canEdit;
    }

    private ProgramSupportControl[] SupportEditors => new[] { RecipesEditor, SoftKeysEditor, MessagesEditor, QueuesEditor };

    public Task<bool> PrepareToLeaveProgramAsync() => CanChangeContextAsync(allowDiscard: true);

    public async Task<bool> PrepareSupportForContextChangeAsync()
    {
        foreach (var editor in SupportEditors)
            if (!await editor.PrepareForContextChangeAsync()) return false;
        return true;
    }

    public void UpdateSupportContext(ProgramContext context)
    {
        foreach (var editor in SupportEditors)
        {
            if (context == null) editor.ClearContext();
            else if (_offlineSource != null) editor.Configure(_offlineSource, context);
            else editor.Configure(_connectionString, context);
        }
    }

    public async Task<bool> OpenProgramAsync(string connectionString, Configurator.Core.Projects.OfflineProjectDataSource offline,
        object plcValue, object programClassValue, object record)
    {
        if (_repository != null && ReferenceEquals(_offlineSource, offline) && _connectionString == connectionString &&
            CurrentContext != null && SameValue(CurrentContext.PlcValue, plcValue) &&
            SameValue(CurrentContext.ClassNumberValue, programClassValue) && SameValue(CurrentContext.RecordValue, record)) return true;
        if (_loading || _contextChangePending) return false;
        if (_repository != null && !await CanChangeContextAsync(allowDiscard: true)) return false;
        _connectionString = connectionString;
        _offlineSource = offline;
        _repository = offline == null ? new ProgramManagementRepository(connectionString) : new ProgramManagementRepository(offline);
        PlcInput.Text = Convert.ToString(plcValue, CultureInfo.InvariantCulture) ?? string.Empty;
        ClassInput.Text = Convert.ToString(programClassValue, CultureInfo.InvariantCulture) ?? string.Empty;
        return await LoadProgramsAsync(record) && CurrentContext != null;
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        await LoadProgramsAsync(requireContextApproval: true);
    }

    private async void ProgramTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTreeSelection || _loading || e.NewValue is not TreeViewItem item || item.Tag is not ProgramTreeNode node)
            return;

        _pendingTreeSelection = node;
        var programRow = FindRow(ProgramsGrid, "Record", GetValue(node.ProgramRow.Row, "Record"));
        if (programRow == null)
        {
            SetStatus("Эта программа больше не загружена. Обновите список программ.");
            _pendingTreeSelection = null;
            return;
        }

        if (node.Kind == ProgramTreeNodeKind.Program)
        {
            DetailsTabs.SelectedItem = ProgramDetailsTab;
            _pendingTreeSelection = null;
            await SelectProgramAsync(programRow, ProgramsGrid.SelectedItem as DataRowView, false);
            return;
        }

        DetailsTabs.SelectedItem = SequenceTab;
        SequenceTabs.SelectedItem = node.Kind switch
        {
            ProgramTreeNodeKind.ProgramParameters => ProgramParamsTab,
            ProgramTreeNodeKind.StepParameters or ProgramTreeNodeKind.StepOccurrence => StepDetailsTab,
            _ => SequenceDetailsTab
        };
        if (!IsCurrentProgram(programRow))
            await SelectProgramAsync(programRow, ProgramsGrid.SelectedItem as DataRowView, false);
        if (!IsCurrentProgram(programRow))
        {
            _pendingTreeSelection = null;
            SyncTreeToContext();
            return;
        }

        var sequenceRow = CurrentContext != null && SameValue(CurrentContext.RecordValue, GetValue(programRow.Row, "Record"))
            ? FindRow(SequencesGrid, "Record", GetValue(node.SequenceRow.Row, "Record"))
            : null;
        if (sequenceRow != null && !SameValue(CurrentContext.SeqRecordValue, GetValue(sequenceRow.Row, "Record")))
            await SelectSequenceAsync(sequenceRow, SequencesGrid.SelectedItem as DataRowView, _contextChangePending);
        else if (sequenceRow == null)
            SetStatus("Последовательность не найдена в выбранной программе. Раскройте ветку ещё раз после обновления.");

        if (node.Kind == ProgramTreeNodeKind.StepOccurrence && !_contextChangePending)
            await SelectPendingTreeStepAsync();

        if (_pendingTreeSelection == node)
        {
            _pendingTreeSelection = null;
            SyncTreeToContext();
        }
    }

    private async void ProgramTreeNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || !ReferenceEquals(e.OriginalSource, item)) return;
        e.Handled = true;
        if (item.Tag is not ProgramTreeNode node || node.ChildrenLoaded || node.IsLoading) return;

        node.IsLoading = true;
        SetTreePlaceholder(node, "Загрузка ветки…");
        try
        {
            if (node.Kind == ProgramTreeNodeKind.Program)
            {
                var rows = IsCurrentProgram(node.ProgramRow)
                    ? SequencesGrid.Items.OfType<DataRowView>().ToList()
                    : (await _repository.GetSequencesAsync(CreateProgramContext(node.ProgramRow))).DefaultView.Cast<DataRowView>().ToList();
                PopulateProgramTreeNode(node, rows);
            }
            else if (node.Kind == ProgramTreeNodeKind.Sequence)
            {
                var context = CreateProgramContext(node.ProgramRow);
                context.SeqRecordValue = GetValue(node.SequenceRow.Row, "Record");
                var transitions = IsCurrentSequence(node.SequenceRow)
                    ? TransitionsGrid.Items.OfType<DataRowView>().ToList()
                    : (await _repository.GetTransitionsAsync(context)).DefaultView.Cast<DataRowView>().ToList();
                var steps = IsCurrentSequence(node.SequenceRow)
                    ? (_referencedSteps?.Cast<DataRowView>() ?? Enumerable.Empty<DataRowView>()).ToList()
                    : (await _repository.GetStepsAsync(context)).DefaultView.Cast<DataRowView>().ToList();
                PopulateSequenceTreeNode(node, transitions, steps);
            }
        }
        catch (Exception ex)
        {
            node.ChildrenLoaded = false;
            SetTreePlaceholder(node, "Не удалось загрузить. Раскройте повторно.");
            SetStatus("Не удалось загрузить ветку дерева: " + Configurator.Infrastructure.UserErrorMessage.Format(ex));
        }
        finally { node.IsLoading = false; }
    }

    private void RebuildProgramTree()
    {
        ProgramTreeView.Items.Clear();
        foreach (var row in ProgramsGrid.Items.OfType<DataRowView>().Where(row => HasPositiveRecord(row.Row)))
        {
            var node = new ProgramTreeNode { Kind = ProgramTreeNodeKind.Program, ProgramRow = row };
            node.Item = CreateTreeItem(node, FormatProgramTreeLabel(row.Row));
            SetTreePlaceholder(node, "Раскройте список последовательностей");
            ProgramTreeView.Items.Add(node.Item);
        }
        if (ProgramTreeView.Items.Count == 0)
            ProgramTreeView.Items.Add(new TreeViewItem { Header = "Для PLC и класса программ нет записей", IsEnabled = false });
        SyncTreeToContext();
    }

    private void PopulateProgramTreeNode(ProgramTreeNode node, IEnumerable<DataRowView> rows)
    {
        node.Item.Items.Clear();
        foreach (var row in rows.Where(row => HasPositiveRecord(row.Row)))
        {
            var child = new ProgramTreeNode
            {
                Kind = ProgramTreeNodeKind.Sequence,
                Parent = node,
                ProgramRow = node.ProgramRow,
                SequenceRow = row
            };
            child.Item = CreateTreeItem(child, FormatSequenceTreeLabel(row.Row));
            SetTreePlaceholder(child, "Раскройте список вхождений шагов");
            node.Item.Items.Add(child.Item);
        }
        if (node.Item.Items.Count == 0) AddEmptyTreeNode(node.Item, "Последовательностей нет");
        node.ChildrenLoaded = true;
    }

    private static bool HasPositiveRecord(DataRow row)
        => long.TryParse(Convert.ToString(GetValue(row, "Record"), CultureInfo.InvariantCulture), out long record) && record > 0;

    private void PopulateSequenceTreeNode(ProgramTreeNode node, IEnumerable<DataRowView> transitions, IEnumerable<DataRowView> steps)
    {
        node.Item.Items.Clear();
        foreach (var (kind, label) in new[]
        {
            (ProgramTreeNodeKind.ProgramParameters, "Параметры программы"),
            (ProgramTreeNodeKind.Transitions, "Шаги и переходы"),
            (ProgramTreeNodeKind.StepParameters, "Параметры шага")
        })
        {
            var section = new ProgramTreeNode
            {
                Kind = kind, Parent = node, ProgramRow = node.ProgramRow, SequenceRow = node.SequenceRow,
                ChildrenLoaded = true
            };
            section.Item = CreateTreeItem(section, label);
            node.Item.Items.Add(section.Item);
        }
        var stepRows = steps.ToList();
        foreach (var transition in transitions.OrderBy(row => TryGetSequenceStepNumber(row) ?? int.MaxValue))
        {
            var step = stepRows.FirstOrDefault(candidate => SameTransitionStep(transition, candidate));
            var child = new ProgramTreeNode
            {
                Kind = ProgramTreeNodeKind.StepOccurrence,
                Parent = node,
                ProgramRow = node.ProgramRow,
                SequenceRow = node.SequenceRow,
                TransitionRow = transition,
                StepRow = step
            };
            child.Item = CreateTreeItem(child, FormatStepOccurrenceLabel(transition.Row, step?.Row));
            node.Item.Items.Add(child.Item);
        }
        if (node.Item.Items.Count == 0) AddEmptyTreeNode(node.Item, "Вхождений шагов нет");
        node.ChildrenLoaded = true;
    }

    private TreeViewItem CreateTreeItem(ProgramTreeNode node, string label)
    {
        var item = new TreeViewItem { Header = new TextBlock { Text = label, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis }, Tag = node, ToolTip = label };
        item.Expanded += ProgramTreeNode_Expanded;
        return item;
    }

    private void RefreshTreeLabels()
    {
        foreach (var item in ProgramTreeView.Items.OfType<TreeViewItem>()) RefreshTreeLabels(item);
        ProgramTreePath.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
    }

    private static void RefreshTreeLabels(TreeViewItem item)
    {
        if (item.Tag is ProgramTreeNode node)
        {
            string label = node.Kind switch
            {
                ProgramTreeNodeKind.Program => FormatProgramTreeLabel(node.ProgramRow.Row),
                ProgramTreeNodeKind.Sequence => FormatSequenceTreeLabel(node.SequenceRow.Row),
                ProgramTreeNodeKind.ProgramParameters => "Параметры программы",
                ProgramTreeNodeKind.Transitions => "Шаги и переходы",
                ProgramTreeNodeKind.StepParameters => "Параметры шага",
                _ => FormatStepOccurrenceLabel(node.TransitionRow.Row, node.StepRow?.Row)
            };
            item.Header = new TextBlock { Text = label, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
            item.ToolTip = label;
        }
        foreach (var child in item.Items.OfType<TreeViewItem>()) RefreshTreeLabels(child);
    }

    private static void SetTreePlaceholder(ProgramTreeNode node, string message)
    {
        node.Item.Items.Clear();
        node.Item.Items.Add(new TreeViewItem { Header = message, IsEnabled = false });
        node.ChildrenLoaded = false;
    }

    private static void AddEmptyTreeNode(TreeViewItem parent, string message)
        => parent.Items.Add(new TreeViewItem { Header = message, IsEnabled = false });

    private static string FormatProgramTreeLabel(DataRow row)
        => FormatEntityTreeLabel("Программа", GetValue(row, "Record"), row);

    private static string FormatSequenceTreeLabel(DataRow row)
        => FormatEntityTreeLabel("Последовательность", GetValue(row, "Record"), row);

    private static string FormatStepOccurrenceLabel(DataRow transition, DataRow step)
    {
        string number = Convert.ToString(GetValue(transition, "SeqStep_Number"), CultureInfo.InvariantCulture) ?? "?";
        string stepName = GetTreeName(step);
        string identity = $"класс {GetValue(transition, "PLC_Class_Step_Number")}, Record {GetValue(transition, "Step_Record")}";
        return string.IsNullOrWhiteSpace(stepName) ? $"Шаг {number} · {identity}" : $"Шаг {number} · {stepName} · {identity}";
    }

    private static string FormatEntityTreeLabel(string title, object record, DataRow row)
    {
        string name = GetTreeName(row);
        return string.IsNullOrWhiteSpace(name) ? $"{title} · Record {record}" : $"{title} · {name} · Record {record}";
    }

    private static string GetTreeName(DataRow row)
    {
        if (row == null) return string.Empty;
        foreach (string column in new[] { "Name_L1", "NameL1", "Name_L2", "NameL2", "Description_L1", "DescriptionL1" })
        {
            string value = Convert.ToString(GetValue(row, column), CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return string.Empty;
    }

    private void RefreshSequenceTreeFromGrids()
    {
        if (CurrentContext == null || SequencesGrid.ItemsSource == null) return;
        var programNode = FindProgramTreeNode(CurrentContext.RecordValue);
        if (programNode == null) return;
        PopulateProgramTreeNode(programNode, SequencesGrid.Items.OfType<DataRowView>());
        var sequenceNode = programNode.Item.Items.OfType<TreeViewItem>()
            .Select(item => item.Tag as ProgramTreeNode)
            .FirstOrDefault(node => node?.Kind == ProgramTreeNodeKind.Sequence
                && SameValue(GetValue(node.SequenceRow.Row, "Record"), CurrentContext.SeqRecordValue));
        if (sequenceNode == null) return;
        PopulateSequenceTreeNode(sequenceNode, TransitionsGrid.Items.OfType<DataRowView>(), (_referencedSteps?.Cast<DataRowView>() ?? Enumerable.Empty<DataRowView>()));
        SyncTreeToContext();
    }

    private static ProgramContext CreateProgramContext(DataRowView row)
    {
        object plc = GetValue(row.Row, "PLC");
        object classNumber = GetValue(row.Row, "PLC_Class_Number") ?? GetValue(row.Row, "PLC_Class_Prog_Number");
        return new ProgramContext
        {
            PLC = Convert.ToString(plc, CultureInfo.InvariantCulture) ?? string.Empty,
            PlcValue = plc,
            ClassNumberValue = classNumber,
            RecordValue = GetValue(row.Row, "Record"),
            AreaValue = GetValue(row.Row, "Area")
        };
    }

    private bool IsCurrentProgram(DataRowView row)
        => CurrentContext != null && SameValue(CurrentContext.RecordValue, GetValue(row.Row, "Record"))
            && SameValue(CurrentContext.PlcValue, GetValue(row.Row, "PLC"))
            && SameValue(CurrentContext.ClassNumberValue,
                GetValue(row.Row, "PLC_Class_Number") ?? GetValue(row.Row, "PLC_Class_Prog_Number"));

    private bool IsCurrentSequence(DataRowView row)
        => CurrentContext != null
            && SameValue(CurrentContext.PlcValue, GetValue(row.Row, "PLC"))
            && SameValue(CurrentContext.ClassNumberValue, GetValue(row.Row, "PLC_Class_Prog_Number"))
            && SameValue(CurrentContext.RecordValue, GetValue(row.Row, "Prog_Record"))
            && SameValue(CurrentContext.SeqRecordValue, GetValue(row.Row, "Record"));

    private async Task SelectPendingTreeStepAsync()
    {
        var node = _pendingTreeSelection;
        if (node?.Kind != ProgramTreeNodeKind.StepOccurrence || !IsCurrentSequence(node.SequenceRow)) return;
        object number = GetValue(node.TransitionRow.Row, "SeqStep_Number");
        var transition = TransitionsGrid.Items.OfType<DataRowView>()
            .FirstOrDefault(row => SameValue(GetValue(row.Row, "SeqStep_Number"), number));
        if (transition == null)
        {
            SetStatus("Вхождение шага не найдено в выбранной последовательности. Обновите ветку дерева.");
            return;
        }
        _revertingSelection = true;
        try
        {
            TransitionsGrid.SelectedItem = transition;
            ShowTransitionConditions(transition);
        }
        finally { _revertingSelection = false; }
        DetailsTabs.SelectedItem = SequenceTab;
        SequenceTabs.SelectedItem = StepDetailsTab;
        if (int.TryParse(Convert.ToString(number, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int stepNumber))
            await LoadStepParametersAsync(stepNumber);
        SyncTreeToContext();
    }

    private void SyncTreeToContext()
    {
        if (CurrentContext == null) return;
        var programNode = FindProgramTreeNode(CurrentContext.RecordValue);
        if (programNode == null) return;
        TreeViewItem target = programNode.Item;
        programNode.Item.IsExpanded = true;
        if (CurrentContext.SeqRecordValue != null)
        {
            var sequenceNode = programNode.Item.Items.OfType<TreeViewItem>()
                .Select(item => item.Tag as ProgramTreeNode)
                .FirstOrDefault(node => node?.Kind == ProgramTreeNodeKind.Sequence
                    && SameValue(GetValue(node.SequenceRow.Row, "Record"), CurrentContext.SeqRecordValue));
            if (sequenceNode != null)
            {
                target = sequenceNode.Item;
                sequenceNode.Item.IsExpanded = true;
                var requestedSection = _pendingTreeSelection ?? (ProgramTreeView.SelectedItem as TreeViewItem)?.Tag as ProgramTreeNode;
                if (requestedSection?.Kind is ProgramTreeNodeKind.ProgramParameters
                        or ProgramTreeNodeKind.Transitions or ProgramTreeNodeKind.StepParameters
                    && SameValue(GetValue(requestedSection.SequenceRow.Row, "Record"), CurrentContext.SeqRecordValue)
                    && SameValue(GetValue(requestedSection.ProgramRow.Row, "Record"), CurrentContext.RecordValue))
                {
                    var section = sequenceNode.Item.Items.OfType<TreeViewItem>()
                        .FirstOrDefault(item => (item.Tag as ProgramTreeNode)?.Kind == requestedSection.Kind);
                    if (section != null) target = section;
                }
                else if (TransitionsGrid.SelectedItem is DataRowView selectedTransition)
                {
                    var stepNode = sequenceNode.Item.Items.OfType<TreeViewItem>()
                        .Select(item => item.Tag as ProgramTreeNode)
                        .FirstOrDefault(node => node?.Kind == ProgramTreeNodeKind.StepOccurrence
                            && SameValue(GetValue(node.TransitionRow.Row, "SeqStep_Number"), GetValue(selectedTransition.Row, "SeqStep_Number")));
                    if (stepNode != null) target = stepNode.Item;
                }
            }
        }
        _syncingTreeSelection = true;
        try { target.IsSelected = true; }
        finally { _syncingTreeSelection = false; }
    }

    private ProgramTreeNode FindProgramTreeNode(object record)
        => ProgramTreeView.Items.OfType<TreeViewItem>()
            .Select(item => item.Tag as ProgramTreeNode)
            .FirstOrDefault(node => node?.Kind == ProgramTreeNodeKind.Program
                && SameValue(GetValue(node.ProgramRow.Row, "Record"), record));

    private void DiscardChangesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        CommitGridEdits();
        if (!HasPendingEdits()) { SetStatus("Несохранённых изменений нет."); return; }
        if (MessageBox.Show("Отменить все несохранённые изменения в открытых таблицах?",
                "Отмена правок", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RejectGridChanges();
        SetStatus("Несохранённые изменения отменены.");
    }

    private async Task<bool> LoadProgramsAsync(object selectRecord = null, bool requireContextApproval = false,
        bool afterCommittedChange = false, string postCommitWarning = null)
    {
        if (_repository == null) { SetStatus("Панель не подключена к базе данных."); return false; }
        if (!TryParseContextInputs(out var plc, out var classNumber)) return false;
        if (requireContextApproval)
        {
            if (!await CanChangeContextAsync()) return false;
        }
        else if (!CanDiscardPendingEdits())
        {
            if (afterCommittedChange) SetStatus("Изменение сохранено, но список программ не обновлён из-за новых несохранённых правок." + FormatJournalWarning(postCommitWarning));
            return false;
        }

        if (_loading) return false;
        bool?[] enabledBeforeLoad = SetContextGridsEnabled(false);
        _loading = true;
        bool selectFirst = false;
        ProgramsGrid.ItemsSource = null;
        SequencesGrid.ItemsSource = null;
        _referencedSteps = null;
        ClearDetails();
        CurrentContext = null;
        PublishContext();
        ContextText.Text = string.Empty;
        try
        {
            var table = await _repository.GetProgramsAsync(plc, classNumber);
            if (selectRecord != null)
                foreach (DataRow row in table.Select().Where(row => !SameValue(GetValue(row, "Record"), selectRecord)))
                    table.Rows.Remove(row);
            await BindTableAsync(ProgramsGrid, table);
            RebuildProgramTree();
            SequencesGrid.ItemsSource = null;
            _referencedSteps = null;
            ClearDetails();
            CurrentContext = null;
            PublishContext();
            ContextText.Text = string.Empty;
            if (table.Rows.Count > 0)
            {
                selectFirst = true;
                SetStatus(postCommitWarning ?? string.Empty);
            }
            else SetStatus("Для выбранных PLC и класса программ нет." + FormatJournalWarning(postCommitWarning));
        }
        catch (Exception ex)
        {
            string message = afterCommittedChange
                ? "Изменение сохранено, но список программ не удалось обновить: " + ex.Message
                : "Не удалось загрузить программы: " + ex.Message;
            SetStatus(message + FormatJournalWarning(postCommitWarning));
            return false;
        }
        finally
        {
            _loading = false;
            RestoreContextGridsEnabled(enabledBeforeLoad);
        }
        if (selectFirst)
        {
            _loading = true;
            try { ProgramsGrid.SelectedIndex = FindRecordIndex(ProgramsGrid, selectRecord); }
            finally { _loading = false; }
            if (ProgramsGrid.SelectedItem is DataRowView selectedProgram)
                await SelectProgramAsync(selectedProgram, null, true);
        }
        return true;
    }

    private void ProgramFieldsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ProgramFields != null)
            ProgramFields.Columns = Math.Clamp((int)(e.NewSize.Width / 260), 1, 3);
    }

    private void RenderProgramFields(DataRowView row)
    {
        ProgramFields.Children.Clear();
        ProgramFieldsScroll?.ScrollToTop();
        if (row == null) return;
        bool canEdit = UserAuthorizationService.Has(Permission.EditData);
        string[][] fields =
        {
            new[] { "PLC" }, new[] { "PLC_Class_Number" }, new[] { "Record" }, new[] { "Type" },
            new[] { "Name_L1", "NameL1" }, new[] { "Name_L2", "NameL2" },
            new[] { "Description_L1", "DescriptionL1" }, new[] { "Description_L2", "DescriptionL2" },
            new[] { "FC_Number", "FCNumber" }, new[] { "MainDB_Number", "MainDBNumber" },
            new[] { "RecipeDB_Number", "RecipeDBNumber" }
        };
        foreach (var aliases in fields)
        {
            string column = aliases.FirstOrDefault(row.Row.Table.Columns.Contains);
            if (column == null) continue;
            bool readOnly = !canEdit || ProtectedContextColumns.Contains(column) ||
                (_nonEditableColumns.TryGetValue("programs", out var protectedColumns) && protectedColumns.Contains(column));
            var line = new DockPanel { Margin = new Thickness(4, 0, 8, 8), MinHeight = 52 };
            var label = new Label { Content = aliases[0], Padding = new Thickness(0, 0, 0, 3) };
            DockPanel.SetDock(label, Dock.Top);
            var input = new TextBox { IsReadOnly = readOnly, Padding = new Thickness(6, 4, 6, 4) };
            label.Target = input;
            input.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding($"[{column}]")
            {
                Source = row,
                Mode = readOnly ? System.Windows.Data.BindingMode.OneWay : System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.Explicit,
                ValidatesOnExceptions = true
            });
            input.Tag = column;
            line.Children.Add(label);
            line.Children.Add(input);
            ProgramFields.Children.Add(line);
        }
    }

    private async void ProgramsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderProgramFields(ProgramsGrid.SelectedItem as DataRowView);
        UpdateCommandState();
        if (_loading || _revertingSelection || _syncingTreeSelection || ProgramsGrid.SelectedItem is not DataRowView row) return;
        await SelectProgramAsync(row, e.RemovedItems.OfType<DataRowView>().FirstOrDefault(), false);
    }

    private async Task SelectProgramAsync(DataRowView row, DataRowView previousSelection, bool preapproved)
    {
        if (!ReferenceEquals(ProgramsGrid.SelectedItem, row))
        {
            _syncingTreeSelection = true;
            try { ProgramsGrid.SelectedItem = row; }
            finally { _syncingTreeSelection = false; }
        }
        if (_contextChangePending && !preapproved)
        {
            RestoreSelection(ProgramsGrid, previousSelection);
            SyncTreeToContext();
            return;
        }
        if (!CommitGridEdits())
        {
            RestoreSelection(ProgramsGrid, previousSelection);
            SetStatus("Есть несохранённые изменения или ошибки ввода. Сохраните их перед переключением программы.");
            SyncTreeToContext();
            return;
        }
        bool ownsChangeLock = !_contextChangePending;
        if (ownsChangeLock) _contextChangePending = true;
        bool?[] enabledBeforeChange = SetContextGridsEnabled(false);
        try
        {
            if (!preapproved && !await CanChangeContextAsync(allowDiscard: true))
            {
                RestoreSelection(ProgramsGrid, previousSelection);
                SyncTreeToContext();
                return;
            }
            var plc = GetValue(row.Row, "PLC") ?? ParseInput(PlcInput.Text);
            var classNumber = GetValue(row.Row, "PLC_Class_Number") ?? GetValue(row.Row, "PLC_Class_Prog_Number") ?? ParseInput(ClassInput.Text);
            if (CurrentContext != null && Equals(CurrentContext.RecordValue, GetValue(row.Row, "Record"))
                && Equals(CurrentContext.PlcValue, plc) && Equals(CurrentContext.ClassNumberValue, classNumber))
            {
                SyncTreeToContext();
                return;
            }
            CurrentContext = new ProgramContext
            {
                PLC = Convert.ToString(plc, CultureInfo.InvariantCulture) ?? string.Empty,
                PlcValue = plc,
                ClassNumberValue = classNumber,
                RecordValue = GetValue(row.Row, "Record"),
                AreaValue = GetValue(row.Row, "Area")
            };
            PublishContext();
            ContextText.Text = CurrentContext.ContextText;
            object requestedSequence = (_pendingTreeSelection?.Kind is ProgramTreeNodeKind.Sequence or ProgramTreeNodeKind.ProgramParameters
                or ProgramTreeNodeKind.Transitions or ProgramTreeNodeKind.StepParameters or ProgramTreeNodeKind.StepOccurrence)
                && SameValue(GetValue(_pendingTreeSelection.ProgramRow.Row, "Record"), CurrentContext.RecordValue)
                ? GetValue(_pendingTreeSelection.SequenceRow.Row, "Record") : null;
            await LoadSequencesAsync(selectRecord: requestedSequence, contextChangeApproved: true);
            SyncTreeToContext();
        }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeChange);
            if (ownsChangeLock) _contextChangePending = false;
        }
    }

    private async Task<bool> LoadSequencesAsync(object selectRecord = null, bool contextChangeApproved = false,
        bool afterCommittedChange = false, string postCommitWarning = null)
    {
        if (CurrentContext == null) return false;
        if (!CanDiscardPendingEdits())
        {
            if (afterCommittedChange) SetStatus("Изменение сохранено, но список последовательностей не обновлён из-за новых несохранённых правок." + FormatJournalWarning(postCommitWarning));
            return false;
        }
        if (_loading) return false;
        bool?[] enabledBeforeLoad = SetContextGridsEnabled(false);
        _loading = true;
        bool selectFirst = false;
        SequencesGrid.ItemsSource = null;
        _referencedSteps = null;
        ClearDetails();
        try
        {
            var table = await _repository.GetSequencesAsync(CurrentContext);
            await BindTableAsync(SequencesGrid, table);
            if (FindProgramTreeNode(CurrentContext.RecordValue) is ProgramTreeNode programNode)
                PopulateProgramTreeNode(programNode, SequencesGrid.Items.OfType<DataRowView>());
            _referencedSteps = null;
            ClearDetails();
            if (table.Rows.Count > 0)
            {
                selectFirst = true;
                SetStatus(postCommitWarning ?? string.Empty);
            }
            else
            {
                CurrentContext.SeqRecordValue = null;
                PublishContext();
                SetStatus("У выбранной программы нет последовательностей." + FormatJournalWarning(postCommitWarning));
            }
        }
        catch (Exception ex)
        {
            string message = afterCommittedChange
                ? "Изменение сохранено, но список последовательностей не удалось обновить: " + ex.Message
                : "Не удалось загрузить последовательности: " + ex.Message;
            SetStatus(message + FormatJournalWarning(postCommitWarning));
            return false;
        }
        finally
        {
            _loading = false;
            RestoreContextGridsEnabled(enabledBeforeLoad);
        }
        if (selectFirst)
        {
            _loading = true;
            try { SequencesGrid.SelectedIndex = FindRecordIndex(SequencesGrid, selectRecord); }
            finally { _loading = false; }
            if (SequencesGrid.SelectedItem is DataRowView selectedSequence)
                await SelectSequenceAsync(selectedSequence, null, contextChangeApproved);
        }
        return true;
    }

    private async void SequencesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCommandState();
        if (_loading || _revertingSelection || _syncingTreeSelection || CurrentContext == null || SequencesGrid.SelectedItem is not DataRowView row) return;
        await SelectSequenceAsync(row, e.RemovedItems.OfType<DataRowView>().FirstOrDefault(), false);
    }

    private async Task SelectSequenceAsync(DataRowView row, DataRowView previousSelection, bool preapproved)
    {
        if (!ReferenceEquals(SequencesGrid.SelectedItem, row))
        {
            _syncingTreeSelection = true;
            try { SequencesGrid.SelectedItem = row; }
            finally { _syncingTreeSelection = false; }
        }
        if (_contextChangePending && !preapproved)
        {
            RestoreSelection(SequencesGrid, previousSelection);
            SyncTreeToContext();
            return;
        }
        object sequenceRecord = GetValue(row.Row, "Record") ?? GetValue(row.Row, "Seq_Record");
        if (Equals(CurrentContext.SeqRecordValue, sequenceRecord))
        {
            SyncTreeToContext();
            return;
        }
        if (!CommitGridEdits())
        {
            RestoreSelection(SequencesGrid, previousSelection);
            SetStatus("Есть несохранённые изменения или ошибки ввода. Сохраните их перед переключением последовательности.");
            SyncTreeToContext();
            return;
        }
        bool ownsChangeLock = !_contextChangePending;
        if (ownsChangeLock) _contextChangePending = true;
        bool?[] enabledBeforeChange = SetContextGridsEnabled(false);
        try
        {
            if (!preapproved && !await CanChangeContextAsync(allowDiscard: true))
            {
                RestoreSelection(SequencesGrid, previousSelection);
                SyncTreeToContext();
                return;
            }
            CurrentContext.SeqRecordValue = sequenceRecord;
            PublishContext();
            ContextText.Text = CurrentContext.ContextText + "  ·  Seq=" + Convert.ToString(CurrentContext.SeqRecordValue, CultureInfo.InvariantCulture);
            await LoadSequenceChildrenAsync();
            SyncTreeToContext();
        }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeChange);
            if (ownsChangeLock) _contextChangePending = false;
        }
    }

    private async Task<bool> LoadSequenceChildrenAsync(bool afterCommittedChange = false, string postCommitWarning = null)
    {
        if (CurrentContext?.SeqRecordValue == null) return false;
        if (!CanDiscardPendingEdits())
        {
            if (afterCommittedChange) SetStatus("Изменение сохранено, но данные последовательности не обновлены из-за новых несохранённых правок." + FormatJournalWarning(postCommitWarning));
            return false;
        }
        if (_loading) return false;
        bool?[] enabledBeforeLoad = SetContextGridsEnabled(false);
        _referencedSteps = null;
        ClearDetails();
        _loading = true;
        try
        {

            var transitions = await _repository.GetTransitionsAsync(CurrentContext);
            var programParams = await _repository.GetParametersAsync(CurrentContext, 0);

            await BindTableAsync(TransitionsGrid, transitions);
            await BindTableAsync(ProgramParamsGrid, programParams);
            StepParamsGrid.ItemsSource = null;
            _referencedSteps = (await _repository.GetStepsAsync(CurrentContext)).DefaultView;
            RefreshSequenceTreeFromGrids();
            SetStatus(postCommitWarning ?? string.Empty);
        }
        catch (Exception ex)
        {
            string message = afterCommittedChange
                ? "Изменение сохранено, но данные последовательности не удалось обновить: " + ex.Message
                : "Не удалось загрузить данные последовательности: " + ex.Message;
            SetStatus(message + FormatJournalWarning(postCommitWarning));
            return false;
        }
        finally
        {
            _loading = false;
            RestoreContextGridsEnabled(enabledBeforeLoad);
        }
        return true;
    }

    private async void TransitionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCommandState();
        if (_loading || _revertingSelection || _contextChangePending || TransitionsGrid.SelectedItem is not DataRowView row
            || !row.Row.Table.Columns.Contains("SeqStep_Number")) return;
        if (!CanChangeSelection(TransitionsGrid, e)) return;
        ShowTransitionConditions(row);
        if (!int.TryParse(Convert.ToString(row["SeqStep_Number"], CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int stepNumber) || stepNumber <= 0) return;
        SyncTreeToContext();
        await LoadStepParametersAsync(stepNumber);
    }

    private void ShowTransitionConditions(DataRowView transition)
    {
        try
        {
            _conditionStep = (_referencedSteps?.Cast<DataRowView>() ?? Enumerable.Empty<DataRowView>())
                .FirstOrDefault(step => SameTransitionStep(transition, step))?.Row;
            ConditionsGrid.ItemsSource = ProgramTransitionConditions.Create(transition.Row).DefaultView;
            ConditionsGrid.IsReadOnly = !CanEditTransitionConditions();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            ConditionsGrid.ItemsSource = null;
            SetStatus("Условия перехода недоступны: " + ex.Message);
        }
    }

    private void ConditionsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == "Назначение")
        {
            var choices = DestinationChoices().ToList();
            if (ConditionsGrid.ItemsSource is DataView view)
                foreach (DataRow row in view.Table.Rows)
                    if (row[1] is int value && !choices.Any(choice => Equals(choice.Value, value)))
                        choices.Add(new TransitionDestination(value, $"{value} · сохранённое значение"));
            e.Column = new DataGridComboBoxColumn
            {
                Header = "Назначение", ItemsSource = choices, DisplayMemberPath = nameof(TransitionDestination.Label),
                SelectedValuePath = nameof(TransitionDestination.Value),
                SelectedValueBinding = new System.Windows.Data.Binding("[Назначение]")
            };
            return;
        }
        if (_conditionStep == null || e.PropertyName.Length != 3 || e.PropertyName[0] != 'C') return;
        string name = Convert.ToString(GetValue(_conditionStep, $"Condition_{e.PropertyName.Substring(1)}_Name_L1"));
        if (!string.IsNullOrWhiteSpace(name)) e.Column.Header = $"{e.PropertyName} · {name}";
    }

    private bool CanEditTransitionConditions()
        => UserAuthorizationService.Has(Permission.EditData) &&
           (!_nonEditableColumns.TryGetValue("transitions", out var fields) ||
            !fields.Any(field => field.Equals("TransPrev_CondMask", StringComparison.OrdinalIgnoreCase)
                || field.Equals("TransNext_CondMask", StringComparison.OrdinalIgnoreCase)
                || field.StartsWith("TransDest_", StringComparison.OrdinalIgnoreCase)));

    private void ConditionsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Column.DisplayIndex == 0 ||
            (e.Column.DisplayIndex == 1 && e.Row.Item is DataRowView row &&
             (Convert.ToString(row[0]) == "Prev" || Convert.ToString(row[0]) == "Next")))
            e.Cancel = true;
    }

    private void DetailGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateCommandState();

    private void EntityGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => { UpdateCommandState(); RefreshTreeLabels(); }));

    private int? FindSequenceStepNumber(DataRowView selectedStep, out bool ambiguous)
    {
        ambiguous = false;
        if (!selectedStep.Row.Table.Columns.Contains("PLC_Class_Number")
            || !selectedStep.Row.Table.Columns.Contains("Record")) return null;

        var candidates = TransitionsGrid.Items.OfType<DataRowView>()
            .Where(row => SameTransitionStep(row, selectedStep));
        var selectedTransition = TransitionsGrid.SelectedItem as DataRowView;
        if (selectedTransition != null && candidates.Contains(selectedTransition))
            return TryGetSequenceStepNumber(selectedTransition);

        var matches = candidates.ToList();
        ambiguous = matches.Count > 1;
        var transition = matches.Count == 1 ? matches[0] : null;
        return transition == null ? null : TryGetSequenceStepNumber(transition);
    }

    private static int? TryGetSequenceStepNumber(DataRowView transition) =>
        transition != null && int.TryParse(Convert.ToString(GetValue(transition.Row, "SeqStep_Number"), CultureInfo.InvariantCulture),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;

    private async Task LoadStepParametersAsync(int stepNumber)
    {
        if (_loading || _contextChangePending || CurrentContext?.SeqRecordValue == null || !CanDiscardPendingEdits()) return;
        var targetContext = CopyContext(CurrentContext);
        _contextChangePending = true;
        bool?[] enabledBeforeLoad = SetContextGridsEnabled(false);
        try
        {
            var table = await _repository.GetParametersAsync(targetContext, stepNumber);
            await BindTableAsync(StepParamsGrid, table);
            SetStatus(string.Empty);
        }
        catch (Exception ex) { SetStatus("Не удалось загрузить параметры шага: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeLoad);
            _contextChangePending = false;
        }
    }

    private static bool SameValue(object left, object right) =>
        left != null && right != null && string.Equals(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool SameTransitionStep(DataRowView transition, DataRowView step) =>
        transition.Row.Table.Columns.Contains("PLC_Class_Step_Number")
        && transition.Row.Table.Columns.Contains("Step_Record")
        && SameValue(GetValue(transition.Row, "PLC_Class_Step_Number"), GetValue(step.Row, "PLC_Class_Number"))
        && SameValue(GetValue(transition.Row, "Step_Record"), GetValue(step.Row, "Record"));

    private async void AddProgramButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.AddData) || !TryParseContextInputs(out var plc, out var classNumber)) return;
        if (!CanDiscardPendingEdits()) return;
        _contextChangePending = true;
        bool?[] enabledBeforeOperation = SetContextGridsEnabled(false);
        try
        {
            var values = await PromptInsertValuesAsync(ProgramManagementRepository.ProgramTable,
                new[] { "PLC", "PLC_Class_Number", "Record" }, "Новая программа");
            if (values == null) return;
            if (!await CanChangeContextAsync()) return;
            int record = await _repository.AddProgramAsync(plc, classNumber, values);
            var inserted = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase)
            {
                ["PLC"] = plc, ["PLC_Class_Number"] = classNumber, ["Record"] = record
            };
            string journalWarning = RaiseJournal(ProgramManagementRepository.ProgramTable, ChangeJournalOperationKind.Insert, null,
                inserted, IdentityFor(ProgramManagementRepository.ProgramTable, inserted), $"Создана программа Record={record}");
            bool refreshed = await LoadProgramsAsync(record, afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus($"Программа Record={record} создана." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Не удалось создать программу: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeOperation);
            _contextChangePending = false;
        }
    }

    private async void AddSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.AddData)) return;
        if (CurrentContext?.RecordValue == null) { SetStatus("Сначала выберите программу."); return; }
        if (!CanDiscardPendingEdits()) return;
        var targetContext = CopyContext(CurrentContext);
        _contextChangePending = true;
        bool?[] enabledBeforeOperation = SetContextGridsEnabled(false);
        try
        {
            var values = await PromptInsertValuesAsync(ProgramManagementRepository.SequenceTable,
                new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Record" }, "Новая последовательность");
            if (values == null) return;
            if (!await CanChangeContextAsync()) return;
            int record = await _repository.AddSequenceAsync(targetContext, values);
            var inserted = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase)
            {
                ["PLC"] = targetContext.PlcValue,
                ["PLC_Class_Prog_Number"] = targetContext.ClassNumberValue,
                ["Prog_Record"] = targetContext.RecordValue,
                ["Record"] = record
            };
            string journalWarning = RaiseJournal(ProgramManagementRepository.SequenceTable, ChangeJournalOperationKind.Insert, null,
                inserted, IdentityFor(ProgramManagementRepository.SequenceTable, inserted), $"Создана последовательность Record={record}");
            bool refreshed = await LoadSequencesAsync(record, contextChangeApproved: true, afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus($"Последовательность Record={record} создана." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Не удалось создать последовательность: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeOperation);
            _contextChangePending = false;
        }
    }

    private async void AddStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.AddData)) return;
        if (CurrentContext?.SeqRecordValue == null) { SetStatus("Сначала выберите программу и последовательность."); return; }
        if (!CanDiscardPendingEdits()) return;
        var targetContext = CopyContext(CurrentContext);
        _contextChangePending = true;
        bool?[] enabledBeforeOperation = SetContextGridsEnabled(false);
        try
        {
            var selectedStep = ChooseStep(await _repository.GetStepChoicesAsync(targetContext.PlcValue));
            if (selectedStep == null) return;
            var defaults = await _repository.GetStepTextDefaultsAsync(selectedStep.Row);
            var extraFields = await PromptInsertValuesAsync(ProgramManagementRepository.TransitionTable,
                new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number", "PLC_Class_Step_Number", "Step_Record" },
                "Параметры перехода нового шага", defaults);
            if (extraFields == null) return;
            int number = await _repository.AddStepToSequenceAsync(targetContext,
                selectedStep["PLC_Class_Number"], selectedStep["Record"], extraFields);
            var snapshot = new Dictionary<string, object>(extraFields, StringComparer.OrdinalIgnoreCase)
            {
                ["PLC"] = targetContext.PlcValue,
                ["PLC_Class_Prog_Number"] = targetContext.ClassNumberValue,
                ["Prog_Record"] = targetContext.RecordValue,
                ["Seq_Record"] = targetContext.SeqRecordValue,
                ["SeqStep_Number"] = number,
                ["PLC_Class_Step_Number"] = selectedStep["PLC_Class_Number"],
                ["Step_Record"] = selectedStep["Record"]
            };
            string journalWarning = RaiseJournal(ProgramManagementRepository.TransitionTable, ChangeJournalOperationKind.Insert, null,
                snapshot, IdentityFor(ProgramManagementRepository.TransitionTable, snapshot),
                $"Добавлен шаг последовательности SeqStep_Number={number}");
            bool refreshed = await LoadSequenceChildrenAsync(afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus($"Шаг добавлен в последовательность под номером {number}." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Не удалось добавить шаг: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeOperation);
            _contextChangePending = false;
        }
    }

    private async void MoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending ||
            !UserAuthorizationService.Require(Permission.EditData) ||
            TransitionsGrid.SelectedItem is not DataRowView selected ||
            sender is not Button button ||
            !int.TryParse(Convert.ToString(button.Tag, CultureInfo.InvariantCulture), out int direction)) return;
        if (!CanDiscardPendingEdits()) return;
        var context = CopyContext(CurrentContext);
        var original = ToValues(selected.Row, DataRowVersion.Current);
        _contextChangePending = true;
        bool?[] enabled = SetContextGridsEnabled(false);
        int? movedTo = null;
        try
        {
            movedTo = await _repository.MoveStepAsync(context, original, direction);
            string warning = RaiseJournal(ProgramManagementRepository.TransitionTable, ChangeJournalOperationKind.Update,
                original, null, IdentityFor(ProgramManagementRepository.TransitionTable, original),
                $"Перемещено вхождение шага на номер {movedTo}; изменены связанные ссылки");
            bool loaded = await LoadSequenceChildrenAsync(afterCommittedChange: true, postCommitWarning: warning);
            if (loaded)
            {
                var row = TransitionsGrid.Items.OfType<DataRowView>()
                    .FirstOrDefault(item => TryGetSequenceStepNumber(item) == movedTo);
                if (row != null)
                {
                    _revertingSelection = true;
                    try { TransitionsGrid.SelectedItem = row; }
                    finally { _revertingSelection = false; }
                    ShowTransitionConditions(row);
                    SyncTreeToContext();
                }
                SetStatus($"Вхождение перемещено на номер {movedTo}." + FormatJournalWarning(warning));
            }
        }
        catch (Exception ex) { SetStatus("Не удалось переместить вхождение: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            RestoreContextGridsEnabled(enabled);
            _contextChangePending = false;
        }
        if (movedTo.HasValue) await LoadStepParametersAsync(movedTo.Value);
    }


    private async void ExportProgramAwl_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending || _repository == null ||
            CurrentContext?.RecordValue == null || ProgramsGrid.SelectedItem is not DataRowView row ||
            !UserAuthorizationService.Require(Permission.ExportData)) return;
        var context = CopyContext(CurrentContext);
        _contextChangePending = true;
        ExportProgramAwlButton.IsEnabled = false;
        string temporaryFile = null;
        try
        {
            string content = await _repository.BuildAwlAsync(context);
            string name = Convert.ToString(GetValue(row.Row, "Name_L1") ?? GetValue(row.Row, "NameL1")) ?? "";
            string safeName = string.Concat(name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).TrimEnd('.', ' ');
            int record = Convert.ToInt32(context.RecordValue, CultureInfo.InvariantCulture);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт сохранённой программы Siemens AWL",
                Filter = "Siemens AWL (*.AWL)|*.AWL",
                DefaultExt = ".AWL",
                AddExtension = true,
                FileName = $"Program_ {record:D2} - {safeName}.AWL"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            temporaryFile = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await System.IO.File.WriteAllTextAsync(temporaryFile, content, new System.Text.UTF8Encoding(false));
            System.IO.File.Move(temporaryFile, dialog.FileName, overwrite: true);
            temporaryFile = null;
            SetStatus($"AWL-файл сохранён: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus("AWL не сохранён: " + Configurator.Infrastructure.UserErrorMessage.Format(ex));
        }
        finally
        {
            if (temporaryFile != null)
            {
                try { System.IO.File.Delete(temporaryFile); }
                catch (System.IO.IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _contextChangePending = false;
            UpdateCommandState();
        }
    }

    private async void DeleteProgramButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.DeleteData)) return;
        if (CurrentContext?.RecordValue == null) { SetStatus("Выберите программу для удаления."); return; }
        var targetContext = CopyContext(CurrentContext);
        if (!await CanChangeContextAsync()) return;
        var deletedProgram = ProgramsGrid.SelectedItem as DataRowView;
        var deletedProgramValues = deletedProgram == null ? null : ToValues(deletedProgram.Row, DataRowVersion.Current);
        if (deletedProgramValues == null) { SetStatus("Не удалось определить исходную строку программы."); return; }
        if (MessageBox.Show("Программа будет удалена только если у неё нет последовательностей, параметров и других зависимых данных. Продолжить?",
                "Удаление программы", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        bool ownsChangeLock = false;
        bool?[] enabledBeforeOperation = null;
        try
        {
            _contextChangePending = true;
            ownsChangeLock = true;
            enabledBeforeOperation = SetContextGridsEnabled(false);
            await _repository.DeleteProgramAsync(targetContext, expected: deletedProgramValues);
            string journalWarning = null;
            if (deletedProgramValues != null)
                journalWarning = RaiseJournal(ProgramManagementRepository.ProgramTable, ChangeJournalOperationKind.Delete, deletedProgramValues,
                    null, IdentityFor(ProgramManagementRepository.ProgramTable, deletedProgramValues), "Удалена программа");
            bool refreshed = await LoadProgramsAsync(afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus("Программа удалена." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Программа не удалена: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            if (ownsChangeLock)
            {
                RestoreContextGridsEnabled(enabledBeforeOperation);
                _contextChangePending = false;
            }
        }
    }

    private async void DeleteSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.DeleteData)) return;
        if (CurrentContext?.RecordValue == null || SequencesGrid.SelectedItem is not DataRowView row)
        { SetStatus("Выберите последовательность для удаления."); return; }
        var targetContext = CopyContext(CurrentContext);
        object sequenceRecord = GetValue(row.Row, "Record") ?? GetValue(row.Row, "Seq_Record");
        if (sequenceRecord == null) { SetStatus("В строке последовательности нет Record."); return; }
        if (!await CanChangeContextAsync()) return;
        var deletedSequenceValues = ToValues(row.Row, DataRowVersion.Current);
        bool ownsChangeLock = false;
        bool?[] enabledBeforeOperation = null;
        try
        {
            _contextChangePending = true;
            ownsChangeLock = true;
            enabledBeforeOperation = SetContextGridsEnabled(false);
            var preview = await _repository.PrepareSequenceDeletionAsync(targetContext, sequenceRecord);
            var counts = string.Join("\n", preview.Counts.Select(pair => $"{pair.Key}: {pair.Value}"));
            if (Configurator.Infrastructure.ModernDialog.Show(Window.GetWindow(this), $"Удалить последовательность № {sequenceRecord} и связанные записи?\n\n{counts}",
                    "Удаление последовательности", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var deletedRows = await _repository.DeleteSequenceAsync(targetContext, sequenceRecord, deleteDependents: true, expected: deletedSequenceValues, preview: preview);
            string journalWarning = RaiseJournal(ProgramManagementRepository.SequenceTable, ChangeJournalOperationKind.Delete, deletedSequenceValues,
                null, IdentityFor(ProgramManagementRepository.SequenceTable, deletedSequenceValues), "Удалена последовательность со связанными данными", deletedRows);
            bool refreshed = await LoadSequencesAsync(contextChangeApproved: true, afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus($"Последовательность Record={sequenceRecord} удалена." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Последовательность не удалена: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            if (ownsChangeLock)
            {
                RestoreContextGridsEnabled(enabledBeforeOperation);
                _contextChangePending = false;
            }
        }
    }

    private async void DeleteStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.DeleteData)) return;
        if (CurrentContext?.SeqRecordValue == null || TransitionsGrid.SelectedItem is not DataRowView row)
        { SetStatus("Выберите переход шага, который нужно убрать из последовательности."); return; }
        if (!CanDiscardPendingEdits()) return;
        var targetContext = CopyContext(CurrentContext);
        object stepNumber = GetValue(row.Row, "SeqStep_Number");
        if (stepNumber == null) { SetStatus("В переходе нет SeqStep_Number."); return; }
        var deletedStepValues = ToValues(row.Row, DataRowVersion.Current);
        if (MessageBox.Show("Шаг будет удалён из последовательности только если на него не ссылаются переходы и нет зависимых параметров, рецептов или выборов очереди. Продолжить?",
                "Удаление шага", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _contextChangePending = true;
        bool?[] enabledBeforeOperation = SetContextGridsEnabled(false);
        try
        {
            await _repository.DeleteStepFromSequenceAsync(targetContext, stepNumber, expected: deletedStepValues);
            string journalWarning = RaiseJournal(ProgramManagementRepository.TransitionTable, ChangeJournalOperationKind.Delete, deletedStepValues,
                null, IdentityFor(ProgramManagementRepository.TransitionTable, deletedStepValues), "Удалён шаг из последовательности");
            bool refreshed = await LoadSequenceChildrenAsync(afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus($"Шаг {stepNumber} удалён из последовательности." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Шаг не удалён: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeOperation);
            _contextChangePending = false;
        }
    }

    private async Task<Dictionary<string, object>> PromptInsertValuesAsync(string tableName,
        IEnumerable<string> generatedColumns, string title, IReadOnlyDictionary<string, object> defaults = null)
    {
        var fields = await _repository.GetInsertColumnsAsync(tableName, generatedColumns);
        var panel = new StackPanel { Margin = new Thickness(14) };
        var editors = new List<(ColumnInfo Column, TextBox Editor)>();
        foreach (var column in fields)
        {
            bool required = !column.IsNullable && !column.HasDefault || column.Name.Equals("Name_L1", StringComparison.OrdinalIgnoreCase);
            panel.Children.Add(new TextBlock
            {
                Text = column.Name + " · " + column.DataType + (required ? " *" : ""),
                Margin = new Thickness(0, 5, 0, 2)
            });
            var editor = new TextBox { MinWidth = 360, Margin = new Thickness(0, 0, 0, 3) };
            if (defaults != null && defaults.TryGetValue(column.Name, out var defaultValue))
                editor.Text = Convert.ToString(defaultValue, CultureInfo.InvariantCulture);
            panel.Children.Add(editor);
            editors.Add((column, editor));
        }
        if (fields.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Все остальные поля будут взяты из значений базы данных или оставлены пустыми." });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Создать", IsDefault = true, MinWidth = 90, Margin = new Thickness(4) };
        ok.SetResourceReference(StyleProperty, "BtnPrimary");
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90, Margin = new Thickness(4) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 560,
            Height = Math.Min(760, 220 + fields.Count * 62),
            MinHeight = 300,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return null;

        var values = defaults == null ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (column, editor) in editors)
        {
            string text = editor.Text?.Trim() ?? string.Empty;
            bool required = !column.IsNullable && !column.HasDefault || column.Name.Equals("Name_L1", StringComparison.OrdinalIgnoreCase);
            if (text.Length == 0)
            {
                if (required) throw new InvalidOperationException($"Заполните обязательное поле {column.Name}.");
                values.Remove(column.Name);
                continue;
            }
            values[column.Name] = ParseSqlValue(column, text);
        }
        return values;
    }

    private static object ParseSqlValue(ColumnInfo column, string value)
    {
        var style = NumberStyles.Number | NumberStyles.AllowExponent;
        return column.DataType.ToLowerInvariant() switch
        {
            "tinyint" => byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            "smallint" => short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            "int" => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            "bigint" => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            "bit" => value == "1" ? true : value == "0" ? false : bool.Parse(value),
            "decimal" or "numeric" or "money" or "smallmoney" => decimal.Parse(value, style, CultureInfo.InvariantCulture),
            "real" => float.Parse(value, style, CultureInfo.InvariantCulture),
            "float" => double.Parse(value, style, CultureInfo.InvariantCulture),
            "date" or "datetime" or "datetime2" or "smalldatetime" => DateTime.Parse(value, CultureInfo.InvariantCulture),
            "uniqueidentifier" => Guid.Parse(value),
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => value,
            _ => throw new InvalidOperationException($"Автоввод поля {column.Name} типа {column.DataType} не поддерживается.")
        };
    }

    private DataRowView ChooseStep(DataTable choices)
    {
        if (choices.Rows.Count == 0)
        {
            SetStatus("Для выбранного PLC справочник Prog_Step пуст.");
            return null;
        }
        string Label(DataRowView row)
        {
            var name = GetValue(row.Row, "Name_L1") ?? GetValue(row.Row, "NameL1") ??
                GetValue(row.Row, "Name_L2") ?? GetValue(row.Row, "NameL2");
            return $"{name ?? "Без названия"}  ·  {row["PLC_Class_Number"]}/{row["Record"]}";
        }
        var options = choices.DefaultView.Cast<DataRowView>()
            .Select(row => (Label: Label(row), Row: row))
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(option => new StepChoice(option.Label, option.Row)).ToArray();
        var list = new ListBox { ItemsSource = options, DisplayMemberPath = nameof(StepChoice.Label), SelectedIndex = 0 };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6) };
        System.Windows.Automation.AutomationProperties.SetName(search, "Поиск шага");
        search.TextChanged += (_, _) =>
        {
            list.ItemsSource = options.Where(option => option.Label.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
            list.SelectedIndex = 0;
        };
        var choose = new Button { Content = "Выбрать шаг", MinWidth = 100, IsDefault = true, Margin = new Thickness(4) };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, IsCancel = true, Margin = new Thickness(4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(choose);
        var layout = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        DockPanel.SetDock(search, Dock.Top);
        layout.Children.Add(search);
        layout.Children.Add(list);
        var window = new Window
        {
            Title = "Добавить шаг в последовательность",
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 620,
            Height = 600,
            Content = layout
        };
        choose.Click += (_, _) => { if (list.SelectedItem is StepChoice) window.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is StepChoice) window.DialogResult = true; };
        window.Loaded += (_, _) => search.Focus();
        return window.ShowDialog() == true ? (list.SelectedItem as StepChoice)?.Row : null;
    }

    private sealed record StepChoice(string Label, DataRowView Row);

    private static int FindRecordIndex(DataGrid grid, object record)
    {
        if (record == null) return 0;
        for (int index = 0; index < grid.Items.Count; index++)
        {
            if (grid.Items[index] is DataRowView row && row.Row.Table.Columns.Contains("Record") &&
                SameValue(row["Record"], record)) return index;
        }
        return -1;
    }

    private static DataRowView FindRow(DataGrid grid, string columnName, object value)
    {
        foreach (var item in grid.Items)
            if (item is DataRowView row && row.Row.Table.Columns.Contains(columnName) && Equals(row[columnName], value))
                return row;
        return null;
    }

    private static Dictionary<string, object> IdentityFor(string tableName, IReadOnlyDictionary<string, object> values)
    {
        if (!JournalKeys.TryGetValue(tableName, out var keys)) return new(StringComparer.OrdinalIgnoreCase);
        return keys.Where(values.ContainsKey).ToDictionary(key => key, key => values[key], StringComparer.OrdinalIgnoreCase);
    }

    private string RaiseJournal(string tableName, ChangeJournalOperationKind kind,
        IReadOnlyDictionary<string, object> before, IReadOnlyDictionary<string, object> after,
        IReadOnlyDictionary<string, object> identity, string description, IReadOnlyList<ChangeJournalRowChange> completeChanges = null)
    {
        try
        {
        bool offline = _offlineSource != null;
        var builder = offline ? null : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString);
        var identityValues = JournalValues(identity);
        var change = new ChangeJournalRowChange
        {
            TableName = tableName,
            Kind = kind,
            Before = JournalValues(before),
            After = JournalValues(after),
            IdentityBefore = kind == ChangeJournalOperationKind.Insert
                ? new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase) : identityValues,
            IdentityAfter = kind == ChangeJournalOperationKind.Delete
                ? new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase) : identityValues
        };
        string identityText = string.Join("; ", (identity ?? new Dictionary<string, object>())
            .Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"));
        ChangeJournalRequested?.Invoke(new ChangeJournalEntry
        {
            Category = "Программа",
            Source = tableName,
            Description = description,
            Details = identityText,
            OperationKind = kind,
            RestoreIncludesAllChanges = completeChanges != null ||
                tableName == ProgramManagementRepository.ProgramTable ||
                (kind == ChangeJournalOperationKind.Update && tableName != ProgramManagementRepository.TransitionTable) ||
                (kind == ChangeJournalOperationKind.Delete && tableName == ProgramManagementRepository.ParameterTable),
            ProjectType = offline ? "Offline" : "Online",
            ProjectId = offline ? _offlineSource.Project.Manifest?.ProjectId ?? "" : "online:" + builder.DataSource.Trim().ToLowerInvariant() + "/" + builder.InitialCatalog.Trim().ToLowerInvariant(),
            ProjectName = offline ? _offlineSource.Project.Manifest?.Name ?? "" : builder.InitialCatalog,
            Server = offline ? "" : builder.DataSource,
            Database = offline ? "" : builder.InitialCatalog,
            Mode = 3,
            UserRole = UserAuthorizationService.CurrentRole?.Name ?? string.Empty,
            EntityType = tableName,
            EntityIdentity = identityText,
            Result = "Успешно",
            Changes = completeChanges?.ToList() ?? new List<ChangeJournalRowChange> { change }
        });
        return null;
        }
        catch (Exception ex)
        {
            return "Операция выполнена, но журнал изменений не записан: " + ex.Message;
        }
    }

    private static string FormatJournalWarning(string warning) =>
        string.IsNullOrWhiteSpace(warning) ? string.Empty : " " + warning;

    private static Dictionary<string, ChangeJournalValue> JournalValues(IReadOnlyDictionary<string, object> values)
        => values?.ToDictionary(pair => pair.Key, pair => ChangeJournalValue.FromObject(pair.Value), StringComparer.OrdinalIgnoreCase)
           ?? new Dictionary<string, ChangeJournalValue>(StringComparer.OrdinalIgnoreCase);

    private bool CanDiscardPendingEdits()
    {
        if (!CommitGridEdits())
        { SetStatus("Проверьте введённые значения перед переключением или операцией."); return false; }
        if (!HasPendingEdits()) return true;
        SetStatus("Есть несохранённые изменения. Сохраните выбранную строку перед обновлением или другой операцией.");
        return false;
    }

    private async Task<bool> CanChangeContextAsync(bool allowDiscard = false)
    {
        if (!allowDiscard && !CanDiscardPendingEdits()) return false;
        if (allowDiscard && !CommitGridEdits())
        { SetStatus("Проверьте введённые значения перед переключением."); return false; }
        bool discardPending = allowDiscard && HasPendingEdits();
        if (discardPending && MessageBox.Show("Переключить контекст и отменить несохранённые изменения в открытых таблицах?",
                "Несохранённые изменения", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            SetStatus("Переключение отменено. Несохранённые изменения остаются открытыми.");
            return false;
        }
        bool ownsChangeLock = !_contextChangePending;
        if (ownsChangeLock) _contextChangePending = true;
        bool?[] enabledBeforeGuard = SetContextGridsEnabled(false);
        try
        {
            if (BeforeContextChangeAsync != null && !await BeforeContextChangeAsync())
            {
                SetStatus("Переключение отменено. Несохранённые изменения остаются открытыми.");
                return false;
            }
            if (discardPending) RejectGridChanges();
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось проверить несохранённые изменения: " + Configurator.Infrastructure.UserErrorMessage.Format(ex));
            return false;
        }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeGuard);
            if (ownsChangeLock) _contextChangePending = false;
        }
        return true;
    }

    private void RejectGridChanges()
    {
        foreach (var grid in new[] { ProgramsGrid, SequencesGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid, ConditionsGrid })
            if (grid.ItemsSource is DataView view) view.Table.RejectChanges();
        RenderProgramFields(ProgramsGrid.SelectedItem as DataRowView);
        if (TransitionsGrid.SelectedItem is DataRowView transition) ShowTransitionConditions(transition);
    }

    private bool CanChangeSelection(DataGrid grid, SelectionChangedEventArgs e)
    {
        if (_revertingSelection) return false;
        if (!CommitGridEdits())
        {
            RevertSelection(grid, e);
            SetStatus("Проверьте введённые значения перед переключением.");
            return false;
        }
        if (!HasPendingEdits()) return true;
        RevertSelection(grid, e);
        SetStatus("Есть несохранённые изменения. Сохраните текущую строку перед переключением.");
        return false;
    }

    private void RevertSelection(DataGrid grid, SelectionChangedEventArgs e)
    {
        var previous = e.RemovedItems.OfType<DataRowView>().FirstOrDefault();
        RestoreSelection(grid, previous);
    }

    private void RestoreSelection(DataGrid grid, DataRowView previous)
    {
        _revertingSelection = true;
        try
        {
            if (previous == null) grid.SelectedIndex = -1;
            else grid.SelectedItem = previous;
        }
        finally { _revertingSelection = false; }
    }

    private bool CommitGridEdits()
    {
        foreach (var input in ProgramFields.Children.OfType<DockPanel>().SelectMany(line => line.Children.OfType<TextBox>()))
        {
            if (input.IsReadOnly || input.Tag is not string column) continue;
            var binding = input.GetBindingExpression(TextBox.TextProperty);
            if (binding?.DataItem is not DataRowView row ||
                string.Equals(input.Text, Convert.ToString(row[column], CultureInfo.CurrentCulture), StringComparison.Ordinal))
                continue;
            binding.UpdateSource();
            if (Validation.GetHasError(input)) return false;
        }
        bool committed = true;
        foreach (var grid in new[] { ProgramsGrid, SequencesGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid, ConditionsGrid })
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            committed &= grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        return committed;
    }

    private bool?[] SetContextGridsEnabled(bool enabled)
    {
        var grids = new[] { ProgramsGrid, SequencesGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid, ConditionsGrid };
        var previous = grids.Select(grid => GetLocalIsEnabled(grid))
            .Concat(new[] { GetLocalIsEnabled(PlcInput), GetLocalIsEnabled(ClassInput) }).ToArray();
        foreach (var grid in grids) grid.IsEnabled = enabled;
        PlcInput.IsEnabled = ClassInput.IsEnabled = enabled;
        Dispatcher.BeginInvoke(new Action(UpdateCommandState));
        return previous;
    }

    private static bool? GetLocalIsEnabled(UIElement element)
    {
        object value = element.ReadLocalValue(UIElement.IsEnabledProperty);
        return ReferenceEquals(value, DependencyProperty.UnsetValue) ? null : value is bool enabled ? enabled : null;
    }

    private void RestoreContextGridsEnabled(bool?[] previous)
    {
        if (previous == null) return;
        var grids = new[] { ProgramsGrid, SequencesGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid, ConditionsGrid };
        for (int index = 0; index < grids.Length && index < previous.Length; index++)
            RestoreLocalIsEnabled(grids[index], previous[index]);
        if (previous.Length > grids.Length) RestoreLocalIsEnabled(PlcInput, previous[grids.Length]);
        if (previous.Length > grids.Length + 1) RestoreLocalIsEnabled(ClassInput, previous[grids.Length + 1]);
        Dispatcher.BeginInvoke(new Action(UpdateCommandState));
    }

    private static void RestoreLocalIsEnabled(UIElement element, bool? value)
    {
        if (value.HasValue) element.IsEnabled = value.Value;
        else element.ClearValue(UIElement.IsEnabledProperty);
    }

    private bool HasPendingEdits()
    {
        foreach (var grid in new[] { ProgramsGrid, SequencesGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid, ConditionsGrid })
            if (grid.ItemsSource is DataView view && view.Table.Rows.Cast<DataRow>().Any(row => row.RowState is DataRowState.Added or DataRowState.Deleted ||
                (row.RowState == DataRowState.Modified && row.Table.Columns.Cast<DataColumn>().Any(column =>
                    !System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(row[column, DataRowVersion.Original], row[column])))))
                return true;
        return false;
    }


    private async void AddParameter_Click(object sender, RoutedEventArgs e) => await ChangeParameterAsync((Button)sender, true);

    private async void DeleteParameter_Click(object sender, RoutedEventArgs e) => await ChangeParameterAsync((Button)sender, false);

    private async Task ChangeParameterAsync(Button button, bool insert)
    {
        if (_loading || _contextChangePending || CurrentContext?.SeqRecordValue == null ||
            !UserAuthorizationService.Require(insert ? Permission.AddData : Permission.DeleteData) || !CanDiscardPendingEdits()) return;
        string tag = (string)button.Tag;
        int? step = tag == "programParams" ? 0 : TryGetSequenceStepNumber(TransitionsGrid.SelectedItem as DataRowView);
        var selected = FindGrid(tag).SelectedItem as DataRowView;
        if (!step.HasValue) { SetStatus("Выберите вхождение шага."); return; }
        if (!insert && selected == null) { SetStatus("Выберите параметр."); return; }
        var context = CopyContext(CurrentContext);
        _contextChangePending = true;
        try
        {
            if (insert)
            {
                var values = await PromptInsertValuesAsync(ProgramManagementRepository.ParameterTable,
                    new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Step_Number", "Record" }, "Новый параметр");
                if (values == null) return;
                int record = await _repository.AddParameterAsync(context, step.Value, values);
                values["PLC"] = context.PlcValue;
                values["PLC_Class_Prog_Number"] = context.ClassNumberValue;
                values["Prog_Record"] = context.RecordValue;
                values["Seq_Record"] = context.SeqRecordValue;
                values["Step_Number"] = step.Value;
                values["Record"] = record;
                RaiseJournal(ProgramManagementRepository.ParameterTable, ChangeJournalOperationKind.Insert, null, values,
                    IdentityFor(ProgramManagementRepository.ParameterTable, values), "Создан параметр");
            }
            else
            {
                var before = ToValues(selected.Row, DataRowVersion.Current);
                if (MessageBox.Show(Window.GetWindow(this), "Удалить выбранный параметр?", "Параметр",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await _repository.DeleteParameterAsync(context, step.Value, GetValue(selected.Row, "Type"), GetValue(selected.Row, "Record"), expected: before);
                RaiseJournal(ProgramManagementRepository.ParameterTable, ChangeJournalOperationKind.Delete, before, null,
                    IdentityFor(ProgramManagementRepository.ParameterTable, before), "Удалён параметр");
            }
            await BindTableAsync(FindGrid(tag), await _repository.GetParametersAsync(context, step.Value));
            SetStatus(insert ? "Параметр создан." : "Параметр удалён.");
        }
        catch (Exception ex)
        {
            SetStatus(Configurator.Infrastructure.UserErrorMessage.Format(ex));
        }
        finally
        {
            _contextChangePending = false;
            UpdateCommandState();
        }
    }

    public Task SaveProgramCardAsync()
    {
        if (DetailsTabs.SelectedItem == SoftKeysTab) return SoftKeysEditor.SaveCurrentAsync();
        if (DetailsTabs.SelectedItem == MessagesTab) return MessagesEditor.SaveCurrentAsync();
        if (DetailsTabs.SelectedItem == QueuesTab) return QueuesEditor.SaveCurrentAsync();
        if (DetailsTabs.SelectedItem != SequenceTab) return SaveEntityAsync(SaveProgramsButton);
        if (SequenceTabs.SelectedItem == RecipesTab) return RecipesEditor.SaveCurrentAsync();
        if (SequenceTabs.SelectedItem == StepDetailsTab) return SaveEntityAsync(SaveStepParamsButton);
        if (SequenceTabs.SelectedItem == SequenceDetailsTab) return SaveEntityAsync(SaveTransitionsButton);
        return SaveEntityAsync(SaveProgramParamsButton);
    }

    private async void EditEntityRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_loading || _contextChangePending || !UserAuthorizationService.Has(Permission.EditData) ||
            sender is not DataGrid grid || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(grid, source) is not DataGridRow { Item: DataRowView selected } ||
            grid.Tag is not string tag || !_nonEditableColumns.TryGetValue(tag, out var protectedColumns)) return;

        var units = Array.Empty<LookupOption>() as IReadOnlyList<LookupOption>;
        if (selected.Row.Table.Columns.Contains("Unit"))
        {
            var context = CurrentContext;
            try
            {
                var field = new Configurator.Core.Architecture.FieldDefinition
                { Name = "Unit", LookupTable = "dbo.Units", LookupKeyField = "Unit_ID" };
                units = _offlineSource != null
                    ? OfflineLookupOptionsService.Get(_offlineSource.Project, "Program", ProgramManagementRepository.ParameterTable, field)
                    : await new LookupOptionsService(new DatabaseService(_connectionString)).TryGetAsync(
                        "Program", ProgramManagementRepository.ParameterTable, "Unit",
                        explicitLookupTable: field.LookupTable, explicitLookupKeyField: field.LookupKeyField);
            }
            catch (Exception ex) { Configurator.Infrastructure.UserErrorMessage.Format(ex); }
            if (!ReferenceEquals(context, CurrentContext) || !ReferenceEquals(grid.SelectedItem, selected) || _contextChangePending) return;
        }
        var draft = selected.Row.Table.Clone();
        draft.ImportRow(selected.Row);
        var fields = new UniformGrid { Columns = 3, Margin = new Thickness(6, 8, 6, 12) };
        var inputs = new List<Control>();
        foreach (DataColumn column in draft.Columns)
        {
            bool readOnly = column.ReadOnly || protectedColumns.Contains(column.ColumnName) ||
                ProtectedContextColumns.Contains(column.ColumnName) || column.ColumnName is "Record" or "Type";
            var field = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
            Control input;
            DependencyProperty property;
            if (!readOnly && column.ColumnName.Equals("Unit", StringComparison.OrdinalIgnoreCase) && units.Count > 0)
            {
                var choices = units.ToList();
                string current = Convert.ToString(selected[column.ColumnName], CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(current) && !choices.Any(choice => choice.Key == current))
                    choices.Insert(0, new LookupOption { Key = current, Display = current + " · текущее значение" });
                if (column.AllowDBNull) choices.Insert(0, new LookupOption { Key = "", Display = "(не задано)" });
                var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(LookupOption.Display), SelectedValuePath = nameof(LookupOption.Key),
                    SelectedValue = current };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedValue is string key)
                        draft.Rows[0][column.ColumnName] = key.Length == 0 ? DBNull.Value : key;
                };
                input = combo;
                property = null;
            }
            else
            {
                input = new TextBox { IsReadOnly = readOnly };
                property = TextBox.TextProperty;
            }
            if (property != null) input.SetBinding(property, new Binding($"[{column.ColumnName}]")
            {
                Source = draft.DefaultView[0],
                Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                ValidatesOnExceptions = true,
                TargetNullValue = string.Empty
            });
            field.Children.Add(new Label { Content = column.ColumnName, Target = input, Padding = new Thickness(0, 0, 0, 2) });
            field.Children.Add(input);
            fields.Children.Add(field);
            inputs.Add(input);
        }
        var save = new Button { Content = "Сохранить", IsDefault = true, Margin = new Thickness(4) };
        save.SetResourceReference(StyleProperty, "BtnSaveObject");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(4) });
        actions.Children.Add(save);
        var layout = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        var scroll = new ScrollViewer
        {
            Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.SizeChanged += (_, args) => fields.Columns = Math.Clamp((int)(args.NewSize.Width / 255), 1, 3);
        layout.Children.Add(scroll);
        var dialog = new Window
        {
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = tag == "sequences" ? "Изменить последовательность" : "Изменить параметр",
            Width = Math.Min(1080, SystemParameters.WorkArea.Width),
            Height = Math.Min(640, SystemParameters.WorkArea.Height),
            Content = layout
        };
        save.Click += (_, _) =>
        {
            if (!inputs.Any(Validation.GetHasError)) dialog.DialogResult = true;
        };
        e.Handled = true;
        if (dialog.ShowDialog() != true || draft.Rows[0].RowState != DataRowState.Modified) return;
        var button = tag == "sequences" ? SaveSequencesButton :
            tag == "programParams" ? SaveProgramParamsButton : SaveStepParamsButton;
        await SaveEntityAsync(button, draft.Rows[0]);
    }

    private async void SaveEntityButton_Click(object sender, RoutedEventArgs e)
        => await SaveEntityAsync((Button)sender);

    private async Task SaveEntityAsync(Button button, DataRow draft = null)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.EditData)) return;
        if (!TableByGrid.TryGetValue(Convert.ToString(button.Tag), out string tableName)) return;
        var grid = FindGrid(button.Tag as string);
        if (grid?.SelectedItem is not DataRowView selected) { SetStatus("Выберите строку для сохранения."); return; }

        if (grid == ProgramsGrid && !CommitGridEdits()) return;
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        if (!grid.CommitEdit(DataGridEditingUnit.Row, true))
        { SetStatus("Проверьте введённые значения: текущая строка не прошла проверку."); return; }
        if (tableName == ProgramManagementRepository.TransitionTable && ConditionsGrid.ItemsSource is DataView conditions)
        {
            ConditionsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            if (!ConditionsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            { SetStatus("Проверьте введённые условия перехода."); return; }
            try
            {
                var choices = DestinationChoices().Select(choice => choice.Value).ToHashSet();
                foreach (DataRow condition in conditions.Table.Rows)
                    if (condition.RowState == DataRowState.Modified &&
                        !Equals(condition[1, DataRowVersion.Original], condition[1]) && !choices.Contains(condition[1]))
                        throw new InvalidOperationException("Выберите назначение из текущей последовательности.");
                ProgramTransitionConditions.Apply(conditions.Table, selected.Row);
            }
            catch (Exception ex) { SetStatus("Не удалось применить условия: " + ex.Message); return; }
        }
        var row = selected.Row;
        if (row.RowState == DataRowState.Added)
        { SetStatus("Создание строк в этом экране пока отключено: схема round-trip для вставки не подтверждена."); return; }
        if (!row.HasVersion(DataRowVersion.Original))
        { SetStatus("Нет исходной версии строки для безопасного сохранения."); return; }

        var original = ToValues(row, DataRowVersion.Original);
        var updated = ToValues(draft ?? row, DataRowVersion.Current);
        _contextChangePending = true;
        bool?[] enabledBeforeSave = SetContextGridsEnabled(false);
        try
        {
            await _repository.SaveRowAsync(tableName, original, updated);
            if (draft != null)
                foreach (DataColumn column in row.Table.Columns)
                    if (!Equals(row[column], draft[column.ColumnName])) row[column] = draft[column.ColumnName];
            row.AcceptChanges();

            if (tableName == ProgramManagementRepository.TransitionTable) ShowTransitionConditions(selected);
            RefreshTreeLabels();
            string journalWarning = RaiseJournal(tableName, ChangeJournalOperationKind.Update, original, updated,
                IdentityFor(tableName, updated), "Изменена запись Program/Step");
            SetStatus("Изменения сохранены. Строка проверена на отсутствие параллельной правки." + FormatJournalWarning(journalWarning));
            if (tableName == ProgramManagementRepository.ProgramTable) ProgramSaved?.Invoke(row);
        }
        catch (Exception ex) { SetStatus("Не удалось сохранить строку: " + Configurator.Infrastructure.UserErrorMessage.Format(ex)); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeSave);
            _contextChangePending = false;
        }
    }

    private sealed record TransitionDestination(object Value, string Label);

    private IEnumerable<TransitionDestination> DestinationChoices()
    {
        yield return new TransitionDestination(DBNull.Value, "—");
        yield return new TransitionDestination(0, "0 — без назначения");
        foreach (var row in TransitionsGrid.Items.OfType<DataRowView>())
        {
            if (TryGetSequenceStepNumber(row) is not > 0) continue;
            int number = TryGetSequenceStepNumber(row).Value;
            string name = Convert.ToString(GetValue(row.Row, "Name_L1") ?? GetValue(row.Row, "NameL1"));
            yield return new TransitionDestination(number, string.IsNullOrWhiteSpace(name) ? $"{number}" : $"{number} · {name}");
        }
    }

    private void EntityGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        string gridKey = Convert.ToString(grid.Tag);
        if (gridKey != "programs" && (e.PropertyName is "PLC" or "PLC_Class_Prog_Number" or "Prog_Record" or "Seq_Record"))
        {
            e.Cancel = true;
            return;
        }
        if (gridKey == "transitions" && e.PropertyName.StartsWith("Trans", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true; // Destinations and masks are edited together in the conditions grid.
            return;
        }
        if (e.Column is DataGridTextColumn text)
        {
            var style = new Style(typeof(TextBlock), text.ElementStyle);
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 3, 8, 3)));
            text.ElementStyle = style;
        }
        bool parameterType = e.PropertyName.Equals("Type", StringComparison.OrdinalIgnoreCase) &&
            (gridKey.Equals("stepParams", StringComparison.OrdinalIgnoreCase) || gridKey.Equals("programParams", StringComparison.OrdinalIgnoreCase));
        bool protectedColumn = ProtectedContextColumns.Contains(e.PropertyName) || parameterType ||
            (_nonEditableColumns.TryGetValue(gridKey, out var nonEditable) && nonEditable.Contains(e.PropertyName));
        if (!protectedColumn) return;
        e.Column.IsReadOnly = true;
    }

    private bool TryParseContextInputs(out object plc, out object classNumber)
    {
        plc = ParseInput(PlcInput.Text);
        classNumber = ParseInput(ClassInput.Text);
        if (plc == null || classNumber == null)
        {
            SetStatus("Укажите значения PLC и класса программы.");
            return false;
        }
        return true;
    }

    private static object ParseInput(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return number;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static object GetValue(DataRow row, string column)
        => row.Table.Columns.Contains(column) && row[column] != DBNull.Value ? row[column] : null;

    private static ProgramContext CopyContext(ProgramContext context) => new()
    {
        PLC = context.PLC,
        PlcValue = context.PlcValue,
        ClassNumberValue = context.ClassNumberValue,
        RecordValue = context.RecordValue,
        AreaValue = context.AreaValue,
        SeqRecordValue = context.SeqRecordValue
    };

    private static Dictionary<string, object> ToValues(DataRow row, DataRowVersion version)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (DataColumn column in row.Table.Columns)
            result[column.ColumnName] = row[column, version];
        return result;
    }

    private DataGrid FindGrid(string key) => key switch
    {
        "programs" => ProgramsGrid,
        "sequences" => SequencesGrid,
        "transitions" => TransitionsGrid,
        "programParams" => ProgramParamsGrid,
        "stepParams" => StepParamsGrid,
        _ => null
    };

    private void ClearDetails()
    {
        TransitionsGrid.ItemsSource = null;
        ConditionsGrid.ItemsSource = null;
        ProgramParamsGrid.ItemsSource = null;
        StepParamsGrid.ItemsSource = null;
    }

    private async Task BindTableAsync(DataGrid grid, DataTable table)
    {
        string key = Convert.ToString(grid.Tag) ?? string.Empty;
        if (TableByGrid.TryGetValue(key, out string tableName))
            _nonEditableColumns[key] = await _repository.GetNonEditableColumnsAsync(tableName);
        grid.ItemsSource = table.DefaultView;
    }

    private void PublishContext() => ContextChanged?.Invoke(CurrentContext);
    private void SetStatus(string message)
    {
        StatusText.Text = message;
        UpdateCommandState();
    }
}
