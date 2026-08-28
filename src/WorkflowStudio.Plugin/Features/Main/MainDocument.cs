using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Constants;
using WorkflowStudio.Workflows;

namespace WorkflowStudio.Features.Main;

/// <summary>Workflow Studio 的非持久化 Document 与 UI 状态适配器。</summary>
/// <remarks>
/// 编辑规则属于 WorkflowEditorCoordinator，Secret/取消/执行属于 WorkflowRunSession；本类型只维护
/// Avalonia 绑定所需的可观察集合、命令可用性和 Document 生命周期。
/// </remarks>
public sealed partial class MainDocument :
    ObservableObject,
    IPluginDocument,
    IWorkbenchDocumentCommandTarget,
    IDisposable
{
    private readonly IWorkflowEditorCoordinator _editor;
    private readonly IWorkflowRunSession _runSession;
    private readonly IDocumentLifetime _lifetime;
    private WorkflowActionCatalogSnapshot? _catalog;
    private CancellationTokenRegistration _closingRegistration;
    private DocumentPresentationState _presentation = new("Workflow Studio");
    private int _closing;
    private int _workbenchRunActive;
    private bool _disposed;

    [ObservableProperty] private string _summary = "临时手工工作流";
    [ObservableProperty] private string _definitionJson = string.Empty;
    [ObservableProperty] private string _secretName = "session-key";
    [ObservableProperty] private string _secretValue = string.Empty;
    [ObservableProperty] private WorkflowActionChoice? _selectedAction;
    [ObservableProperty] private WorkflowStepEditor? _selectedStep;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canExecute;
    [ObservableProperty] private string _riskSummary = "尚未验证。";
    [ObservableProperty] private string _runStatus = "尚未执行。";

    public MainDocument(
        IWorkflowEditorCoordinator editor,
        IWorkflowRunSession runSession,
        IDocumentLifetime lifetime)
    {
        _editor = editor;
        _runSession = runSession;
        _lifetime = lifetime;
        RefreshCatalogCommand = new RelayCommand(RefreshCatalog, () => !IsRunning);
        AddStepCommand = new RelayCommand(AddStep, () => SelectedAction is not null && !IsRunning);
        RemoveStepCommand = new RelayCommand(RemoveStep, () => SelectedStep is not null && !IsRunning);
        MoveStepUpCommand = new RelayCommand(() => MoveStep(-1), () => SelectedStep is not null && !IsRunning);
        MoveStepDownCommand = new RelayCommand(() => MoveStep(1), () => SelectedStep is not null && !IsRunning);
        ValidateCommand = new RelayCommand(ValidateDefinition, () => !IsRunning);
        ImportCommand = new RelayCommand(ImportDefinition, () => !IsRunning);
        ExportCommand = new RelayCommand(ExportDefinition, () => !IsRunning);
        StoreSecretCommand = new RelayCommand(StoreSecret, () => !IsRunning);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanExecute && !IsRunning);
        CancelCommand = new RelayCommand(_runSession.Cancel, () => IsRunning);
    }

    public ObservableCollection<WorkflowActionChoice> AvailableActions { get; } = [];
    public ObservableCollection<WorkflowStepEditor> Steps { get; } = [];
    public ObservableCollection<WorkflowValidationMessage> ValidationMessages { get; } = [];
    public ObservableCollection<WorkflowRunMessage> RunMessages { get; } = [];

    public ICommand RefreshCatalogCommand { get; }
    public ICommand AddStepCommand { get; }
    public ICommand RemoveStepCommand { get; }
    public ICommand MoveStepUpCommand { get; }
    public ICommand MoveStepDownCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand StoreSecretCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public ICommand CancelCommand { get; }

    public DocumentPresentationState Presentation => _presentation;
    public event EventHandler? PresentationChanged;

    /// <summary>当当前 Studio Document 实例中的工作台命令状态变化时发生。</summary>
    /// <remarks>
    /// 一次事件只携带一条命令身份。事件可能由运行任务的延续或 Document 关闭线程触发；
    /// Host 负责切换 UI 线程、去重并在活动目标切换时成对退订。本实例不持有菜单、快捷键或 Host 对象。
    /// </remarks>
    public event EventHandler<WorkbenchCommandStateChangedEventArgs>? CommandStateChanged;

    public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(activation.Title))
        {
            _presentation = new(activation.Title);
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
        _closingRegistration = _lifetime.ClosingToken.Register(CloseSession);
        RefreshCatalog();
        return ValueTask.CompletedTask;
    }

    /// <summary>Standalone 自检和单元测试使用的窄入口；正常 UI 仍通过命令编辑。</summary>
    public void LoadDemonstrationWorkflow(string secretValue)
    {
        EnsureCatalog();
        _runSession.SetSecret("session-key", secretValue);
        Steps.Clear();
        AddStepByActionSuffix("generate-items", "generate", null,
            new Dictionary<string, (WorkflowArgumentMode, string)>
            {
                ["count"] = (WorkflowArgumentMode.Constant, "3"),
                ["prefix"] = (WorkflowArgumentMode.Constant, "\"条目\"")
            });
        AddStepByActionSuffix("format-item", "format", "${generate.result.items}",
            new Dictionary<string, (WorkflowArgumentMode, string)>
            {
                ["value"] = (WorkflowArgumentMode.Reference, "${item.value}"),
                ["secret"] = (WorkflowArgumentMode.Secret, "${secret.session-key}")
            });
        ValidateDefinition();
    }

    public WorkflowDefinitionV2 BuildDefinition()
    {
        EnsureCatalog();
        return _editor.BuildDefinition(_catalog!, Summary, Steps);
    }

    public Task<WorkflowRunResult> RunCurrentAsync(CancellationToken cancellationToken = default) =>
        _runSession.RunAsync(BuildDefinition(), null, cancellationToken);

    /// <summary>查询当前 Studio Document 实例能否接受指定工作台命令。</summary>
    /// <param name="commandId">Host 正在查询的稳定命令身份。</param>
    /// <returns>命令属于本 Target 且当前实例状态允许执行时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 状态严格属于当前 Document：验证只要求空闲；运行还要求最近一次验证成功；取消只在运行中成立。
    /// 关闭开始后全部返回 false，避免迟到的菜单或快捷键重新进入已经释放的会话。
    /// </remarks>
    bool IWorkbenchDocumentCommandTarget.CanExecute(CommandId commandId) =>
        CanExecuteWorkbenchCommand(commandId);

    private bool CanExecuteWorkbenchCommand(CommandId commandId)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        if (_disposed || Volatile.Read(ref _closing) != 0)
        {
            return false;
        }

        if (commandId == PluginIds.ValidateWorkflow)
        {
            return !IsRunning;
        }
        if (commandId == PluginIds.RunWorkflow)
        {
            return CanExecute &&
                !IsRunning &&
                Volatile.Read(ref _workbenchRunActive) == 0;
        }
        return commandId == PluginIds.CancelWorkflow && IsRunning;
    }

    /// <summary>在当前 Studio Document 实例上执行指定工作台命令。</summary>
    /// <param name="commandId">Host 已路由到当前活动实例的稳定命令身份。</param>
    /// <param name="cancellationToken">调用取消、Document 关闭或 Host 退出时使用的协作取消令牌。</param>
    /// <returns>表示命令真实完成的可等待操作；运行命令不会通过 async void 提前返回。</returns>
    /// <exception cref="ArgumentOutOfRangeException">命令不属于 Workflow Studio Target。</exception>
    /// <exception cref="InvalidOperationException">当前实例状态不允许执行该命令。</exception>
    ValueTask IWorkbenchDocumentCommandTarget.ExecuteAsync(
        CommandId commandId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        cancellationToken.ThrowIfCancellationRequested();

        if (commandId != PluginIds.ValidateWorkflow &&
            commandId != PluginIds.RunWorkflow &&
            commandId != PluginIds.CancelWorkflow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandId),
                commandId,
                "当前 Workflow Studio Document Target 不拥有该工作台命令。");
        }
        if (!CanExecuteWorkbenchCommand(commandId))
        {
            // Host Executor 会在执行前重新查询状态；Target 仍保留实例内防御，避免插件内部调用、
            // 快速重复按键或错误适配绕过最终检查后破坏 RunSession 的单运行约束。
            throw new InvalidOperationException("当前 Workflow Studio Document 状态不允许执行该命令。");
        }

        if (commandId == PluginIds.ValidateWorkflow)
        {
            ValidateDefinition();
            return ValueTask.CompletedTask;
        }
        if (commandId == PluginIds.CancelWorkflow)
        {
            _runSession.Cancel();
            return ValueTask.CompletedTask;
        }

        return new ValueTask(RunAsync(cancellationToken));
    }

    private void RefreshCatalog()
    {
        _catalog = _editor.CaptureCatalog();
        AvailableActions.Clear();
        foreach (var action in _catalog.Actions)
        {
            AvailableActions.Add(new(action));
        }
        SelectedAction = AvailableActions.FirstOrDefault();
        CanExecute = false;
        RiskSummary = $"契约：{_catalog.ContractRevision}；展示：{_catalog.PresentationRevision}；动作数：{_catalog.Actions.Count}。";
        NotifyCommandState();
    }

    private void AddStep()
    {
        if (SelectedAction is null)
        {
            return;
        }
        var suffix = SelectedAction.Descriptor.Id.Value.Split('.').Last();
        var candidate = suffix;
        var number = 2;
        while (Steps.Any(item => string.Equals(item.Id, candidate, StringComparison.Ordinal)))
        {
            candidate = suffix + "-" + number++;
        }
        var step = _editor.CreateStep(SelectedAction.Descriptor, candidate, null);
        Steps.Add(step);
        SelectedStep = step;
        CanExecute = false;
        NotifyCommandState();
    }

    private void AddStepByActionSuffix(
        string suffix,
        string id,
        string? forEach,
        IReadOnlyDictionary<string, (WorkflowArgumentMode Mode, string Value)> values)
    {
        var action = _catalog!.Actions.Single(item =>
            item.Id.Value.EndsWith("." + suffix, StringComparison.Ordinal));
        var editor = _editor.CreateStep(action, id, forEach);
        foreach (var argument in editor.Arguments)
        {
            if (values.TryGetValue(argument.Name, out var value))
            {
                argument.Mode = value.Mode;
                argument.Value = value.Value;
            }
        }
        Steps.Add(editor);
        SelectedStep = editor;
    }

    private void RemoveStep()
    {
        if (SelectedStep is null)
        {
            return;
        }
        var index = Steps.IndexOf(SelectedStep);
        Steps.Remove(SelectedStep);
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Min(index, Steps.Count - 1)];
        CanExecute = false;
        NotifyCommandState();
    }

    private void MoveStep(int offset)
    {
        if (SelectedStep is null)
        {
            return;
        }
        var oldIndex = Steps.IndexOf(SelectedStep);
        var newIndex = oldIndex + offset;
        if (newIndex >= 0 && newIndex < Steps.Count)
        {
            Steps.Move(oldIndex, newIndex);
            CanExecute = false;
        }
    }

    private void ValidateDefinition()
    {
        ValidationMessages.Clear();
        try
        {
            var validation = _editor.Validate(BuildDefinition(), _catalog!);
            foreach (var issue in validation.Validation.Issues)
            {
                ValidationMessages.Add(new(issue.Severity, issue.Code, issue.Path, issue.Message));
            }
            CanExecute = validation.Validation.IsValid;
            RiskSummary = validation.Risk.Description;
        }
        catch (Exception exception) when (exception is WorkflowDefinitionFormatException or InvalidOperationException)
        {
            AddUiError("editor.format", "$", exception.Message);
            CanExecute = false;
        }
        NotifyCommandState();
    }

    private void ExportDefinition()
    {
        try
        {
            DefinitionJson = _editor.Export(BuildDefinition());
        }
        catch (Exception exception) when (exception is WorkflowDefinitionFormatException or InvalidOperationException)
        {
            ValidationMessages.Clear();
            AddUiError("export.failed", "$", exception.Message);
        }
    }

    private void ImportDefinition()
    {
        try
        {
            EnsureCatalog();
            var snapshot = _editor.Import(DefinitionJson, _catalog!);
            Steps.Clear();
            Summary = snapshot.Summary;
            foreach (var step in snapshot.Steps)
            {
                Steps.Add(step);
            }
            SelectedStep = Steps.FirstOrDefault();
            ValidateDefinition();
        }
        catch (WorkflowDefinitionFormatException exception)
        {
            ValidationMessages.Clear();
            AddUiError("import.failed", "$", exception.Message);
            CanExecute = false;
        }
    }

    private void StoreSecret()
    {
        try
        {
            _runSession.SetSecret(SecretName, SecretValue);
            SecretValue = string.Empty;
            ValidateDefinition();
        }
        catch (ArgumentException exception)
        {
            ValidationMessages.Clear();
            AddUiError("secret.name", "$.secret", exception.Message);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        ValidateDefinition();
        if (!CanExecute)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _workbenchRunActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("当前 Document 已有工作流正在执行。");
        }

        IsRunning = true;
        RunMessages.Clear();
        RunStatus = "正在执行…";
        NotifyCommandState();
        try
        {
            var progress = new Progress<WorkflowRunProgress>(item =>
                RunStatus = $"{item.StepId}：{item.Stage} {item.Percent?.ToString() ?? "-"}%");
            var result = await _runSession.RunAsync(BuildDefinition(), progress, cancellationToken);
            foreach (var entry in result.Entries)
            {
                var item = entry.ItemIndex is null ? string.Empty : $"[{entry.ItemIndex}]";
                RunMessages.Add(new($"{entry.StepId}{item} · {entry.Status} · {entry.InvocationId:D}" +
                    (entry.FailureCode is null ? string.Empty : $" · {entry.FailureCode}: {entry.FailureMessage}")));
            }
            if (result.Failure is not null)
            {
                RunMessages.Add(new($"{result.Failure.StepId} · {result.Failure.Code} · " +
                    $"{result.Failure.Path}：{result.Failure.Message}"));
            }
            RunStatus = result.Message;
        }
        catch (WorkflowValidationException exception)
        {
            ValidationMessages.Clear();
            foreach (var issue in exception.Result.Issues)
            {
                ValidationMessages.Add(new(issue.Severity, issue.Code, issue.Path, issue.Message));
            }
            RunStatus = "执行前目录或定义已失效。";
        }
        finally
        {
            Volatile.Write(ref _workbenchRunActive, 0);
            IsRunning = false;
            NotifyCommandState();
        }
    }

    private void EnsureCatalog()
    {
        if (_catalog is null)
        {
            RefreshCatalog();
        }
    }

    private void CloseSession()
    {
        Interlocked.Exchange(ref _closing, 1);
        _runSession.Close();
        Steps.Clear();
        DefinitionJson = string.Empty;
        CanExecute = false;
        NotifyWorkbenchCommandState(
            PluginIds.ValidateWorkflow,
            PluginIds.RunWorkflow,
            PluginIds.CancelWorkflow);
    }

    private void AddUiError(string code, string path, string message) =>
        ValidationMessages.Add(new(WorkflowValidationSeverity.Error, code, path, message));

    private void NotifyCommandState()
    {
        foreach (var command in new[]
                 {
                     RefreshCatalogCommand, AddStepCommand, RemoveStepCommand, MoveStepUpCommand,
                     MoveStepDownCommand, ValidateCommand, ImportCommand, ExportCommand,
                     StoreSecretCommand, CancelCommand
                 }.OfType<RelayCommand>())
        {
            command.NotifyCanExecuteChanged();
        }
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>逐条发布受影响的工作台命令状态，避免 null 或“全部刷新”哨兵扩散到 Host。</summary>
    private void NotifyWorkbenchCommandState(params CommandId[] commandIds)
    {
        foreach (var commandId in commandIds)
        {
            CommandStateChanged?.Invoke(
                this,
                new WorkbenchCommandStateChangedEventArgs(commandId));
        }
    }

    partial void OnSelectedActionChanged(WorkflowActionChoice? value) => NotifyCommandState();
    partial void OnSelectedStepChanged(WorkflowStepEditor? value) => NotifyCommandState();
    partial void OnIsRunningChanged(bool value)
    {
        NotifyCommandState();
        NotifyWorkbenchCommandState(
            PluginIds.ValidateWorkflow,
            PluginIds.RunWorkflow,
            PluginIds.CancelWorkflow);
    }

    partial void OnCanExecuteChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        NotifyWorkbenchCommandState(PluginIds.RunWorkflow);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _closingRegistration.Dispose();
        CloseSession();
        _runSession.Dispose();
    }
}
