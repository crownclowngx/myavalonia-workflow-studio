using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkflowStudio.Workflows;

internal enum WorkflowReferenceKind { Step, Item, Secret }

internal sealed record WorkflowReferenceToken(
    WorkflowReferenceKind Kind,
    string Root,
    IReadOnlyList<string> Path)
{
    internal static bool TryParse(string? value, out WorkflowReferenceToken? token)
    {
        token = null;
        if (value is null || value.Length < 4 || !value.StartsWith("${", StringComparison.Ordinal) ||
            !value.EndsWith('}'))
        {
            return false;
        }
        var body = value[2..^1];
        var parts = body.Split('.', StringSplitOptions.None);
        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }
        if (parts[0] == "secret" && parts.Length == 2)
        {
            token = new WorkflowReferenceToken(WorkflowReferenceKind.Secret, parts[1], []);
            return true;
        }
        if (parts[0] == "item" && parts.Length >= 2)
        {
            token = new WorkflowReferenceToken(WorkflowReferenceKind.Item, "item", parts[1..]);
            return true;
        }
        if (parts.Length >= 3 && parts[1] == "result")
        {
            token = new WorkflowReferenceToken(WorkflowReferenceKind.Step, parts[0], parts[2..]);
            return true;
        }
        return false;
    }
}
public interface IWorkflowReferenceResolver
{
    JsonElement ResolveArguments(
        JsonElement arguments,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? item);

    JsonElement ResolveToken(
        string token,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? item);
}

/// <summary>
/// 只解析“整个 JSON 字符串就是一个引用”的简单语法。故意不支持字符串插值、表达式或函数，
/// 让验证结果与执行结果保持一致，也避免把 Workflow Studio 演变为脚本引擎。
/// </summary>
public sealed class WorkflowReferenceResolver(ISessionSecretStore secrets) : IWorkflowReferenceResolver
{
    public JsonElement ResolveArguments(
        JsonElement arguments,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? item)
    {
        var node = ResolveNode(arguments, stepOutputs, item);
        return JsonSerializer.SerializeToElement(node);
    }

    public JsonElement ResolveToken(
        string token,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? item)
    {
        if (!WorkflowReferenceToken.TryParse(token, out var parsed))
        {
            throw new InvalidOperationException("引用格式不合法。");
        }
        return ResolveReference(parsed!, stepOutputs, item);
    }

    private JsonNode? ResolveNode(
        JsonElement element,
        IReadOnlyDictionary<string, JsonElement> outputs,
        JsonElement? item)
    {
        if (element.ValueKind == JsonValueKind.String &&
            WorkflowReferenceToken.TryParse(element.GetString(), out var token))
        {
            return JsonNode.Parse(ResolveReference(token!, outputs, item).GetRawText());
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in element.EnumerateObject())
            {
                result.Add(property.Name, ResolveNode(property.Value, outputs, item));
            }
            return result;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var child in element.EnumerateArray())
            {
                result.Add(ResolveNode(child, outputs, item));
            }
            return result;
        }
        return JsonNode.Parse(element.GetRawText());
    }

    private JsonElement ResolveReference(
        WorkflowReferenceToken token,
        IReadOnlyDictionary<string, JsonElement> outputs,
        JsonElement? item)
    {
        if (token.Kind == WorkflowReferenceKind.Secret)
        {
            if (!secrets.TryGet(token.Root, out var value))
            {
                throw new InvalidOperationException("当前会话缺少所需 Secret。");
            }
            return JsonSerializer.SerializeToElement(value);
        }

        JsonElement current;
        if (token.Kind == WorkflowReferenceKind.Item)
        {
            current = item ?? throw new InvalidOperationException("item 引用只能用于 ForEach 步骤。");
        }
        else if (!outputs.TryGetValue(token.Root, out current))
        {
            throw new InvalidOperationException("引用的前序步骤尚无输出。");
        }

        foreach (var segment in token.Path)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var property))
            {
                current = property;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) &&
                     index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                throw new InvalidOperationException("引用路径在运行输出中不存在。");
            }
        }
        return current.Clone();
    }
}
