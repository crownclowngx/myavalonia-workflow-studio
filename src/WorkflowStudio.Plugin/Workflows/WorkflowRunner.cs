using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows;

/// <summary>执行一个已验证的 Workflow v2 快照并返回结构化结果。</summary>
public interface IWorkflowRunner
{
    Task<WorkflowRunResult> RunAsync(
        WorkflowDefinitionV2 definition,
        IProgress<WorkflowRunProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>执行顺序步骤和有限 ForEach，并把可预期的引用失败收口为运行结果。</summary>
/// <remarks>
/// 授权、超时、调用并发、Provider Scope 与诊断脱敏继续由 Host Gateway 负责。Runner 只处理
/// Studio 自己拥有的顺序、引用、取消和失败停止，不捕获用于暴露程序错误的任意异常。
/// </remarks>
public sealed class WorkflowRunner(
    IWorkflowActionGateway gateway,
    IWorkflowActionCatalogProjection catalogProjection,
    IWorkflowDefinitionValidator validator,
    IWorkflowReferenceResolver resolver,
    IWorkflowJsonSchemaValidator schemaValidator,
    IWorkflowInvocationObserver? observer = null) : IWorkflowRunner
{
    private readonly WorkflowStudioLimits _limits = WorkflowStudioLimits.Default;

    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinitionV2 definition,
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, duration.Token);
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var entries = new List<WorkflowRunEntry>();
        string currentStepId = string.Empty;
        int? currentItemIndex = null;
        try
        {
            observer?.Begin(definition);
            await using var run = gateway.CreateRun();
            for (var stepIndex = 0; stepIndex < definition.Steps.Count; stepIndex++)
            {
                var step = definition.Steps[stepIndex];
                currentStepId = step.Id;
                currentItemIndex = null;
                linked.Token.ThrowIfCancellationRequested();
                if (!catalog.TryGet(step.ActionId, out var descriptor))
                {
                    return Failed(entries, new("catalog.action-missing", step.Id, null,
                        $"$.steps[{stepIndex}].actionId", "已验证的 Action 在目录快照中丢失。"));
                }

                if (step.ForEach is null)
                {
                    var outcome = await InvokeStepAsync(run, step, descriptor!, outputs, null, null,
                        $"$.steps[{stepIndex}].arguments", progress, linked.Token);
                    entries.Add(outcome.Entry);
                    if (!outcome.Succeeded)
                    {
                        return new(false,
                            outcome.Entry.Status == WorkflowActionInvocationStatus.Cancelled,
                            entries, "步骤失败，后续步骤未执行。");
                    }
                    outputs[step.Id] = outcome.Output!.Value;
                    continue;
                }

                var source = resolver.ResolveToken(step.ForEach, outputs, null,
                    $"$.steps[{stepIndex}].forEach");
                if (source.ValueKind != JsonValueKind.Array ||
                    source.GetArrayLength() > _limits.MaximumForEachItems)
                {
                    return Failed(entries, new("foreach.source-invalid", step.Id, null,
                        $"$.steps[{stepIndex}].forEach", "ForEach 运行来源不是允许范围内的数组。"));
                }
                var aggregated = new List<JsonElement>();
                var itemIndex = 0;
                foreach (var item in source.EnumerateArray())
                {
                    currentItemIndex = itemIndex;
                    var outcome = await InvokeStepAsync(run, step, descriptor!, outputs, item, itemIndex,
                        $"$.steps[{stepIndex}].arguments", progress, linked.Token);
                    entries.Add(outcome.Entry);
                    if (!outcome.Succeeded)
                    {
                        return new(false,
                            outcome.Entry.Status == WorkflowActionInvocationStatus.Cancelled,
                            entries, "ForEach 项失败，剩余项和后续步骤未执行。");
                    }
                    aggregated.Add(outcome.Output!.Value.Clone());
                    itemIndex++;
                }
                outputs[step.Id] = JsonSerializer.SerializeToElement(aggregated);
            }
            // 已完成的 SDK 调用仍可能含业务失败。保留成功引用和清理步骤的执行机会，最终摘要必须如实收口。
            var businessFailures = entries.Count(entry => entry.Status == WorkflowActionInvocationStatus.Succeeded && entry.FailureCode is not null);
            return businessFailures == 0 ? new(true, false, entries, "工作流执行成功。") :
                new(false, false, entries, $"工作流执行完成，其中 {businessFailures} 次归档调用包含业务失败；已保留成功产物。");
        }
        catch (WorkflowReferenceResolutionException exception)
        {
            return Failed(entries, new(exception.Code, currentStepId, currentItemIndex,
                exception.Path, exception.Message));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new(false, true, entries,
                duration.IsCancellationRequested ? "工作流超过总运行预算。" : "工作流已取消。");
        }
        finally
        {
            // 输出快照只服务本次引用解析；结果对象不返回正文，避免 Provider 意外回显 Secret 后被长期保存。
            outputs.Clear();
            observer?.End();
        }
    }

    private async Task<InvocationOutcome> InvokeStepAsync(
        IWorkflowActionRun run,
        WorkflowStepDefinition step,
        WorkflowActionDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement> outputs,
        JsonElement? item,
        int? itemIndex,
        string argumentPath,
        IProgress<WorkflowRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = resolver.ResolveArguments(step.Arguments, outputs, item, argumentPath);
        var schemaIssues = new List<WorkflowValidationIssue>();
        schemaValidator.Validate(arguments, descriptor.InputSchema,
            "$runtime." + step.Id, false, schemaIssues);
        if (schemaIssues.Any(issue => issue.Severity == WorkflowValidationSeverity.Error))
        {
            throw new WorkflowValidationException(new WorkflowValidationResult(schemaIssues));
        }

        var actionProgress = progress is null
            ? null
            : new Progress<WorkflowActionProgress>(item => progress.Report(
                new(step.Id, itemIndex, item.Stage, item.Percent, item.Message)));
        observer?.Started(step.Id, itemIndex);
        var result = await run.InvokeAsync(
            new WorkflowActionInvocationRequest(step.ActionId, arguments),
            actionProgress,
            cancellationToken);
        var failure = result.Failure ?? (result.Status == WorkflowActionInvocationStatus.Succeeded
            ? ArchiveWorkflowOutcome.Inspect(step.ActionId, result.Output) : null);
        var entry = new WorkflowRunEntry(step.Id, itemIndex, result.InvocationId, result.Status,
            failure?.Code, failure?.Message);
        observer?.Observe(step.Id, itemIndex, descriptor, result.Status, result.Output);
        return new(result.Status == WorkflowActionInvocationStatus.Succeeded && failure?.Code != "archive.result-invalid", result.Output, entry);
    }

    private static WorkflowRunResult Failed(
        IReadOnlyList<WorkflowRunEntry> entries,
        WorkflowRunFailure failure) =>
        new(false, false, entries, failure.Message, failure);

    private sealed record InvocationOutcome(
        bool Succeeded,
        JsonElement? Output,
        WorkflowRunEntry Entry);
}
