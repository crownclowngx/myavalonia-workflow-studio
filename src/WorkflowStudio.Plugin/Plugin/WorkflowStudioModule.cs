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
                "WorkflowStudio"));
    }
}
