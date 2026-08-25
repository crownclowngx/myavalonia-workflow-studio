using System.Text;
using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class WorkflowDefinitionCodecTests
{
    private readonly WorkflowDefinitionCodec _codec = new();

    [Fact]
    public void 规范导出后可以严格往返且不展开Secret()
    {
        var definition = TestActions.ValidDefinition("sha256:" + new string('a', 64));

        var json = _codec.Serialize(definition);
        var roundTrip = _codec.Parse(json);

        Assert.Equal(1, roundTrip.SchemaVersion);
        Assert.Equal(2, roundTrip.Steps.Count);
        Assert.Contains("${secret.session-key}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("真实密码", json, StringComparison.Ordinal);
        Assert.True(json.IndexOf("schemaVersion", StringComparison.Ordinal) <
                    json.IndexOf("catalogRevision", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1,\"catalogRevision\":\"x\",\"summary\":\"x\",\"steps\":[],\"unknown\":1}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"catalogRevision\":\"x\",\"summary\":\"x\",\"steps\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"catalogRevision\":\"x\",\"summary\":\"x\",\"steps\":[{\"id\":\"a\",\"actionId\":\"myavalonia.plugin.p.workflow.a\",\"arguments\":{},\"extra\":1}]}")]
    [InlineData("{\"schemaVersion\":1,\"catalogRevision\":\"x\",\"summary\":\"x\",\"steps\":[{\"id\":\"a\",\"actionId\":\"myavalonia.plugin.p.workflow.a\",\"arguments\":{\"x\":1,\"x\":2}}]}")]
    public void 严格导入拒绝缺失未知与重复字段(string json) =>
        Assert.Throws<WorkflowDefinitionFormatException>(() => _codec.Parse(json));

    [Fact]
    public void 恶意深度和超大定义被安全拒绝()
    {
        var deep = "{\"schemaVersion\":1,\"catalogRevision\":\"x\",\"summary\":\"x\",\"steps\":[{\"id\":\"a\",\"actionId\":\"myavalonia.plugin.p.workflow.a\",\"arguments\":" +
                   new string('[', 40) + "0" + new string(']', 40) + "}]}";
        Assert.Throws<WorkflowDefinitionFormatException>(() => _codec.Parse(deep));

        var oversized = new string('x', 256 * 1024 + 1);
        var exception = Assert.Throws<WorkflowDefinitionFormatException>(() => _codec.Parse(oversized));
        Assert.DoesNotContain(oversized[..100], exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 非法ActionId不会把原始定义写入异常()
    {
        const string canary = "SECRET-CANARY-NOT-IN-ERROR";
        var json = "{\"schemaVersion\":1,\"catalogRevision\":\"x\",\"summary\":\"x\"," +
                   "\"steps\":[{\"id\":\"a\",\"actionId\":\"INVALID\",\"arguments\":{" +
                   "\"secret\":\"" + canary + "\"}}]}";

        var exception = Assert.Throws<WorkflowDefinitionFormatException>(() => _codec.Parse(json));

        Assert.DoesNotContain(canary, exception.Message, StringComparison.Ordinal);
    }
}
