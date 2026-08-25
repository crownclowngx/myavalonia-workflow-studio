using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Constants;

public static class PluginIds
{
    public static readonly PluginId Plugin = new("myavalonia.plugin.workflow-studio");

    public static readonly DocumentTypeId StudioDocument =
        new("myavalonia.plugin.workflow-studio.document.studio");
}
