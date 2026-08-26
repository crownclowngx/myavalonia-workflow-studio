using System.Collections.ObjectModel;
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows;

/// <summary>集中保存 Studio 私有定义与执行预算，避免 UI、验证器和 Runner 各自维护一套数字。</summary>
public sealed record WorkflowStudioLimits(
    int MaximumDefinitionBytes,
    int MaximumSteps,
    int MaximumForEachItems,
    int MaximumInvocations,
    int MaximumStringBytes,
    TimeSpan MaximumRunDuration)
{
    public static WorkflowStudioLimits Default { get; } = new(
        256 * 1024,
        32,
        100,
        256,
        64 * 1024,
        TimeSpan.FromHours(6));
}

/// <summary>
/// Studio 私有的版本 2 工作流定义。它不是 Plugin SDK，也不进入 Host 的 Document 持久化协议。
/// 构造时复制步骤集合，调用方随后修改原集合不会改变待验证的定义快照。
/// </summary>
/// <remarks>
/// v2 把执行契约修订与展示修订分开保存。契约漂移会阻止执行；仅文案漂移只产生 Warning。
/// Codec 有意硬切版本，不在执行边界内加入 v1 迁移或猜测逻辑。
/// </remarks>
public sealed class WorkflowDefinitionV2
{
    public WorkflowDefinitionV2(
        int schemaVersion,
        string contractRevision,
        string presentationRevision,
        string summary,
        IReadOnlyList<WorkflowStepDefinition> steps)
    {
        SchemaVersion = schemaVersion;
        ContractRevision = contractRevision ?? throw new ArgumentNullException(nameof(contractRevision));
        PresentationRevision = presentationRevision ?? throw new ArgumentNullException(nameof(presentationRevision));
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        ArgumentNullException.ThrowIfNull(steps);
        Steps = new ReadOnlyCollection<WorkflowStepDefinition>(steps.ToArray());
    }

    public int SchemaVersion { get; }
    public string ContractRevision { get; }
    public string PresentationRevision { get; }
    public string Summary { get; }
    public IReadOnlyList<WorkflowStepDefinition> Steps { get; }
}

/// <summary>描述一个顺序步骤；Arguments 是独立 JSON 快照，ForEach 为空表示普通步骤。</summary>
public sealed class WorkflowStepDefinition
{
    public WorkflowStepDefinition(
        string id,
        WorkflowActionId actionId,
        JsonElement arguments,
        string? forEach = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        Arguments = arguments.Clone();
        ForEach = forEach;
    }

    public string Id { get; }
    public WorkflowActionId ActionId { get; }
    public JsonElement Arguments { get; }
    public string? ForEach { get; }
}

/// <summary>区分不阻止执行的目录展示提示与必须修复的确定性错误。</summary>
public enum WorkflowValidationSeverity
{
    /// <summary>定义仍可安全执行，但用户应刷新展示信息。</summary>
    Warning,
    /// <summary>执行安全无法证明，必须阻止运行。</summary>
    Error,
}

/// <summary>描述一次运行前验证发现的稳定、可定位且不包含 Secret 的问题。</summary>
public sealed record WorkflowValidationIssue(
    WorkflowValidationSeverity Severity,
    string Code,
    string Path,
    string Message);

/// <summary>保存验证问题快照；只有 Error 会令 <see cref="IsValid"/> 为 false。</summary>
public sealed class WorkflowValidationResult(IReadOnlyList<WorkflowValidationIssue> issues)
{
    public IReadOnlyList<WorkflowValidationIssue> Issues { get; } =
        new ReadOnlyCollection<WorkflowValidationIssue>(issues.ToArray());
    public bool IsValid => Issues.All(issue => issue.Severity != WorkflowValidationSeverity.Error);
}

public sealed record WorkflowRiskSummary(
    WorkflowActionRiskFlags Risks,
    WorkflowActionConfirmationPolicy HighestConfirmation,
    int StepCount,
    int MaximumInvocationCount,
    string Description);

public sealed record WorkflowRunEntry(
    string StepId,
    int? ItemIndex,
    Guid InvocationId,
    WorkflowActionInvocationStatus Status,
    string? FailureCode,
    string? FailureMessage);

/// <summary>描述未进入 Action invocation 的工作流级运行失败。</summary>
/// <remarks>
/// StepId、ItemIndex 与 Path 允许 UI 精确定位失败点；Message 只保存脱敏说明，引用正文和
/// Secret 永远不进入结果。该类型与 Provider 返回的 WorkflowActionFailure 各自拥有边界。
/// </remarks>
public sealed record WorkflowRunFailure(
    string Code,
    string StepId,
    int? ItemIndex,
    string Path,
    string Message);

/// <summary>汇总已经发生的 Action 调用与可选的工作流级结构化失败。</summary>
public sealed class WorkflowRunResult(
    bool succeeded,
    bool cancelled,
    IReadOnlyList<WorkflowRunEntry> entries,
    string message,
    WorkflowRunFailure? failure = null)
{
    public bool Succeeded { get; } = succeeded;
    public bool Cancelled { get; } = cancelled;
    public IReadOnlyList<WorkflowRunEntry> Entries { get; } =
        new ReadOnlyCollection<WorkflowRunEntry>(entries.ToArray());
    public string Message { get; } = message;
    public WorkflowRunFailure? Failure { get; } = failure;
}

public sealed record WorkflowRunProgress(
    string StepId,
    int? ItemIndex,
    string Stage,
    int? Percent,
    string? Message);

public sealed class WorkflowValidationException(WorkflowValidationResult result)
    : InvalidOperationException("工作流未通过确定性验证，不能执行。")
{
    public WorkflowValidationResult Result { get; } = result;
}

public sealed class WorkflowDefinitionFormatException(string message, Exception? inner = null)
    : FormatException(message, inner);
