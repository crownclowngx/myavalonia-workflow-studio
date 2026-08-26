using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Features.Main;
using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class MainDocumentTests : IDisposable
{
    private readonly MutableGateway _gateway = new();
    private readonly SessionSecretStore _secrets;
    private readonly TestLifetime _lifetime = new();
    private readonly MainDocument _document;

    public MainDocumentTests()
    {
        var services = TestServiceFactory.Create(_gateway);
        _secrets = services.Secrets;
        var editor = new WorkflowEditorCoordinator(
            services.Catalog, services.Codec, services.Validator, new WorkflowRiskSummaryBuilder());
        var runSession = new WorkflowRunSession(services.Runner, services.Secrets, _lifetime);
        _document = new MainDocument(editor, runSession, _lifetime);
    }

    [Fact]
    public async Task 初始化采用Host标题并投影可用Action()
    {
        var changed = 0;
        _document.PresentationChanged += (_, _) => changed++;

        await _document.InitializeAsync(new NewDocumentActivation("测试标题"), CancellationToken.None);

        Assert.Equal("测试标题", _document.Presentation.Title);
        Assert.Equal(1, changed);
        Assert.Equal(2, _document.AvailableActions.Count);
        Assert.NotNull(_document.SelectedAction);
    }

    [Fact]
    public async Task 演示定义可验证执行与导出且Secret不泄漏()
    {
        const string canary = "DOCUMENT-SECRET-CANARY";
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);
        _document.LoadDemonstrationWorkflow(canary);

        _document.ExportCommand.Execute(null);
        var result = await _document.RunCurrentAsync();

        Assert.True(_document.CanExecute);
        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Entries.Count);
        Assert.Contains("${secret.session-key}", _document.DefinitionJson, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, _document.DefinitionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 添加删除与排序命令只改变编辑状态()
    {
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);
        _document.AddStepCommand.Execute(null);
        _document.SelectedAction = _document.AvailableActions[1];
        _document.AddStepCommand.Execute(null);
        var selected = _document.SelectedStep;

        _document.MoveStepUpCommand.Execute(null);
        Assert.Same(selected, _document.Steps[0]);
        _document.MoveStepDownCommand.Execute(null);
        Assert.Same(selected, _document.Steps[1]);
        _document.RemoveStepCommand.Execute(null);

        Assert.Single(_document.Steps);
        Assert.False(_document.CanExecute);
    }

    [Fact]
    public async Task 严格导入恢复同一结构化模型而未知Action不覆盖现状()
    {
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);
        _document.LoadDemonstrationWorkflow("x");
        _document.ExportCommand.Execute(null);
        var exported = _document.DefinitionJson;
        _document.Steps.Clear();

        _document.ImportCommand.Execute(null);
        Assert.Equal(2, _document.Steps.Count);
        Assert.Equal("${generate.result.items}", _document.Steps[1].ForEach);
        var previousIds = _document.Steps.Select(item => item.Id).ToArray();

        _document.DefinitionJson = exported.Replace(
            TestActions.GenerateId,
            "myavalonia.plugin.unknown.workflow.none",
            StringComparison.Ordinal);
        _document.ImportCommand.Execute(null);
        Assert.Contains(_document.ValidationMessages, item => item.Code == "import.failed");
        Assert.Equal(previousIds, _document.Steps.Select(item => item.Id));
    }

    [Fact]
    public async Task Document关闭会取消运行清空定义与Secret()
    {
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);
        _document.LoadDemonstrationWorkflow("x");
        _document.ExportCommand.Execute(null);

        _lifetime.Close();

        Assert.Empty(_document.Steps);
        Assert.Empty(_document.DefinitionJson);
        Assert.False(_document.CanExecute);
        Assert.Empty(_secrets.Names);
    }

    [Fact]
    public async Task UI执行命令投影脱敏调用记录()
    {
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);
        _document.LoadDemonstrationWorkflow("UI-SECRET-MUST-NOT-APPEAR");

        await _document.RunCommand.ExecuteAsync(null);

        Assert.Equal("工作流执行成功。", _document.RunStatus);
        Assert.Equal(4, _document.RunMessages.Count);
        Assert.DoesNotContain(_document.RunMessages,
            item => item.Display.Contains("UI-SECRET-MUST-NOT-APPEAR", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _document.Dispose();
        _lifetime.Dispose();
    }
}
