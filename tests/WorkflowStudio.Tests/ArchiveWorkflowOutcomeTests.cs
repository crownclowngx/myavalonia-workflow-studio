using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class ArchiveWorkflowOutcomeTests
{
    private static WorkflowActionId Action => new("myavalonia.plugin.layer.unpack.workflow.create-v1");

    [Theory]
    [InlineData("completed", null)]
    [InlineData("skipped", null)]
    [InlineData("partial-failure", "archive.partial-failure")]
    [InlineData("failed", "archive.failed")]
    [InlineData("unknown", "archive.result-invalid")]
    public void 已知动作业务状态独立于SDK终态且诊断不透传正文(string state, string? expected)
    {
        var output = JsonSerializer.SerializeToElement(new { contract = "myavalonia.layer-unpack.workflow-result", version = 1, state, diagnosticCode = "private-sentinel" });
        var result = ArchiveWorkflowOutcome.Inspect(Action, output);
        Assert.Equal(expected, result?.Code);
        Assert.DoesNotContain("private-sentinel", result?.Message ?? "");
        Assert.Null(ArchiveWorkflowOutcome.Inspect(new("myavalonia.plugin.other.workflow.action"), output));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"contract\":\"myavalonia.layer-unpack.workflow-result\",\"version\":\"1\",\"state\":\"completed\"}")]
    [InlineData("{\"contract\":\"myavalonia.layer-unpack.workflow-result\",\"version\":2,\"state\":\"completed\"}")]
    public void 非法结果或新版本不被当成业务成功(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("archive.result-invalid", ArchiveWorkflowOutcome.Inspect(Action, doc.RootElement)?.Code);
    }

    [Theory]
    [InlineData("partial-failure", false)]
    [InlineData("failed", false)]
    [InlineData("completed", true)]
    [InlineData("skipped", true)]
    public async Task 业务失败仍允许成功引用及后续清理但最终摘要不谎报成功(string state, bool succeeded)
    {
        var original = TestActions.Generate();
        var gateway = new MutableGateway
        {
            Actions = [new(Action, original.DisplayName, original.Description, original.InputSchema, original.OutputSchema, original.Risks, original.ConfirmationPolicy), TestActions.Format()]
        };
        gateway.InvokeOverride = (request, _) => Task.FromResult(new WorkflowActionInvocationResult(Guid.NewGuid(), WorkflowActionInvocationStatus.Succeeded,
            request.ActionId == Action ? JsonSerializer.SerializeToElement(new { contract = "myavalonia.layer-unpack.workflow-result", version = 1, state, items = new[] { new { value = "success-only" } } }) :
                JsonSerializer.SerializeToElement(new { formatted = "done" }), null));
        var services = TestServiceFactory.Create(gateway);
        using var secrets = services.Secrets;
        secrets.Set("session-key", "session-only");
        var old = TestActions.ValidDefinition(services.Catalog.Capture());
        var steps = old.Steps.Select((s, i) => i == 0 ? new WorkflowStepDefinition(s.Id, Action, s.Arguments) : s).ToArray();
        var definition = new WorkflowDefinitionV2(2, old.ContractRevision, old.PresentationRevision, old.Summary, steps);
        var result = await services.Runner.RunAsync(definition, null, CancellationToken.None);
        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal("success-only", gateway.Requests[1].Arguments.GetProperty("value").GetString());
        Assert.Equal(1, gateway.RunsDisposed);
        Assert.Equal(succeeded ? null : "archive." + state, result.Entries[0].FailureCode);
    }
}
