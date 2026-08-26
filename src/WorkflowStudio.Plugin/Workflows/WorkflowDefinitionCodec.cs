using System.Text;
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows;

/// <summary>定义 Workflow v2 严格 JSON 编解码边界。</summary>
public interface IWorkflowDefinitionCodec
{
    WorkflowDefinitionV2 Parse(string json);
    string Serialize(WorkflowDefinitionV2 definition);
}
/// <summary>
/// 严格读取和规范写出定义 v2。定义结构使用白名单字段；arguments 内部保留 Action 自己的 JSON，
/// 但仍拒绝重复属性，避免“显示一个值、执行另一个值”的歧义输入。
/// </summary>
/// <remarks>
/// 根节点未知字段、重复字段、v1 与混合版本全部作为脱敏格式错误拒绝。这里不承担版本迁移，
/// 使导入后的对象必然只有一种可验证含义。
/// </remarks>
public sealed class WorkflowDefinitionCodec : IWorkflowDefinitionCodec
{
    private static readonly HashSet<string> RootProperties =
        ["schemaVersion", "contractRevision", "presentationRevision", "summary", "steps"];
    private static readonly HashSet<string> StepProperties =
        ["id", "actionId", "forEach", "arguments"];
    private readonly WorkflowStudioLimits _limits;

    public WorkflowDefinitionCodec() : this(WorkflowStudioLimits.Default) { }

    internal WorkflowDefinitionCodec(WorkflowStudioLimits limits) => _limits = limits;

    public WorkflowDefinitionV2 Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > _limits.MaximumDefinitionBytes)
        {
            throw new WorkflowDefinitionFormatException("工作流定义超过 256 KiB 上限。");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            RejectDuplicateProperties(document.RootElement, "$");
            RequireObject(document.RootElement, "根节点");
            RejectUnknown(document.RootElement, RootProperties, "根节点");
            RequireProperties(document.RootElement, RootProperties, "根节点");

            var schemaVersion = document.RootElement.GetProperty("schemaVersion").GetInt32();
            var contractRevision = RequireString(
                document.RootElement.GetProperty("contractRevision"), "contractRevision");
            var presentationRevision = RequireString(
                document.RootElement.GetProperty("presentationRevision"), "presentationRevision");
            var summary = RequireString(document.RootElement.GetProperty("summary"), "summary");
            var stepsElement = document.RootElement.GetProperty("steps");
            if (stepsElement.ValueKind != JsonValueKind.Array)
            {
                throw new WorkflowDefinitionFormatException("steps 必须是数组。");
            }

            var steps = new List<WorkflowStepDefinition>();
            var index = 0;
            foreach (var element in stepsElement.EnumerateArray())
            {
                RequireObject(element, $"steps[{index}]");
                RejectUnknown(element, StepProperties, $"steps[{index}]");
                foreach (var required in new[] { "id", "actionId", "arguments" })
                {
                    if (!element.TryGetProperty(required, out _))
                    {
                        throw new WorkflowDefinitionFormatException($"steps[{index}] 缺少字段 {required}。");
                    }
                }

                var arguments = element.GetProperty("arguments");
                RequireObject(arguments, $"steps[{index}].arguments");
                string? forEach = null;
                if (element.TryGetProperty("forEach", out var forEachElement))
                {
                    forEach = RequireString(forEachElement, $"steps[{index}].forEach");
                }
                steps.Add(new WorkflowStepDefinition(
                    RequireString(element.GetProperty("id"), $"steps[{index}].id"),
                    new WorkflowActionId(RequireString(
                        element.GetProperty("actionId"), $"steps[{index}].actionId")),
                    arguments,
                    forEach));
                index++;
            }

            if (schemaVersion != 2)
            {
                throw new WorkflowDefinitionFormatException("只接受 schemaVersion 2；v1 不提供兼容导入。 ");
            }
            return new WorkflowDefinitionV2(
                schemaVersion, contractRevision, presentationRevision, summary, steps);
        }
        catch (WorkflowDefinitionFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                          ArgumentException or OverflowException)
        {
            // 不把原始 JSON 或异常中可能包含的输入片段向 UI/诊断传播。
            throw new WorkflowDefinitionFormatException("工作流定义不是合法的严格 v2 JSON。", exception);
        }
    }

    public string Serialize(WorkflowDefinitionV2 definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", definition.SchemaVersion);
            writer.WriteString("contractRevision", definition.ContractRevision);
            writer.WriteString("presentationRevision", definition.PresentationRevision);
            writer.WriteString("summary", definition.Summary);
            writer.WritePropertyName("steps");
            writer.WriteStartArray();
            foreach (var step in definition.Steps)
            {
                writer.WriteStartObject();
                writer.WriteString("id", step.Id);
                writer.WriteString("actionId", step.ActionId.Value);
                if (step.ForEach is not null)
                {
                    writer.WriteString("forEach", step.ForEach);
                }
                writer.WritePropertyName("arguments");
                WorkflowActionCatalogProjection.WriteCanonicalJson(writer, step.Arguments);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowDefinitionFormatException($"{path} 必须是对象。");
        }
    }

    private static string RequireString(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new WorkflowDefinitionFormatException($"{path} 必须是字符串。");

    private static void RejectUnknown(JsonElement element, HashSet<string> allowed, string path)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new WorkflowDefinitionFormatException($"{path} 包含未知字段 {property.Name}。");
            }
        }
    }

    private static void RequireProperties(JsonElement element, HashSet<string> required, string path)
    {
        foreach (var name in required)
        {
            if (!element.TryGetProperty(name, out _))
            {
                throw new WorkflowDefinitionFormatException($"{path} 缺少字段 {name}。");
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new WorkflowDefinitionFormatException($"{path} 包含重复字段 {property.Name}。");
                }
                RejectDuplicateProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }
}
