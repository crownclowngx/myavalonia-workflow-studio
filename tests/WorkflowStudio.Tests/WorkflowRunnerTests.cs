using System.Collections.Concurrent;
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class WorkflowRunnerTests : IDisposable
{
    private readonly MutableGateway _gateway = new();
    private readonly WorkflowActionCatalogProjection _catalog;
    private readonly WorkflowDefinitionValidator _validator;
    private readonly WorkflowReferenceResolver _resolver;
    private readonly WorkflowRunner _runner;
    private readonly SessionSecretStore _secrets;

    public WorkflowRunnerTests()
    {
        var services = TestServiceFactory.Create(_gateway);
        _catalog = services.Catalog;
        _validator = services.Validator;
        _resolver = services.Resolver;
        _runner = services.Runner;
        _secrets = services.Secrets;
        _secrets.Set("session-key", "TOP-SECRET-CANARY");
    }

    [Fact]
    public async Task Sequence与ForEach严格顺序执行并只创建一个Run()
    {
        var catalog = _catalog.Capture();

        var result = await _runner.RunAsync(
            TestActions.ValidDefinition(catalog.Revision), progress: null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(["generate", "format", "format"], result.Entries.Select(item => item.StepId));
        Assert.Equal([null, 0, 1], result.Entries.Select(item => item.ItemIndex));
        Assert.Equal(1, _gateway.RunsCreated);
        Assert.Equal(1, _gateway.RunsDisposed);
        Assert.Equal("item1", _gateway.Requests[1].Arguments.GetProperty("value").GetString());
        Assert.Equal("TOP-SECRET-CANARY", _gateway.Requests[1].Arguments.GetProperty("secret").GetString());
        Assert.DoesNotContain(result.Entries, item =>
            item.FailureMessage?.Contains("TOP-SECRET-CANARY", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task 第一次失败后立即停止且释放Run()
    {
        _gateway.InvokeOverride = (request, _) => Task.FromResult(
            new WorkflowActionInvocationResult(
                Guid.NewGuid(),
                WorkflowActionInvocationStatus.Failed,
                output: null,
                new WorkflowActionFailure("test.failed", "脱敏失败")));
        var definition = TestActions.ValidDefinition(_catalog.Capture().Revision);

        var result = await _runner.RunAsync(definition, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Single(result.Entries);
        Assert.Equal("test.failed", result.Entries[0].FailureCode);
        Assert.Single(_gateway.Requests);
        Assert.Equal(1, _gateway.RunsDisposed);
    }

    [Fact]
    public async Task 用户取消在途调用后返回取消终态并释放Run()
    {
        _gateway.InvokeOverride = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("不可达");
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await _runner.RunAsync(
            TestActions.ValidDefinition(_catalog.Capture().Revision), null, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Equal(1, _gateway.RunsDisposed);
    }

    [Fact]
    public async Task 执行前目录变化会拒绝且不会创建Run()
    {
        var definition = TestActions.ValidDefinition(_catalog.Capture().Revision);
        _gateway.Actions = [TestActions.Generate("变化"), TestActions.Format()];

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            _runner.RunAsync(definition, null, CancellationToken.None));

        Assert.Contains(exception.Result.Issues, item => item.Code == "catalog.stale");
        Assert.Equal(0, _gateway.RunsCreated);
    }

    [Fact]
    public async Task 引用解析后的Schema错误仍会释放Run()
    {
        _gateway.InvokeOverride = (request, _) =>
        {
            var output = request.ActionId.Value == TestActions.GenerateId
                ? JsonSerializer.SerializeToElement(new { items = new[] { new { value = 123 } } })
                : JsonSerializer.SerializeToElement(new { formatted = "x" });
            return Task.FromResult(new WorkflowActionInvocationResult(
                Guid.NewGuid(), WorkflowActionInvocationStatus.Succeeded, output, null));
        };

        await Assert.ThrowsAsync<WorkflowValidationException>(() => _runner.RunAsync(
            TestActions.ValidDefinition(_catalog.Capture().Revision), null, CancellationToken.None));

        Assert.Equal(1, _gateway.RunsDisposed);
        Assert.Single(_gateway.Requests, item => item.ActionId.Value == TestActions.GenerateId);
    }

    [Fact]
    public async Task 受控进度投影包含步骤和ForEach索引()
    {
        var observed = new ConcurrentQueue<WorkflowRunProgress>();
        var progress = new InlineProgress<WorkflowRunProgress>(observed.Enqueue);

        var result = await _runner.RunAsync(
            TestActions.ValidDefinition(_catalog.Capture().Revision), progress, CancellationToken.None);
        await Task.Delay(50);

        Assert.True(result.Succeeded);
        Assert.Contains(observed, item => item.StepId == "generate" && item.ItemIndex is null);
        Assert.Contains(observed, item => item.StepId == "format" && item.ItemIndex == 0);
    }

    [Fact]
    public void 引用解析支持嵌套对象数组Item与Secret且拒绝不存在路径()
    {
        var outputs = new Dictionary<string, JsonElement>
        {
            ["previous"] = JsonSerializer.SerializeToElement(new { nested = new { values = new[] { "a", "b" } } })
        };
        var arguments = JsonSerializer.SerializeToElement(new
        {
            fromStep = "${previous.result.nested.values.1}",
            fromItem = "${item.value}",
            secret = "${secret.session-key}",
            array = new[] { "${previous.result.nested.values.0}" }
        });

        var resolved = _resolver.ResolveArguments(
            arguments,
            outputs,
            JsonSerializer.SerializeToElement(new { value = "current" }));

        Assert.Equal("b", resolved.GetProperty("fromStep").GetString());
        Assert.Equal("current", resolved.GetProperty("fromItem").GetString());
        Assert.Equal("TOP-SECRET-CANARY", resolved.GetProperty("secret").GetString());
        Assert.Throws<InvalidOperationException>(() =>
            _resolver.ResolveToken("${previous.result.missing}", outputs, null));
        Assert.Throws<InvalidOperationException>(() =>
            _resolver.ResolveToken("not-a-token", outputs, null));
    }

    public void Dispose() => _secrets.Dispose();

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
