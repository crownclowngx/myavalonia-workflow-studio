using System.Collections.ObjectModel;
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;

namespace WorkflowStudio.Workflows;

/// <summary>从 Host Gateway 捕获一次不可变 Action 目录快照。</summary>
public interface IWorkflowActionCatalogProjection
{
    WorkflowActionCatalogSnapshot Capture();
}
/// <summary>表示同一次 Gateway 目录读取产生的不可变动作集合与双 revision。</summary>
/// <remarks>
/// ContractRevision 参与定义可执行性和授权指纹；PresentationRevision 只用于提示名称、说明或
/// Schema description 已更新。二者必须由同一 actions 快照一次计算，不能跨目录读取拼接。
/// </remarks>
public sealed class WorkflowActionCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, WorkflowActionDescriptor> _byId;

    public WorkflowActionCatalogSnapshot(
        string contractRevision,
        string presentationRevision,
        IReadOnlyList<WorkflowActionDescriptor> actions)
    {
        ContractRevision = contractRevision ?? throw new ArgumentNullException(nameof(contractRevision));
        PresentationRevision = presentationRevision ?? throw new ArgumentNullException(nameof(presentationRevision));
        ArgumentNullException.ThrowIfNull(actions);
        Actions = new ReadOnlyCollection<WorkflowActionDescriptor>(actions.ToArray());
        _byId = new ReadOnlyDictionary<string, WorkflowActionDescriptor>(
            actions.ToDictionary(item => item.Id.Value, StringComparer.Ordinal));
    }

    public string ContractRevision { get; }
    public string PresentationRevision { get; }
    public IReadOnlyList<WorkflowActionDescriptor> Actions { get; }

    public bool TryGet(WorkflowActionId actionId, out WorkflowActionDescriptor? descriptor) =>
        _byId.TryGetValue(actionId.Value, out descriptor);
}

/// <summary>
/// 把公开 Descriptor 投影为 Studio 所需的目录快照。revision 由共享规范算法计算，不依赖对象地址、
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
        var revisions = WorkflowCatalogRevisionCalculator.Calculate(actions);
        return new WorkflowActionCatalogSnapshot(
            revisions.ContractRevision,
            revisions.PresentationRevision,
            actions);
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
