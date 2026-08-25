using Avalonia;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Standalone;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--g3-self-test", StringComparer.Ordinal))
        {
            return RunSelfTestAsync().GetAwaiter().GetResult();
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// 门禁使用的无窗口闭环。成功输出只包含固定标记和计数，绝不打印定义、参数、输出或 Secret。
    /// </summary>
    private static async Task<int> RunSelfTestAsync()
    {
        const string secretCanary = "G3-SELF-TEST-SECRET-MUST-NOT-LEAK";
        try
        {
            using var composition = StandaloneComposition.Create();
            await composition.Document.InitializeAsync(
                new NewDocumentActivation("Workflow Studio G3 Self Test"),
                CancellationToken.None);
            composition.Document.LoadDemonstrationWorkflow(secretCanary);
            composition.Document.ExportCommand.Execute(parameter: null);
            if (composition.Document.DefinitionJson.Contains(secretCanary, StringComparison.Ordinal) ||
                !composition.Document.DefinitionJson.Contains("${secret.session-key}", StringComparison.Ordinal))
            {
                return 2;
            }
            var result = await composition.Document.RunCurrentAsync();
            if (!result.Succeeded || result.Entries.Count != 4 ||
                composition.Gateway.Invocations != 4)
            {
                return 3;
            }
            composition.Dispose();
            if (composition.Gateway.DisposedRuns != 1)
            {
                return 4;
            }
            Console.WriteLine("WORKFLOW_STUDIO_G3_SELF_TEST_OK invocations=4 disposedRuns=1");
            return 0;
        }
        catch (Exception exception)
        {
            // 异常类型足够定位组合问题；异常正文可能来自外部 Action，因此不写入门禁输出。
            Console.Error.WriteLine($"WORKFLOW_STUDIO_G3_SELF_TEST_FAILED type={exception.GetType().Name}");
            return 1;
        }
    }
}
