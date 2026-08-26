using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class WorkflowDefinitionCodecTests
{
    private readonly WorkflowDefinitionCodec _codec = new();

    [Fact]
    public void 规范v2导出可严格往返且不展开Secret()
    {
        var revision = "sha256:" + new string('a', 64);
        var definition = new WorkflowDefinitionV2(2, revision, revision, "测试",
        [
            new("step", new("myavalonia.plugin.p.workflow.a"),
                JsonSerializer.SerializeToElement(new { secret = "${secret.session-key}" }))
        ]);

        var json = _codec.Serialize(definition);
        var roundTrip = _codec.Parse(json);

        Assert.Equal(2, roundTrip.SchemaVersion);
        Assert.Equal(revision, roundTrip.ContractRevision);
        Assert.Equal(revision, roundTrip.PresentationRevision);
        Assert.Contains("${secret.session-key}", json, StringComparison.Ordinal);
        Assert.True(json.IndexOf("contractRevision", StringComparison.Ordinal) <
                    json.IndexOf("presentationRevision", StringComparison.Ordinal));
    }

    [Fact]
    public void 规范导出保留嵌套数组数字布尔与Null常量()
    {
        using var arguments = JsonDocument.Parse(
            """{"z":[true,false,null,1,1.25,{"b":"x","a":2}],"a":{"value":"ok"}}""");
        var definition = new WorkflowDefinitionV2(2, "c", "p", "复杂常量",
        [
            new("step", new("myavalonia.plugin.p.workflow.a"), arguments.RootElement)
        ]);

        var json = _codec.Serialize(definition);
        var roundTrip = _codec.Parse(json);

        Assert.True(JsonElement.DeepEquals(arguments.RootElement, roundTrip.Steps[0].Arguments));
        Assert.True(json.IndexOf("\"a\"", StringComparison.Ordinal) <
                    json.IndexOf("\"z\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1,\"catalogRevision\":\"x\",\"summary\":\"x\",\"steps\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"contractRevision\":\"x\",\"presentationRevision\":\"x\",\"summary\":\"x\",\"steps\":[],\"unknown\":1}")]
    [InlineData("{\"schemaVersion\":2,\"schemaVersion\":2,\"contractRevision\":\"x\",\"presentationRevision\":\"x\",\"summary\":\"x\",\"steps\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"contractRevision\":\"x\",\"presentationRevision\":\"x\",\"summary\":\"x\",\"steps\":[{\"id\":\"a\",\"actionId\":\"myavalonia.plugin.p.workflow.a\",\"arguments\":{\"x\":1,\"x\":2}}]}")]
    public void 严格导入拒绝v1缺失未知与重复字段(string json) =>
        Assert.Throws<WorkflowDefinitionFormatException>(() => _codec.Parse(json));

    [Fact]
    public void 恶意深度超大定义和非法Id均脱敏拒绝()
    {
        var deep = "{\"schemaVersion\":2,\"contractRevision\":\"x\",\"presentationRevision\":\"x\",\"summary\":\"x\",\"steps\":[{\"id\":\"a\",\"actionId\":\"myavalonia.plugin.p.workflow.a\",\"arguments\":" +
                   new string('[', 40) + "0" + new string(']', 40) + "}]}";
        Assert.Throws<WorkflowDefinitionFormatException>(() => _codec.Parse(deep));

        var oversized = new string('x', 256 * 1024 + 1);
        var oversizedException = Assert.Throws<WorkflowDefinitionFormatException>(() => _codec.Parse(oversized));
        Assert.DoesNotContain(oversized[..100], oversizedException.Message, StringComparison.Ordinal);

        const string canary = "SECRET-CANARY-NOT-IN-ERROR";
        var invalid = "{\"schemaVersion\":2,\"contractRevision\":\"x\",\"presentationRevision\":\"x\",\"summary\":\"x\"," +
                      "\"steps\":[{\"id\":\"a\",\"actionId\":\"INVALID\",\"arguments\":{" +
                      "\"secret\":\"" + canary + "\"}}]}";
        var exception = Assert.Throws<WorkflowDefinitionFormatException>(() => _codec.Parse(invalid));
        Assert.DoesNotContain(canary, exception.Message, StringComparison.Ordinal);
    }
}
