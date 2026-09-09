using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows;

/// <summary>
/// 识别归档 v1 已公开的业务结果。SDK Succeeded 只保证 Handler 返回合法 JSON；归档批次允许部分失败，
/// 必须继续传递明确成功项并运行后续释放步骤，同时不能在最终摘要中把失败批次展示为成功。
/// 此适配只读取 SDK 标识和 BCL JSON，不引用归档插件 CLR 类型，也不成为通用表达式或条件执行框架。
/// </summary>
internal static class ArchiveWorkflowOutcome
{
    internal static WorkflowActionFailure? Inspect(WorkflowActionId action, JsonElement? output)
    {
        if (action.Value is not ("myavalonia.plugin.layer.unpack.workflow.unpack-v1" or
            "myavalonia.plugin.layer.unpack.workflow.create-v1")) return null;
        if (output is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String ||
            contract.GetString() != "myavalonia.layer-unpack.workflow-result" ||
            !value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
            !value.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            return new("archive.result-invalid", "归档动作的结果契约无法识别，请核对插件版本。");
        return state.GetString() switch
        {
            "completed" or "skipped" => null,
            "partial-failure" => new("archive.partial-failure", "归档批次部分失败；后续步骤仅消费显式成功产物。"),
            "failed" => new("archive.failed", "归档批次失败；未将失败来源交给后续步骤。"),
            _ => new("archive.result-invalid", "归档动作返回了未知业务状态。")
        };
    }
}
