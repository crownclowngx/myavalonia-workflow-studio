using System.Globalization;
using System.Text;
using System.Text.Json;

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
/// <summary>
/// 实现 SDK 3.1 Profile 中 Studio 编辑器实际需要的确定性子集。Host 仍会在调用边界做最终治理；
/// 此处的价值是让错误在点击执行前以字段路径展示，而不是取代 Host 的安全校验。
/// </summary>
public sealed class WorkflowJsonSchemaValidator : IWorkflowJsonSchemaValidator
{
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

        var expected = schema.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        if (!MatchesType(value, expected))
        {
            issues.Add(new("schema.type", path, $"值类型必须是 {expected}。"));
            return;
        }

        if (schema.TryGetProperty("enum", out var enumElement) &&
            !enumElement.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
        {
            issues.Add(new("schema.enum", path, "值不在允许的枚举集合中。"));
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (Encoding.UTF8.GetByteCount(text) > WorkflowStudioLimits.Default.MaximumStringBytes)
            {
                issues.Add(new("budget.string", path, "字符串超过 64 KiB 上限。"));
            }
            if (schema.TryGetProperty("minLength", out var min) && text.Length < min.GetInt32())
            {
                issues.Add(new("schema.minLength", path, $"字符串长度不能小于 {min.GetInt32()}。"));
            }
            if (schema.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32())
            {
                issues.Add(new("schema.maxLength", path, $"字符串长度不能大于 {max.GetInt32()}。"));
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(value, schema, path, allowReferenceTokens, issues);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            ValidateArray(value, schema, path, allowReferenceTokens, issues);
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            ValidateNumber(value, schema, path, issues);
        }
    }

    internal static bool TryResolveSchemaPath(
        JsonElement schema,
        IReadOnlyList<string> path,
        out JsonElement resolved)
    {
        resolved = schema;
        foreach (var segment in path)
        {
            var type = resolved.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "object" && resolved.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty(segment, out var propertySchema))
            {
                resolved = propertySchema;
            }
            else if (type == "array" && resolved.TryGetProperty("items", out var itemSchema))
            {
                resolved = itemSchema;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    internal static string? SchemaType(JsonElement schema) =>
        schema.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static bool MatchesType(JsonElement value, string? expected) => expected switch
    {
        null => true,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private void ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        bool allowReferences,
        IList<WorkflowValidationIssue> issues)
    {
        var required = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var name in required)
        {
            if (!value.TryGetProperty(name, out _))
            {
                issues.Add(new("schema.required", path + "." + name, "缺少必需字段。"));
            }
        }
        var hasProperties = schema.TryGetProperty("properties", out var properties);
        foreach (var property in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Validate(property.Value, propertySchema, path + "." + property.Name, allowReferences, issues);
            }
            else if (schema.TryGetProperty("additionalProperties", out var additional) &&
                     additional.ValueKind == JsonValueKind.False)
            {
                issues.Add(new("schema.additionalProperties", path + "." + property.Name, "字段不在 Action Schema 中。"));
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
        if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())
        {
            issues.Add(new("schema.maxItems", path, $"数组项数不能超过 {max.GetInt32()}。"));
        }
        if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32())
        {
            issues.Add(new("schema.minItems", path, $"数组项数不能少于 {min.GetInt32()}。"));
        }
        if (schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Validate(item, itemSchema, $"{path}[{index++}]", allowReferences, issues);
            }
        }
    }

    private static void ValidateNumber(
        JsonElement value,
        JsonElement schema,
        string path,
        IList<WorkflowValidationIssue> issues)
    {
        var number = value.GetDecimal();
        if (schema.TryGetProperty("minimum", out var min) && number < min.GetDecimal())
        {
            issues.Add(new("schema.minimum", path, $"数值不能小于 {min.GetRawText()}。"));
        }
        if (schema.TryGetProperty("maximum", out var max) && number > max.GetDecimal())
        {
            issues.Add(new("schema.maximum", path, $"数值不能大于 {max.GetRawText()}。"));
        }
    }
}
