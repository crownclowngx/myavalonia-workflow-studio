using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class WorkflowCatalogTests
{
    [Fact]
    public void 相同Descriptor不受Gateway顺序影响并产生双Revision()
    {
        var gateway = new MutableGateway();
        var projection = new WorkflowActionCatalogProjection(gateway);
        var first = projection.Capture();
        gateway.Actions = gateway.Actions.Reverse().ToArray();

        var second = projection.Capture();

        Assert.Equal(first.ContractRevision, second.ContractRevision);
        Assert.Equal(first.PresentationRevision, second.PresentationRevision);
        Assert.StartsWith("sha256:", first.ContractRevision, StringComparison.Ordinal);
        Assert.Equal(71, first.ContractRevision.Length);
        Assert.Equal(TestActions.FormatId, first.Actions[0].Id.Value);
    }

    [Fact]
    public void 改名只改变展示而Schema或动作集合改变契约()
    {
        var gateway = new MutableGateway();
        var projection = new WorkflowActionCatalogProjection(gateway);
        var original = projection.Capture();

        gateway.Actions = [TestActions.Generate("已改名"), TestActions.Format()];
        var renamed = projection.Capture();
        Assert.Equal(original.ContractRevision, renamed.ContractRevision);
        Assert.NotEqual(original.PresentationRevision, renamed.PresentationRevision);

        gateway.Actions = [TestActions.Generate()];
        var removed = projection.Capture();
        Assert.NotEqual(renamed.ContractRevision, removed.ContractRevision);
    }

    [Fact]
    public void 快照可按稳定ActionId查找且未知值返回False()
    {
        var snapshot = new WorkflowActionCatalogProjection(new MutableGateway()).Capture();

        Assert.True(snapshot.TryGet(new(TestActions.FormatId), out var descriptor));
        Assert.Equal("格式化", descriptor!.DisplayName);
        Assert.False(snapshot.TryGet(new("myavalonia.plugin.unknown.workflow.none"), out _));
    }
}
