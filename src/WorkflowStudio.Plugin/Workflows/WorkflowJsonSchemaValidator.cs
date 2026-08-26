using System.Text.Json;
using MyAvaloniaManagement.PluginSdk.Workflow;

namespace WorkflowStudio.Workflows;

public interface IWorkflowJsonSchemaValidator
{
    void Validate(
        JsonElement value,
        JsonElement schema,
        string path,
        bool allowReferenceTokens,
        IList<WorkflowValidationIssue> issues);
}

/// <summary>把共享 Schema 校验器适配到允许引用占位符的 Studio 编辑期模型。</summary>
/// <remarks>
/// 常量叶节点完全交给 PluginSdk.Workflow；对象和数组只负责绕过合法引用占位符并继续遍历，
/// 因而 Unicode、decimal 和边界语义不会再由 Studio 单独实现。
/// </remarks>
public sealed class WorkflowJsonSchemaValidator : IWorkflowJsonSchemaValidator
{
    private readonly WorkflowSchemaValidator _shared = new();

    public void Validate(
        JsonElement value,
        JsonElement schema,
        string path,
        bool allowReferenceTokens,
        IList<WorkflowValidationIssue> issues)
    {
        if (allowReferenceTokens && value.ValueKind == JsonValueKind.String &&
            WorkflowReferenceToken.TryParse(value.GetString(), out _))
        {
            return;
        }
        if (value.ValueKind == JsonValueKind.Object && SchemaType(schema) == "object")
        {
            ValidateObject(value, schema, path, allowReferenceTokens, issues);
            return;
        }
        if (value.ValueKind == JsonValueKind.Array && SchemaType(schema) == "array")
        {
            ValidateArray(value, schema, path, allowReferenceTokens, issues);
            return;
        }
        AppendShared(_shared.ValidateInstance(
            schema, value, WorkflowSchemaProfile.MaximumInputBytes, path), issues);
    }

    internal static string? SchemaType(JsonElement schema) =>
        schema.TryGetProperty("type", out var type) ? type.GetString() : null;

    private void ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        bool allowReferences,
        IList<WorkflowValidationIssue> issues)
    {
        var properties = schema.GetProperty("properties");
        var required = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var name in required)
        {
            if (!value.TryGetProperty(name, out _))
            {
                AddError(issues, "instance.required", path + "." + name, "缺少必需字段。");
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                AddError(issues, "instance.duplicate", path + "." + property.Name, "字段重复。");
            }
            else if (!properties.TryGetProperty(property.Name, out var propertySchema))
            {
                AddError(issues, "instance.additional", path + "." + property.Name,
                    "字段不在 Action Schema 中。");
            }
            else
            {
                Validate(property.Value, propertySchema, path + "." + property.Name,
                    allowReferences, issues);
            }
        }
    }

    private void ValidateArray(
        JsonElement value,
        JsonElement schema,
        string path,
        bool allowReferences,
        IList<WorkflowValidationIssue> issues)
    {
        var count = value.GetArrayLength();
        var maximum = schema.GetProperty("maxItems").GetInt32();
        var minimum = schema.TryGetProperty("minItems", out var min) ? min.GetInt32() : 0;
        if (count < minimum || count > maximum)
        {
            AddError(issues, "instance.array.bounds", path, "数组项数不符合 Action Schema。");
        }
        var itemSchema = schema.GetProperty("items");
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            Validate(item, itemSchema, $"{path}[{index++}]", allowReferences, issues);
        }
    }

    private static void AppendShared(
        WorkflowSchemaValidationResult result,
        IList<WorkflowValidationIssue> issues)
    {
        foreach (var issue in result.Issues)
        {
            AddError(issues, issue.Code, issue.Path, issue.Message);
        }
    }

    private static void AddError(
        IList<WorkflowValidationIssue> issues,
        string code,
        string path,
        string message) => issues.Add(new(WorkflowValidationSeverity.Error, code, path, message));
}
