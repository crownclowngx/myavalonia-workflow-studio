using Avalonia.Controls;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Standalone;

public sealed partial class MainWindow : Window
{
    private readonly StandaloneComposition _composition;

    public MainWindow()
    {
        InitializeComponent();
        _composition = StandaloneComposition.Create();
        _composition.Document.InitializeAsync(
            new NewDocumentActivation("MyAvalonia Workflow Studio · Standalone"),
            CancellationToken.None).GetAwaiter().GetResult();
        _composition.Document.LoadDemonstrationWorkflow("standalone-session-secret");
        DataContext = _composition.Document;
        Closed += (_, _) => _composition.Dispose();
    }
}
