using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows;

public interface IWorkflowActionCatalogProjection
{
    WorkflowActionCatalogSnapshot Capture();
}
/// <summary>表示同一次 Gateway 目录读取产生的不可变动作集合与 Studio revision。</summary>
public sealed class WorkflowActionCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, WorkflowActionDescriptor> _byId;

    public WorkflowActionCatalogSnapshot(
        string revision,
        IReadOnlyList<WorkflowActionDescriptor> actions)
    {
        Revision = revision ?? throw new ArgumentNullException(nameof(revision));
        ArgumentNullException.ThrowIfNull(actions);
        Actions = new ReadOnlyCollection<WorkflowActionDescriptor>(actions.ToArray());
        _byId = new ReadOnlyDictionary<string, WorkflowActionDescriptor>(
            actions.ToDictionary(item => item.Id.Value, StringComparer.Ordinal));
    }

    public string Revision { get; }
    public IReadOnlyList<WorkflowActionDescriptor> Actions { get; }

    public bool TryGet(WorkflowActionId actionId, out WorkflowActionDescriptor? descriptor) =>
        _byId.TryGetValue(actionId.Value, out descriptor);
}

/// <summary>
/// 把公开 Descriptor 投影为 Studio 所需的目录快照。revision 由规范 JSON 计算，不依赖对象地址、
/// 当前区域或 Gateway 返回顺序，因此相同目录在不同进程中得到相同结果。
/// </summary>
public sealed class WorkflowActionCatalogProjection(IWorkflowActionGateway gateway)
    : IWorkflowActionCatalogProjection
{
    public WorkflowActionCatalogSnapshot Capture()
    {
        var actions = gateway.GetAvailableActions()
            .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var bytes = BuildCanonicalCatalog(actions);
        return new WorkflowActionCatalogSnapshot(
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            actions);
    }

    internal static byte[] BuildCanonicalCatalog(IReadOnlyList<WorkflowActionDescriptor> actions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var action in actions)
            {
                writer.WriteStartObject();
                writer.WriteString("id", action.Id.Value);
                writer.WriteString("displayName", action.DisplayName);
                writer.WriteString("description", action.Description);
                writer.WritePropertyName("inputSchema");
                WriteCanonicalJson(writer, action.InputSchema);
                writer.WritePropertyName("outputSchema");
                WriteCanonicalJson(writer, action.OutputSchema);
                writer.WriteNumber("risks", (int)action.Risks);
                writer.WriteNumber("confirmationPolicy", (int)action.ConfirmationPolicy);
                writer.WritePropertyName("sensitiveInputPointers");
                writer.WriteStartArray();
                foreach (var pointer in action.SensitiveInputPointers.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(pointer);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }

    internal static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"目录中出现不支持的 JSON 类型：{element.ValueKind}。");
        }
    }
}
