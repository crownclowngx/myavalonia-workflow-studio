using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows;

public interface IWorkflowDefinitionValidator
{
    WorkflowValidationResult Validate(
        WorkflowDefinitionV1 definition,
        WorkflowActionCatalogSnapshot catalog);
}

/// <summary>
/// 在执行之前把定义、目录、引用、Secret、Schema 和预算放在同一个确定性观察区间内检查。
/// 验证器不创建 Run、不修改定义，也不接触 UI，因此同一输入始终得到同一组问题。
/// </summary>
public sealed partial class WorkflowDefinitionValidator(
    IWorkflowJsonSchemaValidator schemaValidator,
    ISessionSecretStore secrets) : IWorkflowDefinitionValidator
{
    private readonly WorkflowStudioLimits _limits = WorkflowStudioLimits.Default;

    public WorkflowValidationResult Validate(
        WorkflowDefinitionV1 definition,
        WorkflowActionCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalog);
        var issues = new List<WorkflowValidationIssue>();
        if (definition.SchemaVersion != 1)
        {
            issues.Add(new("definition.schemaVersion", "$.schemaVersion", "只支持 schemaVersion 1。"));
        }
        if (!string.Equals(definition.CatalogRevision, catalog.Revision, StringComparison.Ordinal))
        {
            issues.Add(new("catalog.stale", "$.catalogRevision", "Action 目录已变化，请刷新并重新验证。"));
        }
        if (definition.Steps.Count == 0)
        {
            issues.Add(new("definition.empty", "$.steps", "至少需要一个步骤。"));
        }
        if (definition.Steps.Count > _limits.MaximumSteps)
        {
            issues.Add(new("budget.steps", "$.steps", $"步骤数不能超过 {_limits.MaximumSteps}。"));
        }
        if (Encoding.UTF8.GetByteCount(definition.Summary) > _limits.MaximumStringBytes)
        {
            issues.Add(new("budget.string", "$.summary", "摘要超过 64 KiB。"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var prior = new Dictionary<string, WorkflowActionDescriptor>(StringComparer.Ordinal);
        var maximumInvocations = 0;
        for (var index = 0; index < definition.Steps.Count; index++)
        {
            var step = definition.Steps[index];
            var path = $"$.steps[{index}]";
            if (!StepIdPattern().IsMatch(step.Id))
            {
                issues.Add(new("step.id", path + ".id", "步骤 ID 必须是小写字母开头的 kebab-case，最长 64 字符。"));
            }
            if (!seen.Add(step.Id))
            {
                issues.Add(new("step.duplicate", path + ".id", "步骤 ID 必须唯一。"));
            }
            if (!catalog.TryGet(step.ActionId, out var descriptor))
            {
                issues.Add(new("action.unknown", path + ".actionId", "Action 不存在或所有者当前不可用。"));
                continue;
            }

            JsonElement? itemSchema = null;
            if (step.ForEach is not null)
            {
                maximumInvocations += _limits.MaximumForEachItems;
                if (!WorkflowReferenceToken.TryParse(step.ForEach, out var eachToken) ||
                    eachToken!.Kind != WorkflowReferenceKind.Step)
                {
                    issues.Add(new("foreach.reference", path + ".forEach", "ForEach 必须引用前序步骤的数组输出。"));
                }
                else if (!TryResolvePriorSchema(eachToken, prior, out var arraySchema, out var failure))
                {
                    issues.Add(new("foreach.reference", path + ".forEach", failure));
                }
                else if (WorkflowJsonSchemaValidator.SchemaType(arraySchema) != "array" ||
                         !arraySchema.TryGetProperty("items", out var resolvedItemSchema))
                {
                    issues.Add(new("foreach.array", path + ".forEach", "ForEach 来源必须是带 items Schema 的数组。"));
                }
                else
                {
                    itemSchema = resolvedItemSchema;
                    if (arraySchema.TryGetProperty("maxItems", out var maxItems) &&
                        maxItems.GetInt32() > _limits.MaximumForEachItems)
                    {
                        issues.Add(new("budget.foreach", path + ".forEach",
                            $"ForEach 来源 Schema 上限不能超过 {_limits.MaximumForEachItems}。"));
                    }
                }
            }
            else
            {
                maximumInvocations++;
            }

            schemaValidator.Validate(
                step.Arguments,
                descriptor!.InputSchema,
                path + ".arguments",
                allowReferenceTokens: true,
                issues);
            ValidateReferences(step.Arguments, descriptor, prior, itemSchema, path + ".arguments", issues);
            ValidateSensitivePointers(step.Arguments, descriptor, path + ".arguments", issues);
            prior[step.Id] = descriptor;
        }

        if (maximumInvocations > _limits.MaximumInvocations)
        {
            issues.Add(new("budget.invocations", "$.steps",
                $"按 ForEach 上限估算的调用数超过 {_limits.MaximumInvocations}。"));
        }
        return new WorkflowValidationResult(issues);
    }

    private void ValidateReferences(
        JsonElement element,
        WorkflowActionDescriptor currentAction,
        IReadOnlyDictionary<string, WorkflowActionDescriptor> prior,
        JsonElement? itemSchema,
        string path,
        IList<WorkflowValidationIssue> issues)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            if (!WorkflowReferenceToken.TryParse(text, out var token))
            {
                if (text.Contains("${", StringComparison.Ordinal))
                {
                    issues.Add(new("reference.syntax", path, "引用必须占据整个字段值并使用受支持语法。"));
                }
                return;
            }
            switch (token!.Kind)
            {
                case WorkflowReferenceKind.Secret:
                    if (!secrets.Contains(token.Root))
                    {
                        issues.Add(new("secret.missing", path, $"当前会话缺少 Secret：{token.Root}。"));
                    }
                    break;
                case WorkflowReferenceKind.Item:
                    if (itemSchema is null ||
                        !WorkflowJsonSchemaValidator.TryResolveSchemaPath(itemSchema.Value, token.Path, out _))
                    {
                        issues.Add(new("reference.item", path, "item 引用不属于当前 ForEach 或路径不存在。"));
                    }
                    break;
                case WorkflowReferenceKind.Step:
                    if (!TryResolvePriorSchema(token, prior, out _, out var failure))
                    {
                        issues.Add(new("reference.step", path, failure));
                    }
                    break;
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateReferences(property.Value, currentAction, prior, itemSchema,
                    path + "." + property.Name, issues);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ValidateReferences(item, currentAction, prior, itemSchema, $"{path}[{index++}]", issues);
            }
        }
    }

    private static void ValidateSensitivePointers(
        JsonElement arguments,
        WorkflowActionDescriptor descriptor,
        string path,
        IList<WorkflowValidationIssue> issues)
    {
        foreach (var pointer in descriptor.SensitiveInputPointers)
        {
            if (!TryResolveJsonPointer(arguments, pointer, out var value) ||
                value.ValueKind != JsonValueKind.String ||
                !WorkflowReferenceToken.TryParse(value.GetString(), out var token) ||
                token!.Kind != WorkflowReferenceKind.Secret)
            {
                issues.Add(new("secret.required", path + pointer.Replace('/', '.'),
                    "敏感输入必须使用会话 Secret 引用，不能写入明文常量。"));
            }
        }
    }

    private static bool TryResolvePriorSchema(
        WorkflowReferenceToken token,
        IReadOnlyDictionary<string, WorkflowActionDescriptor> prior,
        out JsonElement schema,
        out string failure)
    {
        schema = default;
        if (!prior.TryGetValue(token.Root, out var owner))
        {
            failure = "只能引用已经排在当前步骤之前的输出。";
            return false;
        }
        if (!WorkflowJsonSchemaValidator.TryResolveSchemaPath(owner.OutputSchema, token.Path, out schema))
        {
            failure = "引用路径不在前序 Action 的输出 Schema 中。";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static bool TryResolveJsonPointer(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        if (!pointer.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }
        return true;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();
}

public interface IWorkflowRiskSummaryBuilder
{
    WorkflowRiskSummary Build(
        WorkflowDefinitionV1 definition,
        WorkflowActionCatalogSnapshot catalog);
}

public sealed class WorkflowRiskSummaryBuilder : IWorkflowRiskSummaryBuilder
{
    public WorkflowRiskSummary Build(
        WorkflowDefinitionV1 definition,
        WorkflowActionCatalogSnapshot catalog)
    {
        var risks = WorkflowActionRiskFlags.None;
        var confirmation = WorkflowActionConfirmationPolicy.Never;
        var invocations = 0;
        foreach (var step in definition.Steps)
        {
            if (catalog.TryGet(step.ActionId, out var descriptor))
            {
                risks |= descriptor!.Risks;
                confirmation = (WorkflowActionConfirmationPolicy)Math.Max(
                    (int)confirmation, (int)descriptor.ConfirmationPolicy);
            }
            invocations += step.ForEach is null ? 1 : WorkflowStudioLimits.Default.MaximumForEachItems;
        }
        var description = risks == WorkflowActionRiskFlags.None
            ? "工作流只包含无副作用动作。"
            : $"风险：{risks}；最高确认策略：{confirmation}。";
        return new WorkflowRiskSummary(risks, confirmation, definition.Steps.Count, invocations, description);
    }
}
