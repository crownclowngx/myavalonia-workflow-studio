using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class WorkflowCatalogTests
{
    [Fact]
    public void 相同Descriptor不受Gateway顺序影响并产生相同Revision()
    {
        var gateway = new MutableGateway();
        var projection = new WorkflowActionCatalogProjection(gateway);
        var first = projection.Capture();
        gateway.Actions = gateway.Actions.Reverse().ToArray();

        var second = projection.Capture();

        Assert.Equal(first.Revision, second.Revision);
        Assert.StartsWith("sha256:", first.Revision, StringComparison.Ordinal);
        Assert.Equal(71, first.Revision.Length);
        Assert.Equal(TestActions.FormatId, first.Actions[0].Id.Value);
    }

    [Fact]
    public void 名称Schema或动作集合变化都会使Revision失效()
    {
        var gateway = new MutableGateway();
        var projection = new WorkflowActionCatalogProjection(gateway);
        var original = projection.Capture().Revision;

        gateway.Actions = [TestActions.Generate("已改名"), TestActions.Format()];
        var renamed = projection.Capture().Revision;
        gateway.Actions = [TestActions.Generate()];
        var removed = projection.Capture().Revision;

        Assert.NotEqual(original, renamed);
        Assert.NotEqual(renamed, removed);
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
