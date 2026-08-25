using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows;

public interface IWorkflowRunner
{
    Task<WorkflowRunResult> RunAsync(
        WorkflowDefinitionV1 definition,
        IProgress<WorkflowRunProgress>? progress,
        CancellationToken cancellationToken);
}
/// <summary>
/// 轻量 Runner 只负责顺序、有限 ForEach、引用解析和失败停止。授权、Action 超时、调用并发、
/// Provider Scope 与诊断脱敏继续由 Host Gateway 负责，Studio 不复制第二套治理内核。
/// </summary>
public sealed class WorkflowRunner(
    IWorkflowActionGateway gateway,
    IWorkflowActionCatalogProjection catalogProjection,
    IWorkflowDefinitionValidator validator,
    IWorkflowReferenceResolver resolver,
    IWorkflowJsonSchemaValidator schemaValidator) : IWorkflowRunner
{
    private readonly WorkflowStudioLimits _limits = WorkflowStudioLimits.Default;

    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinitionV1 definition,
        IProgress<WorkflowRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var catalog = catalogProjection.Capture();
        var validation = validator.Validate(definition, catalog);
        if (!validation.IsValid)
        {
            throw new WorkflowValidationException(validation);
        }

        using var duration = new CancellationTokenSource(_limits.MaximumRunDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, duration.Token);
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var entries = new List<WorkflowRunEntry>();
        try
        {
            await using var run = gateway.CreateRun();
            foreach (var step in definition.Steps)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!catalog.TryGet(step.ActionId, out var descriptor))
                {
                    throw new InvalidOperationException("已验证的 Action 在同一目录快照中丢失。");
                }

                if (step.ForEach is null)
                {
                    var outcome = await InvokeStepAsync(
                        run, step, descriptor!, outputs, item: null, itemIndex: null,
                        progress, linked.Token);
                    entries.Add(outcome.Entry);
                    if (!outcome.Succeeded)
                    {
                        return new WorkflowRunResult(false, outcome.Entry.Status == WorkflowActionInvocationStatus.Cancelled,
                            entries, "步骤失败，后续步骤未执行。");
                    }
                    outputs[step.Id] = outcome.Output!.Value;
                }
                else
                {
                    var source = resolver.ResolveToken(step.ForEach, outputs, item: null);
                    if (source.ValueKind != JsonValueKind.Array ||
                        source.GetArrayLength() > _limits.MaximumForEachItems)
                    {
                        throw new InvalidOperationException("ForEach 运行来源不是允许范围内的数组。");
                    }
                    var aggregated = new List<JsonElement>();
                    var itemIndex = 0;
                    foreach (var item in source.EnumerateArray())
                    {
                        var outcome = await InvokeStepAsync(
                            run, step, descriptor!, outputs, item, itemIndex,
                            progress, linked.Token);
                        entries.Add(outcome.Entry);
                        if (!outcome.Succeeded)
                        {
                            return new WorkflowRunResult(false,
                                outcome.Entry.Status == WorkflowActionInvocationStatus.Cancelled,
                                entries, "ForEach 项失败，剩余项和后续步骤未执行。");
                        }
                        aggregated.Add(outcome.Output!.Value.Clone());
                        itemIndex++;
                    }
                    outputs[step.Id] = JsonSerializer.SerializeToElement(aggregated);
                }
            }
            return new WorkflowRunResult(true, false, entries, "工作流执行成功。");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new WorkflowRunResult(false, true, entries,
                duration.IsCancellationRequested ? "工作流超过总运行预算。" : "工作流已取消。");
        }
        finally
        {
            // JsonElement 快照只在本次运行中用于引用解析；结果对象有意不返回 Action 输出，
            // 避免 Provider 意外回显 Secret 后被长期保存在运行日志或 UI 状态。
            outputs.Clear();
        }
    }

    private async Task<InvocationOutcome> InvokeStepAsync(
        IWorkflowActionRun run,
        WorkflowStepDefinition step,
        WorkflowActionDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement> outputs,
        JsonElement? item,
        int? itemIndex,
        IProgress<WorkflowRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = resolver.ResolveArguments(step.Arguments, outputs, item);
        var schemaIssues = new List<WorkflowValidationIssue>();
        schemaValidator.Validate(arguments, descriptor.InputSchema,
            "$runtime." + step.Id, allowReferenceTokens: false, schemaIssues);
        if (schemaIssues.Count != 0)
        {
            throw new WorkflowValidationException(new WorkflowValidationResult(schemaIssues));
        }

        var actionProgress = progress is null
            ? null
            : new Progress<WorkflowActionProgress>(item => progress.Report(
                new WorkflowRunProgress(step.Id, itemIndex, item.Stage, item.Percent, item.Message)));
        var result = await run.InvokeAsync(
            new WorkflowActionInvocationRequest(step.ActionId, arguments),
            actionProgress,
            cancellationToken);
        var entry = new WorkflowRunEntry(
            step.Id,
            itemIndex,
            result.InvocationId,
            result.Status,
            result.Failure?.Code,
            result.Failure?.Message);
        return new InvocationOutcome(
            result.Status == WorkflowActionInvocationStatus.Succeeded,
            result.Output,
            entry);
    }

    private sealed record InvocationOutcome(
        bool Succeeded,
        JsonElement? Output,
        WorkflowRunEntry Entry);
}
