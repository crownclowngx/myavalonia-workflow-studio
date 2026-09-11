using MyAvaloniaManagement.PluginSdk.UI;

namespace WorkflowStudio.Plugin;

/// <summary>本插件专属矢量。保持纯不可变数据，不保存 Host 服务、控件或主题画刷。</summary>
internal static class PluginIcons
{
    /// <summary>相连的流程节点。公共八图标无法准确表达该语义，使用原创 20×20 填充路径。</summary>
    internal static VectorIconDefinition Workflow { get; } = new(
        "M1,2h6v6h-6Z M2.5,3.5h3.0v3.0h-3.0Z M13,2h6v6h-6Z M14.5,3.5h3.0v3.0h-3.0Z M7,12h6v6h-6Z M8.5,13.5h3.0v3.0h-3.0Z M7,4h6v2H7Z M3,8h2v2h6v2H9v-1H3Z", 20, 20);

}
