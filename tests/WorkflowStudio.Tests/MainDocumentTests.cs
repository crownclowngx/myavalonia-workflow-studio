using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Constants;
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
    private readonly IWorkbenchDocumentCommandTarget _target;

    public MainDocumentTests()
    {
        var services = TestServiceFactory.Create(_gateway);
        _secrets = services.Secrets;
        var editor = new WorkflowEditorCoordinator(
            services.Catalog, services.Codec, services.Validator, new WorkflowRiskSummaryBuilder());
        var runSession = new WorkflowRunSession(services.Runner, services.Secrets, _lifetime);
        _document = new MainDocument(editor, runSession, _lifetime);
        _target = _document;
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

    [Fact]
    public async Task 工作台命令按当前Document验证状态启用并拒绝未知身份()
    {
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);

        Assert.True(_target.CanExecute(PluginIds.ValidateWorkflow));
        Assert.False(_target.CanExecute(PluginIds.RunWorkflow));
        Assert.False(_target.CanExecute(PluginIds.CancelWorkflow));
        Assert.False(_target.CanExecute(new CommandId("myavalonia.plugin.workflow-studio.command.unknown")));

        _document.LoadDemonstrationWorkflow("x");
        await _target.ExecuteAsync(PluginIds.ValidateWorkflow, CancellationToken.None);

        Assert.True(_target.CanExecute(PluginIds.ValidateWorkflow));
        Assert.True(_target.CanExecute(PluginIds.RunWorkflow));
        Assert.False(_target.CanExecute(PluginIds.CancelWorkflow));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _target.ExecuteAsync(
                    new CommandId("myavalonia.plugin.workflow-studio.command.unknown"),
                    CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public async Task Secret命令成功后清空输入且非法名称只产生脱敏验证错误()
    {
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);
        _document.SecretName = "valid-secret";
        _document.SecretValue = "secret-value";

        _document.StoreSecretCommand.Execute(null);

        Assert.Empty(_document.SecretValue);
        Assert.Contains("valid-secret", _secrets.Names);

        _document.SecretName = "invalid secret name";
        _document.SecretValue = "must-not-appear";
        _document.StoreSecretCommand.Execute(null);

        Assert.Contains(_document.ValidationMessages, item => item.Code == "secret.name");
        Assert.DoesNotContain(
            _document.ValidationMessages,
            item => item.Message.Contains("must-not-appear", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 工作台Run真实等待并允许Cancel协作结束同一运行()
    {
        var invocationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.InvokeOverride = async (_, cancellationToken) =>
        {
            invocationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("取消后不应继续产生结果。");
        };
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);
        _document.LoadDemonstrationWorkflow("x");
        var changed = new List<CommandId>();
        _target.CommandStateChanged += (_, args) => changed.Add(args.CommandId);

        var running = _target.ExecuteAsync(PluginIds.RunWorkflow, CancellationToken.None).AsTask();
        await invocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_document.IsRunning);
        Assert.False(_target.CanExecute(PluginIds.ValidateWorkflow));
        Assert.False(_target.CanExecute(PluginIds.RunWorkflow));
        Assert.True(_target.CanExecute(PluginIds.CancelWorkflow));
        Assert.False(running.IsCompleted);

        await _target.ExecuteAsync(PluginIds.CancelWorkflow, CancellationToken.None);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(_document.IsRunning);
        Assert.Equal("工作流已取消。", _document.RunStatus);
        Assert.True(_target.CanExecute(PluginIds.ValidateWorkflow));
        Assert.True(_target.CanExecute(PluginIds.RunWorkflow));
        Assert.False(_target.CanExecute(PluginIds.CancelWorkflow));
        Assert.Contains(PluginIds.ValidateWorkflow, changed);
        Assert.Contains(PluginIds.RunWorkflow, changed);
        Assert.Contains(PluginIds.CancelWorkflow, changed);
    }

    [Fact]
    public async Task 工作台命令观察预取消且Document关闭后全部失败关闭()
    {
        await _document.InitializeAsync(new NewDocumentActivation("测试"), CancellationToken.None);
        _document.LoadDemonstrationWorkflow("x");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _target.ExecuteAsync(PluginIds.RunWorkflow, cancelled.Token).AsTask());

        _lifetime.Close();

        Assert.False(_target.CanExecute(PluginIds.ValidateWorkflow));
        Assert.False(_target.CanExecute(PluginIds.RunWorkflow));
        Assert.False(_target.CanExecute(PluginIds.CancelWorkflow));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _target.ExecuteAsync(PluginIds.ValidateWorkflow, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task 两个StudioDocument的运行和取消状态彼此隔离()
    {
        await _document.InitializeAsync(new NewDocumentActivation("A"), CancellationToken.None);
        _document.LoadDemonstrationWorkflow("a");
        var secondGateway = new MutableGateway();
        var secondServices = TestServiceFactory.Create(secondGateway);
        using var secondLifetime = new TestLifetime();
        using var second = new MainDocument(
            new WorkflowEditorCoordinator(
                secondServices.Catalog,
                secondServices.Codec,
                secondServices.Validator,
                new WorkflowRiskSummaryBuilder()),
            new WorkflowRunSession(secondServices.Runner, secondServices.Secrets, secondLifetime),
            secondLifetime);
        await second.InitializeAsync(new NewDocumentActivation("B"), CancellationToken.None);
        second.LoadDemonstrationWorkflow("b");
        var secondTarget = (IWorkbenchDocumentCommandTarget)second;
        var invocationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.InvokeOverride = async (_, cancellationToken) =>
        {
            invocationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("取消后不应继续产生结果。");
        };

        var firstRun = _target.ExecuteAsync(PluginIds.RunWorkflow, CancellationToken.None).AsTask();
        await invocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_target.CanExecute(PluginIds.CancelWorkflow));
        Assert.False(_target.CanExecute(PluginIds.RunWorkflow));
        Assert.False(secondTarget.CanExecute(PluginIds.CancelWorkflow));
        Assert.True(secondTarget.CanExecute(PluginIds.RunWorkflow));

        await _target.ExecuteAsync(PluginIds.CancelWorkflow, CancellationToken.None);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(secondTarget.CanExecute(PluginIds.RunWorkflow));
        Assert.False(secondTarget.CanExecute(PluginIds.CancelWorkflow));
    }

    public void Dispose()
    {
        _document.Dispose();
        _lifetime.Dispose();
    }
}
