using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;

namespace WorkflowStudio.Workflows;

/// <summary>定义运行前协议、安全预算与静态引用验证入口。</summary>
public interface IWorkflowDefinitionValidator
{
    WorkflowValidationResult Validate(
        WorkflowDefinitionV2 definition,
        WorkflowActionCatalogSnapshot catalog);
}

/// <summary>在执行前统一验证 v2 定义、目录、引用值域、Secret 和预算。</summary>
/// <remarks>
/// 验证器同时遍历参数值与目标输入 Schema，因此每个引用都能取得明确的来源和目标类型。
/// 前序输出保存“真实运行形状”：普通步骤是 Action 输出，ForEach 步骤则是 Action 输出数组。
/// </remarks>
public sealed partial class WorkflowDefinitionValidator(
    IWorkflowJsonSchemaValidator schemaValidator,
    ISessionSecretStore secrets,
    WorkflowReferenceTypeSystem referenceTypes,
    WorkflowSchemaValidator sharedSchema) : IWorkflowDefinitionValidator
{
    private readonly WorkflowStudioLimits _limits = WorkflowStudioLimits.Default;

    public WorkflowValidationResult Validate(
        WorkflowDefinitionV2 definition,
        WorkflowActionCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalog);
        var issues = new List<WorkflowValidationIssue>();
        if (definition.SchemaVersion != 2)
        {
            AddError(issues, "definition.schemaVersion", "$.schemaVersion", "只支持 schemaVersion 2。");
        }
        if (!string.Equals(definition.ContractRevision, catalog.ContractRevision, StringComparison.Ordinal))
        {
            AddError(issues, "catalog.contract-stale", "$.contractRevision",
                "Action 执行契约已变化，请刷新并重新验证。");
        }
        if (!string.Equals(definition.PresentationRevision, catalog.PresentationRevision, StringComparison.Ordinal))
        {
            issues.Add(new(WorkflowValidationSeverity.Warning, "catalog.presentation-stale",
                "$.presentationRevision", "Action 名称或说明已变化；当前定义仍可执行。"));
        }
        if (definition.Steps.Count == 0)
        {
            AddError(issues, "definition.empty", "$.steps", "至少需要一个步骤。");
        }
        if (definition.Steps.Count > _limits.MaximumSteps)
        {
            AddError(issues, "budget.steps", "$.steps", $"步骤数不能超过 {_limits.MaximumSteps}。");
        }
        if (Encoding.UTF8.GetByteCount(definition.Summary) > _limits.MaximumStringBytes)
        {
            AddError(issues, "budget.string", "$.summary", "摘要超过 64 KiB。");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var prior = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var maximumInvocations = 0;
        for (var index = 0; index < definition.Steps.Count; index++)
        {
            var step = definition.Steps[index];
            var path = $"$.steps[{index}]";
            if (!StepIdPattern().IsMatch(step.Id))
            {
                AddError(issues, "step.id", path + ".id",
                    "步骤 ID 必须是小写字母开头的 kebab-case，最长 64 字符。");
            }
            if (!seen.Add(step.Id))
            {
                AddError(issues, "step.duplicate", path + ".id", "步骤 ID 必须唯一。");
            }
            if (!catalog.TryGet(step.ActionId, out var descriptor))
            {
                AddError(issues, "action.unknown", path + ".actionId", "Action 不存在或所有者当前不可用。");
                continue;
            }

            JsonElement? itemSchema = null;
            JsonElement? forEachSchema = null;
            if (step.ForEach is null)
            {
                maximumInvocations++;
            }
            else
            {
                if (!WorkflowReferenceToken.TryParse(step.ForEach, out var eachToken) ||
                    eachToken!.Kind != WorkflowReferenceKind.Step)
                {
                    AddError(issues, "foreach.reference", path + ".forEach",
                        "ForEach 必须引用前序步骤的数组输出。");
                }
                else if (!TryResolvePriorSchema(eachToken, prior, out var arraySchema,
                             out var failureCode, out var failure))
                {
                    AddError(issues, failureCode, path + ".forEach", failure);
                }
                else if (WorkflowJsonSchemaValidator.SchemaType(arraySchema) != "array" ||
                         !arraySchema.TryGetProperty("items", out var resolvedItemSchema))
                {
                    AddError(issues, "foreach.array", path + ".forEach",
                        "ForEach 来源必须是带 items Schema 的数组。");
                }
                else
                {
                    forEachSchema = arraySchema;
                    itemSchema = resolvedItemSchema;
                    var maximum = arraySchema.GetProperty("maxItems").GetInt32();
                    maximumInvocations += maximum;
                    if (maximum > _limits.MaximumForEachItems)
                    {
                        AddError(issues, "budget.foreach", path + ".forEach",
                            $"ForEach 来源 Schema 上限不能超过 {_limits.MaximumForEachItems}。");
                    }
                }
            }

            schemaValidator.Validate(step.Arguments, descriptor!.InputSchema,
                path + ".arguments", true, issues);
            ValidateReferences(step.Arguments, descriptor.InputSchema, prior, itemSchema,
                path + ".arguments", issues);
            ValidateSensitivePointers(step.Arguments, descriptor, path + ".arguments", issues);
            prior[step.Id] = forEachSchema is null
                ? descriptor.OutputSchema.Clone()
                : BuildForEachOutputSchema(descriptor.OutputSchema, forEachSchema.Value);
        }

        if (maximumInvocations > _limits.MaximumInvocations)
        {
            AddError(issues, "budget.invocations", "$.steps",
                $"按 ForEach 上限估算的调用数超过 {_limits.MaximumInvocations}。");
        }
        return new WorkflowValidationResult(issues);
    }

    private void ValidateReferences(
        JsonElement value,
        JsonElement targetSchema,
        IReadOnlyDictionary<string, JsonElement> prior,
        JsonElement? itemSchema,
        string path,
        IList<WorkflowValidationIssue> issues)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (!WorkflowReferenceToken.TryParse(text, out var token))
            {
                if (text.Contains("${", StringComparison.Ordinal))
                {
                    AddError(issues, "reference.syntax", path,
                        "引用必须占据整个字段值并使用受支持语法。");
                }
                return;
            }
            ValidateReferenceToken(token!, targetSchema, prior, itemSchema, path, issues);
            return;
        }
        if (value.ValueKind == JsonValueKind.Object &&
            WorkflowJsonSchemaValidator.SchemaType(targetSchema) == "object")
        {
            var properties = targetSchema.GetProperty("properties");
            foreach (var property in value.EnumerateObject())
            {
                if (properties.TryGetProperty(property.Name, out var childSchema))
                {
                    ValidateReferences(property.Value, childSchema, prior, itemSchema,
                        path + "." + property.Name, issues);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array &&
                 WorkflowJsonSchemaValidator.SchemaType(targetSchema) == "array")
        {
            var childSchema = targetSchema.GetProperty("items");
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateReferences(item, childSchema, prior, itemSchema,
                    $"{path}[{index++}]", issues);
            }
        }
    }

    private void ValidateReferenceToken(
        WorkflowReferenceToken token,
        JsonElement targetSchema,
        IReadOnlyDictionary<string, JsonElement> prior,
        JsonElement? itemSchema,
        string path,
        IList<WorkflowValidationIssue> issues)
    {
        if (token.Kind == WorkflowReferenceKind.Secret)
        {
            if (!secrets.TryGet(token.Root, out var secret))
            {
                AddError(issues, "secret.missing", path, $"当前会话缺少 Secret：{token.Root}。");
                return;
            }
            var value = JsonSerializer.SerializeToElement(secret);
            var result = sharedSchema.ValidateInstance(
                targetSchema, value, WorkflowSchemaProfile.MaximumInputBytes, path);
            if (!result.IsValid)
            {
                AddError(issues, "reference.secret-type", path,
                    "当前 Secret 值不符合目标参数 Schema。");
            }
            return;
        }

        JsonElement sourceSchema;
        if (token.Kind == WorkflowReferenceKind.Item)
        {
            if (itemSchema is null)
            {
                AddError(issues, "reference.item", path, "item 引用只能用于 ForEach 步骤。");
                return;
            }
            var resolved = WorkflowReferencePath.ResolveGuaranteedSchemaPath(itemSchema.Value, token.Path);
            if (!resolved.Succeeded)
            {
                AddPathFailure(issues, resolved, path, "item 引用路径无效。");
                return;
            }
            sourceSchema = resolved.Value!.Value;
        }
        else if (!TryResolvePriorSchema(token, prior, out sourceSchema,
                     out var failureCode, out var failure))
        {
            AddError(issues, failureCode, path, failure);
            return;
        }

        var compatibility = referenceTypes.ValidateAssignable(sourceSchema, targetSchema, path);
        foreach (var issue in compatibility.Issues)
        {
            AddError(issues, issue.Code, issue.Path, issue.Message);
        }
    }

    private static bool TryResolvePriorSchema(
        WorkflowReferenceToken token,
        IReadOnlyDictionary<string, JsonElement> prior,
        out JsonElement schema,
        out string failureCode,
        out string failure)
    {
        schema = default;
        if (!prior.TryGetValue(token.Root, out var ownerSchema))
        {
            failureCode = "reference.step";
            failure = "只能引用已经排在当前步骤之前的输出。";
            return false;
        }
        var result = WorkflowReferencePath.ResolveGuaranteedSchemaPath(ownerSchema, token.Path);
        if (!result.Succeeded)
        {
            failureCode = PathFailureCode(result.Failure);
            failure = PathFailureMessage(result.Failure);
            return false;
        }
        schema = result.Value!.Value;
        failureCode = string.Empty;
        failure = string.Empty;
        return true;
    }

    private static JsonElement BuildForEachOutputSchema(JsonElement output, JsonElement sourceArray)
    {
        var minimum = sourceArray.TryGetProperty("minItems", out var min) ? min.GetInt32() : 0;
        var maximum = sourceArray.GetProperty("maxItems").GetInt32();
        return JsonSerializer.SerializeToElement(new
        {
            type = "array",
            minItems = minimum,
            maxItems = maximum,
            items = output,
        });
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
                AddError(issues, "secret.required", path + pointer.Replace('/', '.'),
                    "敏感输入必须使用会话 Secret 引用，不能写入明文常量。");
            }
        }
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

    private static void AddPathFailure(
        IList<WorkflowValidationIssue> issues,
        WorkflowReferencePathResult result,
        string path,
        string fallback) => AddError(issues, PathFailureCode(result.Failure), path,
        result.Failure == WorkflowReferencePathFailure.None ? fallback : PathFailureMessage(result.Failure));

    private static string PathFailureCode(WorkflowReferencePathFailure failure) => failure switch
    {
        WorkflowReferencePathFailure.OptionalProperty => "reference.optional",
        WorkflowReferencePathFailure.InvalidArrayIndex or
            WorkflowReferencePathFailure.ArrayIndexNotGuaranteed => "reference.array-index",
        _ => "reference.path",
    };

    private static string PathFailureMessage(WorkflowReferencePathFailure failure) => failure switch
    {
        WorkflowReferencePathFailure.OptionalProperty => "引用路径未由输出 Schema 的 required 保证存在。",
        WorkflowReferencePathFailure.InvalidArrayIndex => "数组引用段必须是非负十进制索引。",
        WorkflowReferencePathFailure.ArrayIndexNotGuaranteed => "数组索引未由来源 minItems 保证存在。",
        _ => "引用路径不在来源 Schema 中。",
    };

    private static void AddError(
        IList<WorkflowValidationIssue> issues,
        string code,
        string path,
        string message) => issues.Add(new(WorkflowValidationSeverity.Error, code, path, message));

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();
}

/// <summary>从已冻结定义和目录快照生成用户确认所需的保守风险摘要。</summary>
public interface IWorkflowRiskSummaryBuilder
{
    WorkflowRiskSummary Build(
        WorkflowDefinitionV2 definition,
        WorkflowActionCatalogSnapshot catalog);
}

/// <summary>以所有步骤风险并集和最高确认策略构造朴素风险摘要。</summary>
public sealed class WorkflowRiskSummaryBuilder : IWorkflowRiskSummaryBuilder
{
    public WorkflowRiskSummary Build(
        WorkflowDefinitionV2 definition,
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
