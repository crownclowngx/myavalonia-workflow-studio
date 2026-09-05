using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows;

/// <summary>
/// 同步观察一次 Gateway 终态；观察器不得保存通用输出或改变调用结果。Begin 必须核对冻结定义，
/// End 解除关联。这个窄端口只服务会话投影，不引入事件总线、持久化或自动重试。
/// </summary>
public interface IWorkflowInvocationObserver
{
    void Begin(WorkflowDefinitionV2 definition);
    void Started(string stepId, int? itemIndex);
    void Observe(string stepId, int? itemIndex, WorkflowActionDescriptor descriptor,
        WorkflowActionInvocationStatus status, JsonElement? output);
    void End();
}
