using Avalonia.Input;
using MyAvaloniaManagement.PluginSdk.UI;
using WorkflowStudio.Constants;
using WorkflowStudio.Features.Main;

namespace WorkflowStudio.Plugin;

public sealed class WorkflowStudioModule : IPluginModule
{
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        registration.Services.AddWorkflowStudioServices();
        registration.UseWorkflowActionGateway();
        registration.AddDocument<MainDocument, MainView>(
            new DocumentDescriptor(
                PluginIds.StudioDocument,
                "Workflow Studio",
                "临时编辑、验证并执行受 Host 治理的 Workflow Action",
                "工作流插件"));

        // 这里仅声明稳定身份、展示文本和目标 Document 类型。注册表不能保存 MainDocument、
        // ICommand、回调或 Provider；Host 在执行瞬间才把 CommandId 路由到当前活动实例。
        registration.AddDocumentCommand(
            new CommandDescriptor(
                PluginIds.ValidateWorkflow,
                "验证当前工作流",
                "使用当前 Action 目录验证活动 Workflow Studio 文档。"),
            PluginIds.StudioDocument);
        registration.AddDocumentCommand(
            new CommandDescriptor(
                PluginIds.RunWorkflow,
                "运行当前工作流",
                "通过既有 WorkflowRunSession 和受治理 Gateway 运行活动工作流。"),
            PluginIds.StudioDocument);
        registration.AddDocumentCommand(
            new CommandDescriptor(
                PluginIds.CancelWorkflow,
                "取消当前工作流",
                "协作取消活动 Workflow Studio 文档中正在运行的工作流。"),
            PluginIds.StudioDocument);

        // 三项使用同一稳定分组和明确顺序。目标不是 Studio Document 时采用 Hide，避免把只对
        // 当前编辑器有意义的操作展示成全局可用行为；顶级菜单和 Avalonia 控件仍完全由 Host 拥有。
        registration.AddMenuCommandContribution(
            new MenuCommandContributionDescriptor(
                PluginIds.ValidateWorkflowMenu,
                PluginIds.ValidateWorkflow,
                WorkbenchMenuLocations.ToolsShared,
                group: "workflow",
                order: 0,
                targetUnavailableBehavior: MenuCommandTargetUnavailableBehavior.Hide));
        registration.AddMenuCommandContribution(
            new MenuCommandContributionDescriptor(
                PluginIds.RunWorkflowMenu,
                PluginIds.RunWorkflow,
                WorkbenchMenuLocations.ToolsShared,
                group: "workflow",
                order: 10,
                targetUnavailableBehavior: MenuCommandTargetUnavailableBehavior.Hide));
        registration.AddMenuCommandContribution(
            new MenuCommandContributionDescriptor(
                PluginIds.CancelWorkflowMenu,
                PluginIds.CancelWorkflow,
                WorkbenchMenuLocations.ToolsShared,
                group: "workflow",
                order: 20,
                targetUnavailableBehavior: MenuCommandTargetUnavailableBehavior.Hide));

        // F5 / Shift+F5 沿用桌面工具“运行/停止”的常见心智模型，F6 专用于验证。
        // Descriptor 保存强类型枚举，不解析字符串 Gesture，也不创建 Host KeyBinding。
        registration.AddKeyBindingContribution(
            new KeyBindingContributionDescriptor(
                PluginIds.ValidateWorkflowKeyBinding,
                PluginIds.ValidateWorkflow,
                Key.F6,
                KeyModifiers.None));
        registration.AddKeyBindingContribution(
            new KeyBindingContributionDescriptor(
                PluginIds.RunWorkflowKeyBinding,
                PluginIds.RunWorkflow,
                Key.F5,
                KeyModifiers.None));
        registration.AddKeyBindingContribution(
            new KeyBindingContributionDescriptor(
                PluginIds.CancelWorkflowKeyBinding,
                PluginIds.CancelWorkflow,
                Key.F5,
                KeyModifiers.Shift));
    }
}
