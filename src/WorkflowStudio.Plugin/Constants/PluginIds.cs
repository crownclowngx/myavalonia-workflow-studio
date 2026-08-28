using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;

namespace WorkflowStudio.Constants;

public static class PluginIds
{
    public static readonly PluginId Plugin = new("myavalonia.plugin.workflow-studio");

    public static readonly DocumentTypeId StudioDocument =
        new("myavalonia.plugin.workflow-studio.document.studio");

    /// <summary>获取“验证当前工作流”工作台命令的稳定身份。</summary>
    /// <remarks>
    /// 工作台只通过这个值定位用户语义，不持有 <see cref="System.Windows.Input.ICommand"/>、
    /// <c>MainDocument</c> 或执行回调。实际执行目标始终由 Host 路由到当前活动的 Studio Document 实例。
    /// </remarks>
    public static readonly CommandId ValidateWorkflow =
        new("myavalonia.plugin.workflow-studio.command.validate");

    /// <summary>获取“运行当前工作流”工作台命令的稳定身份。</summary>
    public static readonly CommandId RunWorkflow =
        new("myavalonia.plugin.workflow-studio.command.run");

    /// <summary>获取“取消当前工作流”工作台命令的稳定身份。</summary>
    public static readonly CommandId CancelWorkflow =
        new("myavalonia.plugin.workflow-studio.command.cancel");

    /// <summary>获取验证命令在 Host Tools 共享菜单中的展示身份。</summary>
    public static readonly CommandPlacementId ValidateWorkflowMenu =
        new("myavalonia.plugin.workflow-studio.command-placement.menu.tools.validate");

    /// <summary>获取运行命令在 Host Tools 共享菜单中的展示身份。</summary>
    public static readonly CommandPlacementId RunWorkflowMenu =
        new("myavalonia.plugin.workflow-studio.command-placement.menu.tools.run");

    /// <summary>获取取消命令在 Host Tools 共享菜单中的展示身份。</summary>
    public static readonly CommandPlacementId CancelWorkflowMenu =
        new("myavalonia.plugin.workflow-studio.command-placement.menu.tools.cancel");

    /// <summary>获取验证命令的快捷键展示身份。</summary>
    public static readonly CommandPlacementId ValidateWorkflowKeyBinding =
        new("myavalonia.plugin.workflow-studio.command-placement.keybinding.validate");

    /// <summary>获取运行命令的快捷键展示身份。</summary>
    public static readonly CommandPlacementId RunWorkflowKeyBinding =
        new("myavalonia.plugin.workflow-studio.command-placement.keybinding.run");

    /// <summary>获取取消命令的快捷键展示身份。</summary>
    public static readonly CommandPlacementId CancelWorkflowKeyBinding =
        new("myavalonia.plugin.workflow-studio.command-placement.keybinding.cancel");
}
