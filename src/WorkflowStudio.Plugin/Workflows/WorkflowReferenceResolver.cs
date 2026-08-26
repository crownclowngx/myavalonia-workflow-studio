using System.Text.Json;
using System.Text.Json.Nodes;
using MyAvaloniaManagement.PluginSdk.Workflow;

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
        var parts = value[2..^1].Split('.', StringSplitOptions.None);
        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }
        if (parts[0] == "secret" && parts.Length == 2)
        {
            token = new(WorkflowReferenceKind.Secret, parts[1], []);
            return true;
        }
        if (parts[0] == "item" && parts.Length >= 2)
        {
            token = new(WorkflowReferenceKind.Item, "item", parts[1..]);
            return true;
        }
        if (parts.Length >= 3 && parts[1] == "result")
        {
            token = new(WorkflowReferenceKind.Step, parts[0], parts[2..]);
            return true;
        }
        return false;
    }
}

/// <summary>表示已经通过静态验证的引用在运行快照中仍无法解析。</summary>
/// <remarks>
/// 异常只携带稳定代码、逻辑路径和脱敏消息。Runner 只捕获这一明确类型并转为
/// WorkflowRunFailure，避免把编程错误误报成普通引用失败。
/// </remarks>
public sealed class WorkflowReferenceResolutionException(
    string code,
    string path,
    string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public string Path { get; } = path;
}

/// <summary>按共享 WorkflowReferencePath 语义解析步骤输出、item 与会话 Secret。</summary>
public interface IWorkflowReferenceResolver
{
    JsonElement ResolveArguments(
        JsonElement arguments,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? item,
        string path);

    JsonElement ResolveToken(
        string token,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? item,
        string path);
}

/// <summary>解析只占据完整 JSON 字符串的简单引用，并提供稳定、脱敏的失败类型。</summary>
/// <remarks>
/// 对象与数组递归仅重建当前调用参数，不缓存展开后的 Secret。静态 Schema 与运行时 JSON
/// 使用共享包中的同一套数组 segment 语法，避免“静态允许、运行拒绝”的协议漂移。
/// </remarks>
public sealed class WorkflowReferenceResolver(ISessionSecretStore secrets) : IWorkflowReferenceResolver
{
    public JsonElement ResolveArguments(
        JsonElement arguments,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? item,
        string path)
    {
        var node = ResolveNode(arguments, stepOutputs, item, path);
        return JsonSerializer.SerializeToElement(node);
    }

    public JsonElement ResolveToken(
        string token,
        IReadOnlyDictionary<string, JsonElement> stepOutputs,
        JsonElement? item,
        string path)
    {
        if (!WorkflowReferenceToken.TryParse(token, out var parsed))
        {
            throw Failure("reference.syntax", path, "引用格式不合法。");
        }
        return ResolveReference(parsed!, stepOutputs, item, path);
    }

    private JsonNode? ResolveNode(
        JsonElement element,
        IReadOnlyDictionary<string, JsonElement> outputs,
        JsonElement? item,
        string path)
    {
        if (element.ValueKind == JsonValueKind.String &&
            WorkflowReferenceToken.TryParse(element.GetString(), out var token))
        {
            return JsonNode.Parse(ResolveReference(token!, outputs, item, path).GetRawText());
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in element.EnumerateObject())
            {
                result.Add(property.Name, ResolveNode(property.Value, outputs, item,
                    path + "." + property.Name));
            }
            return result;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            var index = 0;
            foreach (var child in element.EnumerateArray())
            {
                result.Add(ResolveNode(child, outputs, item, $"{path}[{index++}]"));
            }
            return result;
        }
        return JsonNode.Parse(element.GetRawText());
    }

    private JsonElement ResolveReference(
        WorkflowReferenceToken token,
        IReadOnlyDictionary<string, JsonElement> outputs,
        JsonElement? item,
        string path)
    {
        if (token.Kind == WorkflowReferenceKind.Secret)
        {
            if (!secrets.TryGet(token.Root, out var value))
            {
                throw Failure("reference.secret-missing", path, "当前会话缺少所需 Secret。");
            }
            return JsonSerializer.SerializeToElement(value);
        }

        JsonElement root;
        if (token.Kind == WorkflowReferenceKind.Item)
        {
            root = item ?? throw Failure("reference.item-missing", path,
                "item 引用只能用于 ForEach 步骤。");
        }
        else if (!outputs.TryGetValue(token.Root, out root))
        {
            throw Failure("reference.output-missing", path, "引用的前序步骤尚无输出。");
        }

        var resolved = WorkflowReferencePath.ResolveValuePath(root, token.Path);
        if (!resolved.Succeeded)
        {
            throw Failure("reference.path-missing", path, "引用路径在运行输出中不存在。");
        }
        return resolved.Value!.Value;
    }

    private static WorkflowReferenceResolutionException Failure(
        string code,
        string path,
        string message) => new(code, path, message);
}
