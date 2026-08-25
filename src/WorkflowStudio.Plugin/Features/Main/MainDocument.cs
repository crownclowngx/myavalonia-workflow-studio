using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Workflows;

namespace WorkflowStudio.Features.Main;

/// <summary>
/// Workflow Studio 的非持久化 Document 模型。它负责 UI 用例编排和可观察状态，不实现 Codec、
/// 验证或运行算法；这些职责由构造注入的小服务承担，便于分别测试和替换 Standalone Fake 边界。
/// </summary>
public sealed partial class MainDocument : ObservableObject, IPluginDocument, IDisposable
{
    private readonly IWorkflowActionCatalogProjection _catalogProjection;
    private readonly IWorkflowDefinitionCodec _codec;
    private readonly IWorkflowDefinitionValidator _validator;
    private readonly IWorkflowRiskSummaryBuilder _riskBuilder;
    private readonly IWorkflowRunner _runner;
    private readonly ISessionSecretStore _secrets;
    private readonly IDocumentLifetime _lifetime;
    private WorkflowActionCatalogSnapshot? _catalog;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenRegistration _closingRegistration;
    private DocumentPresentationState _presentation = new("Workflow Studio");
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
        IWorkflowActionCatalogProjection catalogProjection,
        IWorkflowDefinitionCodec codec,
        IWorkflowDefinitionValidator validator,
        IWorkflowRiskSummaryBuilder riskBuilder,
        IWorkflowRunner runner,
        ISessionSecretStore secrets,
        IDocumentLifetime lifetime)
    {
        _catalogProjection = catalogProjection;
        _codec = codec;
        _validator = validator;
        _riskBuilder = riskBuilder;
        _runner = runner;
        _secrets = secrets;
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
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
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

    public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(activation.Title))
        {
            _presentation = new DocumentPresentationState(activation.Title);
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
        _closingRegistration = _lifetime.ClosingToken.Register(CloseSession);
        RefreshCatalog();
        return ValueTask.CompletedTask;
    }

    /// <summary>Standalone 自检和单元测试使用的窄入口；正常 UI 仍通过命令编辑。</summary>
    public void LoadDemonstrationWorkflow(string secretValue)
    {
        if (_catalog is null)
        {
            RefreshCatalog();
        }
        _secrets.Set("session-key", secretValue);
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

    public WorkflowDefinitionV1 BuildDefinition()
    {
        if (_catalog is null)
        {
            throw new InvalidOperationException("尚未取得 Action 目录。");
        }
        return new WorkflowDefinitionV1(1, _catalog.Revision, Summary, Steps.Select(item => item.Build()).ToArray());
    }

    public async Task<WorkflowRunResult> RunCurrentAsync(CancellationToken cancellationToken = default) =>
        await _runner.RunAsync(BuildDefinition(), progress: null, cancellationToken);

    private void RefreshCatalog()
    {
        _catalog = _catalogProjection.Capture();
        AvailableActions.Clear();
        foreach (var action in _catalog.Actions)
        {
            AvailableActions.Add(new WorkflowActionChoice(action));
        }
        SelectedAction = AvailableActions.FirstOrDefault();
        CanExecute = false;
        RiskSummary = $"目录 revision：{_catalog.Revision}；动作数：{_catalog.Actions.Count}。";
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
        var editor = CreateStep(SelectedAction.Descriptor, candidate, null);
        Steps.Add(editor);
        SelectedStep = editor;
        CanExecute = false;
        NotifyCommandState();
    }

    private void AddStepByActionSuffix(
        string suffix,
        string id,
        string? forEach,
        IReadOnlyDictionary<string, (WorkflowArgumentMode Mode, string Value)> values)
    {
        var action = _catalog!.Actions.Single(item => item.Id.Value.EndsWith("." + suffix, StringComparison.Ordinal));
        var editor = CreateStep(action, id, forEach);
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

    private static WorkflowStepEditor CreateStep(
        WorkflowActionDescriptor action,
        string id,
        string? forEach)
    {
        var editor = new WorkflowStepEditor { Action = action, Id = id, ForEach = forEach };
        if (action.InputSchema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                var pointer = "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal)
                    .Replace("/", "~1", StringComparison.Ordinal);
                var sensitive = action.SensitiveInputPointers.Contains(pointer, StringComparer.Ordinal);
                var type = WorkflowJsonSchemaValidator.SchemaType(property.Value) ?? "json";
                editor.Arguments.Add(new WorkflowArgumentEditor
                {
                    Name = property.Name,
                    SchemaType = type,
                    IsSensitive = sensitive,
                    Mode = sensitive ? WorkflowArgumentMode.Secret : WorkflowArgumentMode.Constant,
                    Value = sensitive ? "${secret.session-key}" : DefaultConstant(type),
                });
            }
        }
        return editor;
    }

    private static string DefaultConstant(string type) => type switch
    {
        "string" => "\"\"",
        "integer" or "number" => "0",
        "boolean" => "false",
        "array" => "[]",
        "object" => "{}",
        _ => "null",
    };

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
            var definition = BuildDefinition();
            var result = _validator.Validate(definition, _catalog!);
            foreach (var issue in result.Issues)
            {
                ValidationMessages.Add(new WorkflowValidationMessage(issue.Code, issue.Path, issue.Message));
            }
            CanExecute = result.IsValid;
            RiskSummary = _riskBuilder.Build(definition, _catalog!).Description;
        }
        catch (Exception exception) when (exception is WorkflowDefinitionFormatException or InvalidOperationException)
        {
            ValidationMessages.Add(new WorkflowValidationMessage("editor.format", "$", exception.Message));
            CanExecute = false;
        }
        NotifyCommandState();
    }

    private void ExportDefinition()
    {
        try
        {
            DefinitionJson = _codec.Serialize(BuildDefinition());
        }
        catch (Exception exception) when (exception is WorkflowDefinitionFormatException or InvalidOperationException)
        {
            ValidationMessages.Clear();
            ValidationMessages.Add(new WorkflowValidationMessage("export.failed", "$", exception.Message));
        }
    }

    private void ImportDefinition()
    {
        try
        {
            var definition = _codec.Parse(DefinitionJson);
            if (_catalog is null)
            {
                RefreshCatalog();
            }
            // 先在局部集合中完整解析；任一步失败都保留用户当前编辑状态，避免一次恶意或过期导入
            // 把已经通过验证的临时工作流清空。
            var importedSteps = new List<WorkflowStepEditor>();
            foreach (var step in definition.Steps)
            {
                if (!_catalog!.TryGet(step.ActionId, out var action))
                {
                    throw new WorkflowDefinitionFormatException($"导入定义包含未知 Action：{step.ActionId.Value}。");
                }
                var editor = CreateStep(action!, step.Id, step.ForEach);
                foreach (var argument in editor.Arguments)
                {
                    if (!step.Arguments.TryGetProperty(argument.Name, out var value))
                    {
                        continue;
                    }
                    var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                    if (WorkflowReferenceToken.TryParse(text, out var token))
                    {
                        argument.Mode = token!.Kind == WorkflowReferenceKind.Secret
                            ? WorkflowArgumentMode.Secret
                            : WorkflowArgumentMode.Reference;
                        argument.Value = text;
                    }
                    else
                    {
                        argument.Mode = WorkflowArgumentMode.Constant;
                        argument.Value = value.GetRawText();
                    }
                }
                importedSteps.Add(editor);
            }
            Steps.Clear();
            Summary = definition.Summary;
            foreach (var editor in importedSteps)
            {
                Steps.Add(editor);
            }
            SelectedStep = Steps.FirstOrDefault();
            ValidateDefinition();
        }
        catch (WorkflowDefinitionFormatException exception)
        {
            ValidationMessages.Clear();
            ValidationMessages.Add(new WorkflowValidationMessage("import.failed", "$", exception.Message));
            CanExecute = false;
        }
    }

    private void StoreSecret()
    {
        try
        {
            _secrets.Set(SecretName, SecretValue);
            SecretValue = string.Empty;
            ValidateDefinition();
        }
        catch (ArgumentException exception)
        {
            ValidationMessages.Clear();
            ValidationMessages.Add(new WorkflowValidationMessage("secret.name", "$.secret", exception.Message));
        }
    }

    private async Task RunAsync()
    {
        ValidateDefinition();
        if (!CanExecute)
        {
            return;
        }
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        IsRunning = true;
        RunMessages.Clear();
        RunStatus = "正在执行…";
        NotifyCommandState();
        try
        {
            var progress = new Progress<WorkflowRunProgress>(item =>
                RunStatus = $"{item.StepId}：{item.Stage} {item.Percent?.ToString() ?? "-"}%");
            var result = await _runner.RunAsync(BuildDefinition(), progress, _runCancellation.Token);
            foreach (var entry in result.Entries)
            {
                var item = entry.ItemIndex is null ? string.Empty : $"[{entry.ItemIndex}]";
                RunMessages.Add(new WorkflowRunMessage(
                    $"{entry.StepId}{item} · {entry.Status} · {entry.InvocationId:D}" +
                    (entry.FailureCode is null ? string.Empty : $" · {entry.FailureCode}: {entry.FailureMessage}")));
            }
            RunStatus = result.Message;
        }
        catch (WorkflowValidationException exception)
        {
            ValidationMessages.Clear();
            foreach (var issue in exception.Result.Issues)
            {
                ValidationMessages.Add(new WorkflowValidationMessage(issue.Code, issue.Path, issue.Message));
            }
            RunStatus = "执行前目录或定义已失效。";
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            IsRunning = false;
            NotifyCommandState();
        }
    }

    private void Cancel() => _runCancellation?.Cancel();

    private void CloseSession()
    {
        _runCancellation?.Cancel();
        _secrets.Clear();
        Steps.Clear();
        DefinitionJson = string.Empty;
        CanExecute = false;
    }

    private void NotifyCommandState()
    {
        (RefreshCatalogCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (AddStepCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RemoveStepCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (MoveStepUpCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (MoveStepDownCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ValidateCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ImportCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ExportCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StoreSecretCommand as RelayCommand)?.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
        (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedActionChanged(WorkflowActionChoice? value) => NotifyCommandState();
    partial void OnSelectedStepChanged(WorkflowStepEditor? value) => NotifyCommandState();
    partial void OnIsRunningChanged(bool value) => NotifyCommandState();
    partial void OnCanExecuteChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _closingRegistration.Dispose();
        CloseSession();
        _runCancellation?.Dispose();
        if (_secrets is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
