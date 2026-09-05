using Avalonia.Controls;

namespace WorkflowStudio.Features.Main;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => AttachPicker();
        DataContextChanged += (_, _) => AttachPicker();
        DetachedFromVisualTree += (_, _) =>
        {
            if (DataContext is MainDocument { ArtWorkflow.Files: ArtWorkflowFilePicker picker }) picker.Attach(null);
        };
    }

    private void AttachPicker()
    {
        if (DataContext is MainDocument { ArtWorkflow.Files: ArtWorkflowFilePicker picker })
            picker.Attach(TopLevel.GetTopLevel(this)?.StorageProvider);
    }
}
