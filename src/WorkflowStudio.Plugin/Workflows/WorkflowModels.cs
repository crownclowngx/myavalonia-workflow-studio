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
/// Studio 私有的版本 1 工作流定义。它不是 Plugin SDK，也不进入 Host 的 Document 持久化协议。
/// 构造时复制步骤集合，调用方随后修改原集合不会改变待验证的定义快照。
/// </summary>
public sealed class WorkflowDefinitionV1
{
    public WorkflowDefinitionV1(
        int schemaVersion,
        string catalogRevision,
        string summary,
        IReadOnlyList<WorkflowStepDefinition> steps)
    {
        SchemaVersion = schemaVersion;
        CatalogRevision = catalogRevision ?? throw new ArgumentNullException(nameof(catalogRevision));
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        ArgumentNullException.ThrowIfNull(steps);
        Steps = new ReadOnlyCollection<WorkflowStepDefinition>(steps.ToArray());
    }

    public int SchemaVersion { get; }
    public string CatalogRevision { get; }
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

public sealed record WorkflowValidationIssue(string Code, string Path, string Message);

public sealed class WorkflowValidationResult(IReadOnlyList<WorkflowValidationIssue> issues)
{
    public IReadOnlyList<WorkflowValidationIssue> Issues { get; } =
        new ReadOnlyCollection<WorkflowValidationIssue>(issues.ToArray());
    public bool IsValid => Issues.Count == 0;
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

public sealed class WorkflowRunResult(
    bool succeeded,
    bool cancelled,
    IReadOnlyList<WorkflowRunEntry> entries,
    string message)
{
    public bool Succeeded { get; } = succeeded;
    public bool Cancelled { get; } = cancelled;
    public IReadOnlyList<WorkflowRunEntry> Entries { get; } =
        new ReadOnlyCollection<WorkflowRunEntry>(entries.ToArray());
    public string Message { get; } = message;
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
