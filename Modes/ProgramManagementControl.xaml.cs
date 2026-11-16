using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Configurator;

public partial class ProgramManagementControl : UserControl
{
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
        ["steps"] = ProgramManagementRepository.StepTable,
        ["transitions"] = ProgramManagementRepository.TransitionTable,
        ["programParams"] = ProgramManagementRepository.ParameterTable,
        ["stepParams"] = ProgramManagementRepository.ParameterTable
    };

    private static readonly Dictionary<string, string[]> JournalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProgramManagementRepository.ProgramTable] = new[] { "PLC", "PLC_Class_Number", "Record" },
        [ProgramManagementRepository.SequenceTable] = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Record" },
        [ProgramManagementRepository.StepTable] = new[] { "PLC", "PLC_Class_Number", "Record" },
        [ProgramManagementRepository.TransitionTable] = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number" },
        [ProgramManagementRepository.ParameterTable] = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "Step_Number", "Type", "Record" }
    };

    private ProgramManagementRepository _repository;
    private string _connectionString;
    private bool _loading;
    private bool _revertingSelection;
    private bool _contextChangePending;
    private bool _approveNextProgramSelection;
    private bool _approveNextSequenceSelection;
    private readonly Dictionary<string, HashSet<string>> _nonEditableColumns = new(StringComparer.OrdinalIgnoreCase);

    public ProgramContext CurrentContext { get; private set; }
    public Func<Task<bool>> BeforeContextChangeAsync { get; set; }
    public event Action<ProgramContext> ContextChanged;
    public event Action<ChangeJournalEntry> ChangeJournalRequested;

    public ProgramManagementControl()
    {
        InitializeComponent();
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
        foreach (var grid in new[] { ProgramsGrid, SequencesGrid, StepsGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid })
            if (grid != null) grid.IsReadOnly = !canEdit;
    }

    public async void Configure(string connectionString, object plcValue, object programClassValue)
    {
        if (_loading || _contextChangePending)
        {
            SetStatus("Нельзя сменить контекст до завершения текущего действия.");
            return;
        }
        if (_repository != null && !await CanChangeContextAsync()) return;

        _loading = true;
        foreach (var grid in new[] { ProgramsGrid, SequencesGrid, StepsGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid })
            grid.ItemsSource = null;
        CurrentContext = null;
        PublishContext();
        ContextText.Text = string.Empty;
        _connectionString = connectionString;
        _repository = new ProgramManagementRepository(connectionString);
        PlcInput.Text = Convert.ToString(plcValue, CultureInfo.InvariantCulture) ?? string.Empty;
        ClassInput.Text = Convert.ToString(programClassValue, CultureInfo.InvariantCulture) ?? string.Empty;
        _loading = false;
        await LoadProgramsAsync();
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        await LoadProgramsAsync(requireContextApproval: true);
    }

    private void DiscardChangesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        CommitGridEdits();
        if (!HasPendingEdits()) { SetStatus("Несохранённых изменений нет."); return; }
        if (MessageBox.Show("Отменить все несохранённые изменения в открытых таблицах?",
                "Отмена правок", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var grid in new[] { ProgramsGrid, SequencesGrid, StepsGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid })
            if (grid.ItemsSource is DataView view) view.Table.RejectChanges();
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
        bool[] enabledBeforeLoad = SetContextGridsEnabled(false);
        _loading = true;
        bool selectFirst = false;
        try
        {
            var table = await _repository.GetProgramsAsync(plc, classNumber);
            await BindTableAsync(ProgramsGrid, table);
            SequencesGrid.ItemsSource = null;
            StepsGrid.ItemsSource = null;
            ClearDetails();
            CurrentContext = null;
            PublishContext();
            ContextText.Text = string.Empty;
            if (table.Rows.Count > 0)
            {
                selectFirst = true;
                SetStatus($"Загружено программ: {table.Rows.Count}." + FormatJournalWarning(postCommitWarning));
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
            _approveNextProgramSelection = true;
            ProgramsGrid.SelectedIndex = FindRecordIndex(ProgramsGrid, selectRecord);
            _approveNextProgramSelection = false;
        }
        return true;
    }

    private async void ProgramsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _revertingSelection || ProgramsGrid.SelectedItem is not DataRowView row) return;
        var previousSelection = e.RemovedItems.OfType<DataRowView>().FirstOrDefault();
        bool preapproved = _approveNextProgramSelection;
        _approveNextProgramSelection = false;
        if (_contextChangePending && !preapproved)
        {
            RestoreSelection(ProgramsGrid, previousSelection);
            return;
        }
        if (!CommitGridEdits() || HasPendingEdits())
        {
            RestoreSelection(ProgramsGrid, previousSelection);
            SetStatus("Есть несохранённые изменения или ошибки ввода. Сохраните их перед переключением программы.");
            return;
        }
        bool ownsChangeLock = !_contextChangePending;
        if (ownsChangeLock) _contextChangePending = true;
        bool[] enabledBeforeChange = SetContextGridsEnabled(false);
        try
        {
            if (!preapproved && !await CanChangeContextAsync())
            {
                RestoreSelection(ProgramsGrid, previousSelection);
                return;
            }
            var plc = GetValue(row.Row, "PLC") ?? ParseInput(PlcInput.Text);
            var classNumber = GetValue(row.Row, "PLC_Class_Number") ?? GetValue(row.Row, "PLC_Class_Prog_Number") ?? ParseInput(ClassInput.Text);
            if (CurrentContext != null && Equals(CurrentContext.RecordValue, GetValue(row.Row, "Record"))
                && Equals(CurrentContext.PlcValue, plc) && Equals(CurrentContext.ClassNumberValue, classNumber)) return;
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
            await LoadSequencesAsync(contextChangeApproved: true);
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
        bool[] enabledBeforeLoad = SetContextGridsEnabled(false);
        _loading = true;
        bool selectFirst = false;
        try
        {
            var table = await _repository.GetSequencesAsync(CurrentContext);
            await BindTableAsync(SequencesGrid, table);
            StepsGrid.ItemsSource = null;
            ClearDetails();
            if (table.Rows.Count > 0)
            {
                selectFirst = true;
                SetStatus($"Загружено последовательностей: {table.Rows.Count}." + FormatJournalWarning(postCommitWarning));
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
            _approveNextSequenceSelection = contextChangeApproved;
            SequencesGrid.SelectedIndex = FindRecordIndex(SequencesGrid, selectRecord);
            _approveNextSequenceSelection = false;
        }
        return true;
    }

    private async void SequencesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _revertingSelection || CurrentContext == null || SequencesGrid.SelectedItem is not DataRowView row) return;
        var previousSelection = e.RemovedItems.OfType<DataRowView>().FirstOrDefault();
        bool preapproved = _approveNextSequenceSelection;
        _approveNextSequenceSelection = false;
        if (_contextChangePending && !preapproved)
        {
            RestoreSelection(SequencesGrid, previousSelection);
            return;
        }
        object sequenceRecord = GetValue(row.Row, "Record") ?? GetValue(row.Row, "Seq_Record");
        if (Equals(CurrentContext.SeqRecordValue, sequenceRecord)) return;
        if (!CommitGridEdits() || HasPendingEdits())
        {
            RestoreSelection(SequencesGrid, previousSelection);
            SetStatus("Есть несохранённые изменения или ошибки ввода. Сохраните их перед переключением последовательности.");
            return;
        }
        bool ownsChangeLock = !_contextChangePending;
        if (ownsChangeLock) _contextChangePending = true;
        bool[] enabledBeforeChange = SetContextGridsEnabled(false);
        try
        {
            if (!preapproved && !await CanChangeContextAsync())
            {
                RestoreSelection(SequencesGrid, previousSelection);
                return;
            }
            CurrentContext.SeqRecordValue = sequenceRecord;
            PublishContext();
            ContextText.Text = CurrentContext.ContextText + "  ·  Seq=" + Convert.ToString(CurrentContext.SeqRecordValue, CultureInfo.InvariantCulture);
            await LoadSequenceChildrenAsync();
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
        bool[] enabledBeforeLoad = SetContextGridsEnabled(false);
        StepsGrid.ItemsSource = null;
        ClearDetails();
        _loading = true;
        try
        {
            var steps = await _repository.GetStepsAsync(CurrentContext);
            var transitions = await _repository.GetTransitionsAsync(CurrentContext);
            var programParams = await _repository.GetParametersAsync(CurrentContext, 0);
            await BindTableAsync(StepsGrid, steps);
            await BindTableAsync(TransitionsGrid, transitions);
            await BindTableAsync(ProgramParamsGrid, programParams);
            StepParamsGrid.ItemsSource = null;
            SetStatus($"Загружено шагов: {steps.Rows.Count}; переходов: {transitions.Rows.Count}; параметров программы: {programParams.Rows.Count}." + FormatJournalWarning(postCommitWarning));
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

    private async void LoadStepParamsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (CurrentContext?.SeqRecordValue == null) { SetStatus("Сначала выберите программу и последовательность."); return; }
        if (!CanDiscardPendingEdits()) return;
        if (!int.TryParse(StepNumberInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stepNumber) || stepNumber <= 0)
        { SetStatus("Введите положительный номер шага."); return; }
        var targetContext = CopyContext(CurrentContext);
        _contextChangePending = true;
        bool[] enabledBeforeLoad = SetContextGridsEnabled(false);
        try
        {
            var table = await _repository.GetParametersAsync(targetContext, stepNumber);
            await BindTableAsync(StepParamsGrid, table);
            SetStatus($"Загружено параметров выбранного шага: {table.Rows.Count}.");
        }
        catch (Exception ex) { SetStatus("Не удалось загрузить параметры шага: " + ex.Message); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeLoad);
            _contextChangePending = false;
        }
    }

    private void TransitionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _contextChangePending || TransitionsGrid.SelectedItem is not DataRowView row || !row.Row.Table.Columns.Contains("SeqStep_Number")) return;
        var value = row["SeqStep_Number"];
        if (value == DBNull.Value) return;
        StepNumberInput.Text = Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async void AddProgramButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.AddData) || !TryParseContextInputs(out var plc, out var classNumber)) return;
        if (!CanDiscardPendingEdits()) return;
        _contextChangePending = true;
        bool[] enabledBeforeOperation = SetContextGridsEnabled(false);
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
        catch (Exception ex) { SetStatus("Не удалось создать программу: " + ex.Message); }
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
        bool[] enabledBeforeOperation = SetContextGridsEnabled(false);
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
        catch (Exception ex) { SetStatus("Не удалось создать последовательность: " + ex.Message); }
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
        bool[] enabledBeforeOperation = SetContextGridsEnabled(false);
        try
        {
            var choices = await _repository.GetStepChoicesAsync(targetContext.PlcValue);
            var selectedStep = ChooseStep(choices);
            if (selectedStep == null) return;
            var extraFields = await PromptInsertValuesAsync(ProgramManagementRepository.TransitionTable,
                new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record", "Seq_Record", "SeqStep_Number", "PLC_Class_Step_Number", "Step_Record" },
                "Параметры перехода нового шага");
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
        catch (Exception ex) { SetStatus("Не удалось добавить шаг: " + ex.Message); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeOperation);
            _contextChangePending = false;
        }
    }

    private async void DeleteProgramButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.DeleteData)) return;
        if (CurrentContext?.RecordValue == null) { SetStatus("Выберите программу для удаления."); return; }
        var targetContext = CopyContext(CurrentContext);
        if (MessageBox.Show("Программа будет удалена только если у неё нет последовательностей, параметров и других зависимых данных. Продолжить?",
                "Удаление программы", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await CanChangeContextAsync()) return;
        var deletedProgram = ProgramsGrid.SelectedItem as DataRowView;
        var deletedProgramValues = deletedProgram == null ? null : ToValues(deletedProgram.Row, DataRowVersion.Current);
        bool ownsChangeLock = false;
        bool[] enabledBeforeOperation = null;
        try
        {
            _contextChangePending = true;
            ownsChangeLock = true;
            enabledBeforeOperation = SetContextGridsEnabled(false);
            await _repository.DeleteProgramAsync(targetContext);
            string journalWarning = null;
            if (deletedProgramValues != null)
                journalWarning = RaiseJournal(ProgramManagementRepository.ProgramTable, ChangeJournalOperationKind.Delete, deletedProgramValues,
                    null, IdentityFor(ProgramManagementRepository.ProgramTable, deletedProgramValues), "Удалена программа");
            bool refreshed = await LoadProgramsAsync(afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus("Программа удалена." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Программа не удалена: " + ex.Message); }
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
        if (MessageBox.Show("Последовательность будет удалена только если у неё нет шагов и связанных параметров, рецептов или выборов очереди. Продолжить?",
                "Удаление последовательности", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await CanChangeContextAsync()) return;
        var deletedSequenceValues = ToValues(row.Row, DataRowVersion.Current);
        bool ownsChangeLock = false;
        bool[] enabledBeforeOperation = null;
        try
        {
            _contextChangePending = true;
            ownsChangeLock = true;
            enabledBeforeOperation = SetContextGridsEnabled(false);
            await _repository.DeleteSequenceAsync(targetContext, sequenceRecord);
            string journalWarning = RaiseJournal(ProgramManagementRepository.SequenceTable, ChangeJournalOperationKind.Delete, deletedSequenceValues,
                null, IdentityFor(ProgramManagementRepository.SequenceTable, deletedSequenceValues), "Удалена последовательность");
            bool refreshed = await LoadSequencesAsync(contextChangeApproved: true, afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus($"Последовательность Record={sequenceRecord} удалена." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Последовательность не удалена: " + ex.Message); }
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
        if (MessageBox.Show("Шаг будет удалён из последовательности только если на него не ссылаются переходы и нет зависимых параметров, рецептов или выборов очереди. Продолжить?",
                "Удаление шага", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var deletedStepValues = ToValues(row.Row, DataRowVersion.Current);
        _contextChangePending = true;
        bool[] enabledBeforeOperation = SetContextGridsEnabled(false);
        try
        {
            await _repository.DeleteStepFromSequenceAsync(targetContext, stepNumber);
            string journalWarning = RaiseJournal(ProgramManagementRepository.TransitionTable, ChangeJournalOperationKind.Delete, deletedStepValues,
                null, IdentityFor(ProgramManagementRepository.TransitionTable, deletedStepValues), "Удалён шаг из последовательности");
            bool refreshed = await LoadSequenceChildrenAsync(afterCommittedChange: true, postCommitWarning: journalWarning);
            if (refreshed) SetStatus($"Шаг {stepNumber} удалён из последовательности." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Шаг не удалён: " + ex.Message); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeOperation);
            _contextChangePending = false;
        }
    }

    private async Task<Dictionary<string, object>> PromptInsertValuesAsync(string tableName,
        IEnumerable<string> generatedColumns, string title)
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
            panel.Children.Add(editor);
            editors.Add((column, editor));
        }
        if (fields.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Все остальные поля будут взяты из значений базы данных или оставлены пустыми." });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Создать", IsDefault = true, MinWidth = 90, Margin = new Thickness(4) };
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

        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, editor) in editors)
        {
            string text = editor.Text?.Trim() ?? string.Empty;
            bool required = !column.IsNullable && !column.HasDefault || column.Name.Equals("Name_L1", StringComparison.OrdinalIgnoreCase);
            if (text.Length == 0)
            {
                if (required) throw new InvalidOperationException($"Заполните обязательное поле {column.Name}.");
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
        var grid = new DataGrid
        {
            ItemsSource = choices.DefaultView,
            AutoGenerateColumns = true,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        var choose = new Button { Content = "Выбрать шаг", MinWidth = 100, IsDefault = true, Margin = new Thickness(4) };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, IsCancel = true, Margin = new Thickness(4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(choose);
        var layout = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(grid);
        var window = new Window
        {
            Title = "Выберите шаг из Prog_Step",
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 900,
            Height = 600,
            Content = layout
        };
        choose.Click += (_, _) =>
        {
            if (grid.SelectedItem == null) return;
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? grid.SelectedItem as DataRowView : null;
    }

    private static int FindRecordIndex(DataGrid grid, object record)
    {
        if (record == null) return 0;
        for (int index = 0; index < grid.Items.Count; index++)
        {
            if (grid.Items[index] is DataRowView row && row.Row.Table.Columns.Contains("Record") &&
                Equals(row["Record"], record)) return index;
        }
        return 0;
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
        IReadOnlyDictionary<string, object> identity, string description)
    {
        try
        {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString);
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
            RestoreIncludesAllChanges = false,
            ProjectType = "Online",
            ProjectId = "online:" + builder.DataSource.Trim().ToLowerInvariant() + "/" + builder.InitialCatalog.Trim().ToLowerInvariant(),
            ProjectName = builder.InitialCatalog,
            Server = builder.DataSource,
            Database = builder.InitialCatalog,
            Mode = 3,
            UserRole = UserAuthorizationService.CurrentRole?.Name ?? string.Empty,
            EntityType = tableName,
            EntityIdentity = identityText,
            Result = "Успешно",
            Changes = new List<ChangeJournalRowChange> { change }
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

    private async Task<bool> CanChangeContextAsync()
    {
        if (!CanDiscardPendingEdits()) return false;
        bool ownsChangeLock = !_contextChangePending;
        if (ownsChangeLock) _contextChangePending = true;
        bool[] enabledBeforeGuard = SetContextGridsEnabled(false);
        try
        {
            if (BeforeContextChangeAsync != null && !await BeforeContextChangeAsync())
            {
                SetStatus("Переключение отменено. Несохранённые изменения остаются открытыми.");
                return false;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось проверить несохранённые изменения: " + ex.Message);
            return false;
        }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeGuard);
            if (ownsChangeLock) _contextChangePending = false;
        }
        return true;
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
        bool committed = true;
        foreach (var grid in new[] { ProgramsGrid, SequencesGrid, StepsGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid })
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            committed &= grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        return committed;
    }

    private bool[] SetContextGridsEnabled(bool enabled)
    {
        var grids = new[] { ProgramsGrid, SequencesGrid, StepsGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid };
        var previous = grids.Select(grid => grid.IsEnabled).Concat(new[] { PlcInput.IsEnabled, ClassInput.IsEnabled }).ToArray();
        foreach (var grid in grids) grid.IsEnabled = enabled;
        PlcInput.IsEnabled = ClassInput.IsEnabled = enabled;
        return previous;
    }

    private void RestoreContextGridsEnabled(bool[] previous)
    {
        if (previous == null) return;
        var grids = new[] { ProgramsGrid, SequencesGrid, StepsGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid };
        for (int index = 0; index < grids.Length && index < previous.Length; index++)
            grids[index].IsEnabled = previous[index];
        if (previous.Length > grids.Length) PlcInput.IsEnabled = previous[grids.Length];
        if (previous.Length > grids.Length + 1) ClassInput.IsEnabled = previous[grids.Length + 1];
    }

    private bool HasPendingEdits()
    {
        foreach (var grid in new[] { ProgramsGrid, SequencesGrid, StepsGrid, TransitionsGrid, ProgramParamsGrid, StepParamsGrid })
            if (grid.ItemsSource is DataView view && view.Table.Rows.Cast<DataRow>().Any(row => row.RowState is DataRowState.Modified or DataRowState.Added or DataRowState.Deleted))
                return true;
        return false;
    }

    private async void SaveEntityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _contextChangePending) return;
        if (!UserAuthorizationService.Require(Permission.EditData)) return;
        if (sender is not Button button || !TableByGrid.TryGetValue(Convert.ToString(button.Tag), out string tableName)) return;
        var grid = FindGrid(button.Tag as string);
        if (grid?.SelectedItem is not DataRowView selected) { SetStatus("Выберите строку для сохранения."); return; }

        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        if (!grid.CommitEdit(DataGridEditingUnit.Row, true))
        { SetStatus("Проверьте введённые значения: текущая строка не прошла проверку."); return; }
        var row = selected.Row;
        if (row.RowState == DataRowState.Added)
        { SetStatus("Создание строк в этом экране пока отключено: схема round-trip для вставки не подтверждена."); return; }
        if (!row.HasVersion(DataRowVersion.Original))
        { SetStatus("Нет исходной версии строки для безопасного сохранения."); return; }

        var original = ToValues(row, DataRowVersion.Original);
        var updated = ToValues(row, DataRowVersion.Current);
        _contextChangePending = true;
        bool[] enabledBeforeSave = SetContextGridsEnabled(false);
        try
        {
            await _repository.SaveRowAsync(tableName, original, updated);
            row.AcceptChanges();
            string journalWarning = RaiseJournal(tableName, ChangeJournalOperationKind.Update, original, updated,
                IdentityFor(tableName, updated), "Изменена запись Program/Step");
            SetStatus("Изменения сохранены. Строка проверена на отсутствие параллельной правки." + FormatJournalWarning(journalWarning));
        }
        catch (Exception ex) { SetStatus("Не удалось сохранить строку: " + ex.Message); }
        finally
        {
            RestoreContextGridsEnabled(enabledBeforeSave);
            _contextChangePending = false;
        }
    }

    private void EntityGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        string gridKey = Convert.ToString(grid.Tag);
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
        "steps" => StepsGrid,
        "transitions" => TransitionsGrid,
        "programParams" => ProgramParamsGrid,
        "stepParams" => StepParamsGrid,
        _ => null
    };

    private void ClearDetails()
    {
        TransitionsGrid.ItemsSource = null;
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
    private void SetStatus(string message) => StatusText.Text = message;
}
